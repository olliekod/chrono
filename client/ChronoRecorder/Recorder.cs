using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ChronoRecorder
{
    /// <summary>
    /// Records one monitor with a single long-running FFmpeg process that writes rolling segments,
    /// and saves clips by joining the newest segments.
    /// </summary>
    public class Recorder : IRecorder, IDiagnosticsSource
    {
        private readonly RecorderConfig config;
        private readonly SegmentTracker tracker;

        // Everything about the running FFmpeg process is guarded by processLock. FFmpeg's exit event, the monitor
        // timer and the UI thread (hotkey, buttons) all get here.
        private readonly object processLock = new object();
        private Process? ffmpegProcess;
        private volatile bool isRecording = false;
        private string? currentRun;
        private readonly Queue<DateTime> unexpectedExits = new Queue<DateTime>();

        private static int SegmentSeconds => RecorderConfig.SegmentSeconds;
        private string? resolvedEncoder;
        private List<AudioCapture> audioCaptures = new List<AudioCapture>();
        private Size nativeSize;   // the monitor's size, which is what is recorded

        private System.Threading.Timer? monitorTimer;
        private System.Threading.Timer? pruneTimer;
        private int monitorBusy;   // 1 while a monitor tick is running, so ticks can't overlap
        private string currentApplication = "";

        // Auto mode: the game found by the scanner, and when to look again.
        private readonly ProcessScanner scanner = new ProcessScanner();

        // Games are captured as their own window. If that can't start for a game, the monitor is used instead, but only while
        // the game is in front (see GameCapturePlanner); these track that.
        private bool windowCaptureFailed;
        private bool captureNeedsGameInFront;

        // Screen capture may only run while the game is in front, and "in front" changes in an instant when you alt-tab. The
        // once-a-second monitor tick let a couple of seconds of whatever you switched to into the clip, so while screen
        // capture is running the foreground is checked several times a second (it is one cheap lookup), and the last
        // moments before a pause are cut off, since the switch happened somewhere between the last check and the stop.
        private System.Threading.Timer? focusTimer;
        private int focusBusy;
        private const int FocusPollMilliseconds = 100;
        private const double FocusLossGuardSeconds = 0.8;
        private bool loggedWaiting;

        // FFmpeg was killed on purpose to reopen the sound devices, not because the capture broke.
        private bool killedForAudio;

        // Clips being saved right now. The buffer is not pruned while any is in flight: the export reads those very
        // files, and deleting one halfway through would truncate or fail the clip.
        private int exportsInFlight;

        // How hard recording is working: 0 normal, 1 lighter encoder preset, 2 lighter preset and at most 30 FPS (see LoadPlan).
        // Chosen when a recording starts, and moved down by the governor if this PC can't keep up.
        private int loadLevel;
        private LoadGovernor? governor;
        private bool suggestedLowerFps;   // the "can't keep up at this frame rate" warning is shown once until settings change

        // What the Diagnostics page shows about the recording in progress.
        private CaptureSource? currentSource;
        private int currentBitrateKbps;
        private DateTime startedUtc;
        private volatile EncodeStats? latestStats;
        private bool encoderFailed;            // the hardware encoder wouldn't start; the processor is used until Chrono restarts
        private string encoderNote = "";
        private readonly Queue<string> eventLog = new Queue<string>();
        private const int MaxEvents = 30;
        private RunningGame? autoGame;
        private DateTime nextGameScanUtc = DateTime.MinValue;
        private static readonly TimeSpan GameScanInterval = TimeSpan.FromSeconds(3);

        /// <summary>The game being recorded (or null). Shown by the UI and used to label clips.</summary>
        public string? CurrentGameName => config.Mode == RecorderConfig.RecordingMode.Auto ? autoGame?.Candidate.DisplayName
            : config.Mode == RecorderConfig.RecordingMode.Application && !string.IsNullOrWhiteSpace(config.SelectedApplication) ? config.SelectedApplication : null;
        public event EventHandler<string>? ApplicationChanged;

        /// <summary>Raised when recording stops for a reason the user should hear about. The text is fit to show.</summary>
        public event Action<string>? RecordingFailed;

        /// <summary>Something the user should know that doesn't stop recording, such as sound not being captured.</summary>
        public event Action<string>? Warning;

        /// <summary>Shrink saved clips on the NVIDIA GPU when possible. Off only to test the CPU fallback.</summary>
        public bool PreferGpuExport { get; set; } = true;

        public bool IsRecordingActive => isRecording;

        /// <summary>
        /// Is there anything to save? True while recording, and also while screen capture is paused because the game isn't in
        /// front (you alt-tabbed): what was recorded just before is still in the buffer, and that is the moment someone wants
        /// to clip.
        /// </summary>
        public bool HasBufferedFootage
        {
            get
            {
                if (isRecording) return true;
                try { return Directory.EnumerateFiles(config.TempFolder, "segment_*.ts").Any(); }
                catch { return false; }
            }
        }

        /// <summary>Size of the video being recorded, like "2560x1440". Null until recording has started once.</summary>
        public string? CaptureResolution { get; private set; }

        // A monitor change or a game switching resolution can end a Desktop Duplication session. Restart a few
        // times, but not forever: a crash loop would burn CPU for nothing.
        private const int MaxUnexpectedExits = 3;
        private static readonly TimeSpan UnexpectedExitWindow = TimeSpan.FromMinutes(2);

        // A capture that dies this soon after starting never worked (rather than broke midway).
        private static readonly TimeSpan StartupFailureWindow = TimeSpan.FromSeconds(5);

        public Recorder(RecorderConfig config)
        {
            this.config = config;
            Directory.CreateDirectory(config.TempFolder);
            DeleteStaleSegments();
            tracker = new SegmentTracker(config.TempFolder, SegmentSeconds, MediaProbe.DurationSeconds);
        }

        /// <summary>
        /// Segments left behind by a previous run of the app would look like footage from just now, so clear them.
        /// </summary>
        private void DeleteStaleSegments()
        {
            try
            {
                foreach (var file in Directory.GetFiles(config.TempFolder, "segment_*"))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Could not clean stale segments: {ex.Message}");
            }
        }

        /// <summary>
        /// start recording the game's monitor in the background
        /// </summary>
        public void StartRecording()
        {
            lock (processLock)
            {
                if (isRecording)
                {
                    Console.WriteLine("⚠ Already recording");
                    return;
                }

                try
                {
                    var source = ChooseSource();
                    if (source == null) return;   // nothing that is safe to record yet (the reason is logged once)

                    loggedWaiting = false;
                    Console.WriteLine($"▶️ STARTING RECORDING: {TargetName}");
                    LaunchFfmpeg(source);
                    Console.WriteLine("✓ Recording started");
                }
                catch (Win32Exception)
                {
                    isRecording = false;
                    Console.WriteLine("✗ FFmpeg was not found");
                    RecordingFailed?.Invoke("FFmpeg wasn't found. Install FFmpeg and make sure it is on your PATH.");
                }
                catch (Exception ex)
                {
                    isRecording = false;
                    Console.WriteLine($"✗ Failed to start recording: {ex.Message}");
                    RecordingFailed?.Invoke($"Couldn't start recording: {ex.Message}");
                }
            }
        }

        /// <summary>The game's window (even while minimized), or Zero when there is no game or its window isn't found.</summary>
        private IntPtr ResolveGameWindow()
        {
            switch (config.Mode)
            {
                case RecorderConfig.RecordingMode.Application:
                    return WindowDetector.FindApplicationWindow(config.SelectedApplication);
                case RecorderConfig.RecordingMode.Auto when autoGame != null:
                    var main = scanner.MainWindowOf(autoGame.Candidate.Pid);
                    return main != IntPtr.Zero ? main : scanner.WindowFor(autoGame.Candidate.Pid);
                default:
                    return IntPtr.Zero;
            }
        }

        /// <summary>Is the game the window you are looking at? Only matters when the monitor is what gets captured.</summary>
        private bool GameInFront()
        {
            uint foreground = WindowDetector.ProcessIdOf(WindowDetector.GetForegroundWindowHandle());
            if (foreground == 0) return false;

            return config.Mode switch
            {
                RecorderConfig.RecordingMode.Auto => autoGame != null && foreground == (uint)autoGame.Candidate.Pid,
                RecorderConfig.RecordingMode.Application => WindowDetector.NameMatches(WindowDetector.ProcessNameOf(foreground), config.SelectedApplication),
                _ => true
            };
        }

        /// <summary>
        /// What to capture. Whole-screen mode records the monitor, as asked. A game is recorded as its own window, so nothing
        /// else can end up in the clip, however it is covered or minimized; if that isn't possible the monitor is used only
        /// while the game is in front; and if there is no window at all, nothing is recorded rather than something else.
        /// </summary>
        private CaptureSource? ChooseSource()
        {
            captureNeedsGameInFront = false;
            MonitorLocator.Invalidate();   // what is about to be recorded is worth reading from the hardware

            if (config.Mode == RecorderConfig.RecordingMode.Display)
                return CaptureSourceChooser.Choose(PickMonitor(), PrimaryBounds());

            IntPtr window = ResolveGameWindow();
            var plan = GameCapturePlanner.Plan(config.GameCapture, windowCaptureFailed, window != IntPtr.Zero, WindowCaptureSupport.Borderless);
            var monitor = window == IntPtr.Zero ? null : MonitorLocator.ForWindow(window) ?? MonitorLocator.Primary();

            switch (plan)
            {
                case GameCapturePlan.Window:
                    return CaptureSourceChooser.ForWindow(window, monitor, PrimaryBounds());

                case GameCapturePlan.MonitorWhenInFront:
                    captureNeedsGameInFront = true;
                    if (!GameInFront())
                    {
                        if (!loggedWaiting) Console.WriteLine($"Not recording {TargetName} until it is in front (screen capture is only used while it is)");
                        loggedWaiting = true;
                        return null;
                    }
                    // The game is in front, so the screen it is on is the one the foreground window is on. That is more
                    // reliable than the game's "main window", which can be a launcher or a hidden window on another screen.
                    var front = MonitorLocator.ForWindow(WindowDetector.GetForegroundWindowHandle());
                    return CaptureSourceChooser.Choose(front ?? monitor, PrimaryBounds());

                default:
                    if (!loggedWaiting) Console.WriteLine($"Not recording {TargetName}: its window wasn't found");
                    loggedWaiting = true;
                    return null;
            }
        }

        /// <summary>
        /// The monitor to record: wherever the foreground window is. Recording starts when the tracked game gets
        /// focus, so that is the game's monitor.
        /// </summary>
        private MonitorInfo? PickMonitor()
        {
            // The game's own window, not whatever is in front: recording can start while it is tabbed out.
            IntPtr window = config.Mode switch
            {
                RecorderConfig.RecordingMode.Application => WindowDetector.FindApplicationWindow(config.SelectedApplication),
                RecorderConfig.RecordingMode.Auto => autoGame == null ? IntPtr.Zero : scanner.WindowFor(autoGame.Candidate.Pid),
                _ => IntPtr.Zero
            };

            if (window == IntPtr.Zero) window = WindowDetector.GetForegroundWindowHandle();
            return MonitorLocator.ForWindow(window) ?? MonitorLocator.Primary();
        }

        /// <summary>The size that is (or is about to be) recorded: the monitor being captured, else the one the game is on.</summary>
        public Size RecordingSize()
        {
            if (nativeSize.Width > 0 && nativeSize.Height > 0) return nativeSize;
            return PickMonitor()?.Pixels ?? PrimaryBounds().Size;
        }

        private static Rectangle PrimaryBounds()
            => MonitorLocator.Primary()?.Bounds
               ?? System.Windows.Forms.Screen.PrimaryScreen?.Bounds
               ?? new Rectangle(0, 0, 1920, 1080);

        /// <summary>
        /// stop recording
        /// </summary>
        public void StopRecording()
        {
            Process? process;
            List<AudioCapture> audio;
            lock (processLock)
            {
                // Clearing these first tells the exit handler this stop was deliberate.
                isRecording = false;
                process = ffmpegProcess;
                ffmpegProcess = null;

                // Take this recording's own sound sources now. Waiting for FFmpeg below can take seconds, and a new
                // recording may start in the meantime; disposing whatever was current by then would close the pipes
                // of the recording that had just started, leaving it silent.
                audio = audioCaptures;
                audioCaptures = new List<AudioCapture>();
            }

            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        // "q" lets FFmpeg finish and close the segment it is writing.
                        process.StandardInput.WriteLine("q");
                        if (!process.WaitForExit(4000)) process.Kill();
                    }
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }

            foreach (var capture in audio) capture.Dispose();
            StopFocusWatch();
            Console.WriteLine("✓ Recording stopped");
        }

        /// <summary>
        /// Open the sound sources for a recording. A source that can't be opened is skipped with a warning, so a
        /// missing microphone or an odd audio device costs the sound, not the recording.
        /// </summary>
        private List<AudioCapture> CreateAudioCaptures(double clockStart)
        {
            var captures = new List<AudioCapture>();
            if (!config.RecordAudio) return captures;

            var delay = TimeSpan.FromMilliseconds(config.AudioDelayMs);

            void Add(AudioSource source, string what, string? deviceId, double gain)
            {
                var capture = AudioCapture.TryCreate(source, clockStart, delay, deviceId, gain, out string? problem);
                if (capture == null)
                {
                    Console.WriteLine($"⚠ No {what}: {problem}");
                    Warning?.Invoke($"Recording without {what}: {problem}.");
                    return;
                }

                if (capture.FellBackToDefault) Warning?.Invoke($"The {what} you chose isn't available, so Windows' default is being used.");
                capture.Failed += reason => OnAudioFailed(capture, what, reason);
                captures.Add(capture);
            }

            Add(AudioSource.SystemSound, "game and system sound", config.SpeakerDeviceId, 1.0);
            if (config.RecordMicrophone) Add(AudioSource.Microphone, "microphone", config.MicrophoneDeviceId, Math.Clamp(config.MicrophoneVolumePercent, 0, 500) / 100.0);

            return captures;
        }

        /// <summary>A sound device went away mid-recording. Restarting reopens whatever is the default now.</summary>
        private void OnAudioFailed(AudioCapture capture, string what, string reason)
        {
            Process? process;
            lock (processLock)
            {
                if (!audioCaptures.Contains(capture) || !isRecording) return;
                process = ffmpegProcess;

                // Tell the exit handler why FFmpeg is about to die. Without this, a headset unplugged in the first
                // seconds of a recording looks like a capture that never started, and the game would be moved to
                // screen capture for no reason.
                killedForAudio = true;
            }

            Console.WriteLine($"⚠ {what} stopped ({reason}); restarting the recording");
            Warning?.Invoke($"The {what} device changed. Restarting the recording.");

            // Ending FFmpeg takes the normal "capture dropped" path, which relaunches with the current devices.
            try { process?.Kill(); } catch { }
        }

        private void DisposeAudio()
        {
            List<AudioCapture> old;
            lock (processLock)
            {
                old = audioCaptures;
                audioCaptures = new List<AudioCapture>();
            }
            foreach (var capture in old) capture.Dispose();
        }

        /// <summary>
        /// Start FFmpeg on a monitor. Caller holds processLock.
        /// </summary>
        private void LaunchFfmpeg(CaptureSource source)
        {
            Directory.CreateDirectory(config.TempFolder);

            string run = SegmentTracker.NewRunId();

            // Picture and sound are both timed from one instant, T0. Set before the audio sources exist.
            double clockStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            DisposeAudio();
            var audio = CreateAudioCaptures(clockStart);
            audioCaptures = audio;

            // A fixed setting decides the step outright. "Automatic" begins where the card and earlier runs say, and only ever
            // moves down within a session: coming back up would lose footage to try, and the governor found this step for a reason.
            bool automatic = LoadPlan.IsAutomatic(config.EncoderLoad);
            string encoder = ResolveEncoder();
            int startLevel = LoadPlan.StartLevel(config.EncoderLoad, CardTier, config.LearnedLoadLevel, config.Fps, LoadPlan.HasPresetStep(encoder));
            loadLevel = automatic ? Math.Max(loadLevel, startLevel) : startLevel;
            governor = automatic ? new LoadGovernor() : null;

            int fps = EffectiveFps;
            int bitrate = BitrateSizing.Resolve(config.Bitrate, source.Bounds.Size, fps);

            var request = new CaptureRequest(
                encoder, fps, bitrate, source,
                SegmentSeconds, SegmentTracker.NamePattern(config.TempFolder, run),
                Audio: audio.Select(a => a.Input).ToList(),
                // Always: the clock is what keeps a second of video a second of real time, sound or no sound.
                ClockStartUnixSeconds: clockStart,
                Load: LoadPlan.LoadFor(loadLevel));

            Note($"▶ Recording {source.Bounds.Width}x{source.Bounds.Height} at {fps} FPS via {source.Method} with {encoder} ({LoadPlan.Describe(loadLevel, config.Fps, LoadPlan.HasPresetStep(encoder)).ToLowerInvariant()} load)");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FfmpegLocator.Path,
                    Arguments = CaptureCommand.Build(request),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,   // -progress: a block of numbers every two seconds. It must be read, or FFmpeg would stall.
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            // FFmpeg is quiet at "warning" level, so what it does say is worth keeping for the failure message.
            var recentErrors = new Queue<string>();
            process.ErrorDataReceived += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                Console.WriteLine($"[ffmpeg] {e.Data}");
                lock (recentErrors)
                {
                    recentErrors.Enqueue(e.Data);
                    while (recentErrors.Count > 6) recentErrors.Dequeue();
                }
            };

            DateTime started = DateTime.UtcNow;
            var progress = new FfmpegProgressParser();
            process.OutputDataReceived += (s, e) => OnProgressLine(process, progress, e.Data, started);
            process.Exited += (s, e) => OnFfmpegExited(process, source, started, recentErrors);

            try
            {
                process.Start();
            }
            catch
            {
                DisposeAudio();
                throw;
            }
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            // FFmpeg opens the audio pipes as it starts; the captures wait for that and then feed them.
            foreach (var capture in audio) capture.Start();

            ffmpegProcess = process;
            currentRun = run;
            currentSource = source;
            currentBitrateKbps = bitrate;
            startedUtc = started;
            latestStats = null;
            nativeSize = source.Bounds.Size;
            CaptureResolution = CaptureSizing.Describe(nativeSize);
            isRecording = true;

            if (captureNeedsGameInFront) StartFocusWatch(); else StopFocusWatch();
        }

        private void StartFocusWatch()
        {
            if (focusTimer == null) focusTimer = new System.Threading.Timer(FocusTick, null, FocusPollMilliseconds, FocusPollMilliseconds);
        }

        private void StopFocusWatch()
        {
            var timer = focusTimer;
            focusTimer = null;
            timer?.Dispose();
        }

        private void FocusTick(object? state)
        {
            if (!isRecording || !captureNeedsGameInFront) return;
            if (System.Threading.Interlocked.Exchange(ref focusBusy, 1) == 1) return;   // a pause is already under way

            try
            {
                if (!GameInFront()) PauseBecauseGameLeftFront();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error watching the foreground window: {ex.Message}");
            }
            finally
            {
                System.Threading.Volatile.Write(ref focusBusy, 0);
            }
        }

        /// <summary>Stop screen capture the moment the game is no longer the window in front, and cut off what may have leaked.</summary>
        private void PauseBecauseGameLeftFront()
        {
            string? run = currentRun;
            Note($"⏸️ {TargetName} isn't in front: pausing screen capture so nothing else is recorded");
            StopRecording();
            if (run != null) TrimTail(run);
        }

        /// <summary>
        /// Remove the last moments of a run that was paused because the game lost the front. The switch happened somewhere
        /// between the last check and the stop, so those frames may already show what was switched to. The cut is a stream
        /// copy of one short file, so it takes a moment and loses nothing else.
        /// </summary>
        private void TrimTail(string run)
        {
            try
            {
                string? path = tracker.NewestFileOf(run);
                if (path == null) return;

                double? length = MediaProbe.DurationSeconds(path);
                if (length == null) return;

                double keep = length.Value - FocusLossGuardSeconds;
                if (keep < 0.5)
                {
                    File.Delete(path);   // nothing worth keeping: it would be almost all the guard
                    tracker.Forget(path);
                    return;
                }

                string temp = path + ".trim";
                var (ok, _) = FfmpegRunner.Run($"-hide_banner -loglevel error -i \"{path}\" -t {keep.ToString("F3", CultureInfo.InvariantCulture)} -c copy -f mpegts -y \"{temp}\"", 15_000);
                if (!ok) { try { File.Delete(temp); } catch { } return; }

                File.Move(temp, path, overwrite: true);
                tracker.Forget(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Couldn't trim the end of the footage: {ex.Message}");   // a save may be reading it; the leak is a fraction of a second
            }
        }

        /// <summary>One block of FFmpeg's progress report. Keeps the newest numbers, and lets the governor decide whether this PC is keeping up.</summary>
        private void OnProgressLine(Process process, FfmpegProgressParser parser, string? line, DateTime started)
        {
            var stats = parser.Feed(line, DateTime.UtcNow);
            if (stats == null || !ReferenceEquals(ffmpegProcess, process)) return;   // not a finished block, or a recording that has been replaced

            latestStats = stats;

            var watching = governor;
            if (watching == null) return;

            bool presetStep = LoadPlan.HasPresetStep(ResolveEncoder());
            int highest = LoadPlan.HighestUsefulLevel(config.Fps, presetStep, LoadPlan.MayLowerFrameRate(config.EncoderLoad));
            var verdict = watching.Evaluate(stats, started, loadLevel, highest);

            if (verdict.StepDown) { StepDownLoad(verdict.Reason); return; }

            // Out of steps the governor may take, but still behind: the frame rate is the user's setting, so say so once.
            if (verdict.BehindAtLimit && config.Fps > 30 && !suggestedLowerFps)
            {
                suggestedLowerFps = true;
                string text = $"This PC can't keep up with recording at {config.Fps} FPS: {verdict.Reason}. Choose 30 FPS under Frame rate in Settings, or set Recording load to \"Automatic, and lower the frame rate if needed\".";
                Note("⚠ " + text);
                Warning?.Invoke(text);
            }
        }

        /// <summary>This PC can't keep up: remember the next lighter step, tell the user, and let the monitor restart the recording with it.</summary>
        private void StepDownLoad(string reason)
        {
            loadLevel = LoadPlan.NextLevel(loadLevel, LoadPlan.HasPresetStep(ResolveEncoder()));
            config.LearnedLoadLevel = loadLevel;
            try { ConfigManager.Save(config); } catch { /* remembering it is a convenience */ }

            string now = LoadPlan.Describe(loadLevel, config.Fps, LoadPlan.HasPresetStep(ResolveEncoder()));
            Note($"⚠ This PC couldn't keep up ({reason}). Recording load is now: {now}.");
            Warning?.Invoke($"This PC couldn't keep up with recording ({reason}), so Chrono lowered how hard it works. Recording load is now: {now}.");

            // Stopping waits on FFmpeg, and this runs on the thread that reads FFmpeg's output, so hand it to another one.
            // The monitor starts the recording again within a second, at the new step.
            Task.Run(StopRecording);
        }

        /// <summary>Extra silence around the chime's own length, to cover WASAPI's startup latency (the sound isn't
        /// actually audible for a beat after Play() is called) and leave its tail a clean decay instead of a hard cut.</summary>
        private static readonly TimeSpan ClipCueMuteMargin = TimeSpan.FromMilliseconds(150);

        /// <summary>
        /// Keeps the clip-save chime (or, in Minion mode, its replacement) out of the recording: mutes the
        /// system-sound capture - never the microphone, which never picks it up directly - for <paramref name="soundDuration"/>
        /// plus a margin, starting now. Does nothing if nothing is recording, or system sound isn't being captured.
        /// The sound itself still plays normally; only what gets written to the recording is affected.
        /// </summary>
        public void MuteForClipCue(TimeSpan soundDuration)
        {
            List<AudioCapture> audio;
            lock (processLock) audio = audioCaptures.ToList();

            foreach (var capture in audio)
                if (capture.Source == AudioSource.SystemSound) capture.MuteBriefly(soundDuration + ClipCueMuteMargin);
        }

        /// <summary>Everything the Diagnostics page shows about the recording.</summary>
        public RecorderFacts GetFacts()
        {
            var source = currentSource;
            bool recording = isRecording;

            double buffer = 0;
            try
            {
                var scan = tracker.Scan(recording, currentRun);
                buffer = scan.Finished.Sum(f => f.DurationSeconds);
                if (scan.LivePath != null)
                    buffer += Math.Clamp((DateTime.Now - File.GetCreationTime(scan.LivePath)).TotalSeconds, 0, SegmentSeconds);
            }
            catch { /* the folder can change while it is read */ }

            string[] events;
            lock (eventLog) events = eventLog.ToArray();

            MonitorInfo? screen = recording && source != null ? MonitorLocator.Enumerate().FirstOrDefault(m => m.Bounds == source.Bounds) : null;
            int pid = 0;
            try { pid = recording ? ffmpegProcess?.Id ?? 0 : 0; } catch { /* it ended a moment ago */ }

            List<AudioCapture> audio;
            lock (processLock) audio = audioCaptures.ToList();

            return new RecorderFacts(
                recording, TargetName, recording ? source?.Method : null, recording ? nativeSize : Size.Empty,
                config.Fps, EffectiveFps, EncoderForDisplay(),
                loadLevel, (config.EncoderLoad ?? "auto").ToLowerInvariant(), governor != null, currentBitrateKbps, buffer, pid,
                recording ? startedUtc : null, recording ? latestStats : null, encoderNote,
                audio.Select(a => a.Source == AudioSource.SystemSound ? "game sound" : "microphone").ToList(),
                events, screen?.AdapterName ?? "", screen?.AdapterVendorId ?? 0,
                config.GameCapture ?? "auto", WindowCaptureSupport.Borderless);
        }

        /// <summary>The encoder for display. It never starts the detection (which runs FFmpeg), so an idle Chrono shows what it has learned so far.</summary>
        private string EncoderForDisplay()
        {
            if (encoderFailed) return "libx264";
            if (!string.IsNullOrWhiteSpace(config.Encoder) && !config.Encoder.Equals("auto", StringComparison.OrdinalIgnoreCase)) return config.Encoder;
            return resolvedEncoder ?? GpuDetector.Detected?.Encoder ?? "libx264";
        }

        /// <summary>
        /// FFmpeg ended. If we didn't ask it to, work out whether to retry, fall back, or give up.
        /// </summary>
        private void OnFfmpegExited(Process process, CaptureSource source, DateTime started, Queue<string> recentErrors)
        {
            string detail;
            lock (recentErrors) { detail = recentErrors.LastOrDefault() ?? "no details"; }

            lock (processLock)
            {
                // Replaced or stopped on purpose: nothing to do.
                if (!ReferenceEquals(ffmpegProcess, process) || !isRecording) return;

                isRecording = false;
                ffmpegProcess = null;

                bool forAudio = killedForAudio;
                killedForAudio = false;

                try
                {
                    var stoppedAt = DateTime.UtcNow;
                    unexpectedExits.Enqueue(stoppedAt);
                    while (unexpectedExits.Count > 0 && stoppedAt - unexpectedExits.Peek() > UnexpectedExitWindow)
                        unexpectedExits.Dequeue();
                    bool withinBudget = unexpectedExits.Count <= MaxUnexpectedExits;

                    bool neverStarted = stoppedAt - started < StartupFailureWindow;

                    // Restart after a capture we ended ourselves for the sound devices, or one that ran and then dropped
                    // (a resolution change, a lock screen). A capture that dies at once is broken, and retrying it
                    // would spin; those get the fallbacks below instead.
                    if (withinBudget && (forAudio || !neverStarted))
                    {
                        Console.WriteLine(forAudio ? "Reopening the sound devices; restarting the recording." : $"⚠ Capture stopped ({detail}). Restarting.");
                        var again = RefreshedSource(source);
                        if (again == null) { Console.WriteLine("The game's window is gone; waiting for it."); return; }
                        LaunchFfmpeg(again);
                        return;
                    }

                    // The video encoder itself wouldn't start: an out-of-date graphics driver, or another program holding the
                    // card's encoder sessions. That is not the capture's fault, so don't blame it (moving to screen capture
                    // would fail the same way); use the processor and say why.
                    string errors;
                    lock (recentErrors) errors = string.Join("\n", recentErrors);
                    if (!forAudio && neverStarted && EncoderProfile.Normalize(ResolveEncoder()) != "libx264" && EncoderProbe.LooksLikeEncoderFailure(errors))
                    {
                        encoderFailed = true;
                        encoderNote = EncoderProbe.Explain(errors);
                        Note($"⚠ The video encoder wouldn't start: {encoderNote}");
                        Warning?.Invoke($"The graphics card's video encoder wouldn't start, so Chrono is recording on the processor instead. {encoderNote}");
                        LaunchFfmpeg(source);
                        return;
                    }

                    if (!forAudio && neverStarted && source.Method == CaptureMethod.WindowCapture)
                    {
                        // This game can't be captured as a window. Never fall back to "the monitor, always": ChooseSource uses
                        // the monitor only while the game is in front, and the next check (within a second) starts that.
                        Console.WriteLine($"⚠ Window capture failed to start ({detail}). Using screen capture while the game is in front.");
                        windowCaptureFailed = true;
                        Warning?.Invoke($"Chrono couldn't capture {TargetName} directly, so it records the screen while the game is in front and pauses when it isn't.");
                        return;
                    }

                    if (!forAudio && neverStarted && source.Method == CaptureMethod.DesktopDuplication)
                    {
                        // GPU capture never got going (another GPU, an unsupported setup...). GDI is heavier but works.
                        Console.WriteLine($"⚠ GPU screen capture failed to start ({detail}). Falling back to GDI capture.");
                        LaunchFfmpeg(source with { Method = CaptureMethod.Gdi });
                        return;
                    }

                    if (forAudio) detail = "the sound devices kept changing";
                }
                catch (Exception ex)
                {
                    detail = ex.Message;
                }
            }

            Console.WriteLine($"✗ Recording stopped: {detail}");
            RecordingFailed?.Invoke($"Recording stopped unexpectedly ({detail}).");
        }

        /// <summary>A window capture needs the game's current window; a game can replace its window (loading screen to game).</summary>
        private CaptureSource? RefreshedSource(CaptureSource source)
        {
            if (source.Method != CaptureMethod.WindowCapture) return source;
            var window = ResolveGameWindow();
            return window == IntPtr.Zero ? null : source with { WindowHandle = window.ToInt64() };
        }

        /// <summary>
        /// Manually start recording (for Display mode)
        /// </summary>
        public void ForceStartRecording()
        {
            if (!isRecording && config.RecorderEnabled)
            {
                Console.WriteLine("Force starting recording...");
                StartRecording();
            }
        }

        /// <summary>
        /// "auto" (or empty) means detect the GPU encoder once and reuse it.
        /// </summary>
        /// <summary>The encoder in use (the configured one, or the best this PC has when set to auto).</summary>
        public string EncoderName => ResolveEncoder();

        public IReadOnlyList<string> RunningApplications() => GetRunningApplications();

        /// <summary>
        /// Sound devices, microphone volume and the game-capture method only take effect when FFmpeg is started, so end
        /// the current recording; the monitor restarts it within a second with the new settings. (The buffered footage is lost.)
        /// </summary>
        public void RestartRecording()
        {
            windowCaptureFailed = false;

            // Settings changed, so what "automatic" learned about the old ones no longer applies.
            loadLevel = 0;
            suggestedLowerFps = false;
            encoderFailed = false;
            encoderNote = GpuDetector.ProbeNote;
            if (config.LearnedLoadLevel != 0)
            {
                config.LearnedLoadLevel = 0;
                try { ConfigManager.Save(config); } catch { }
            }

            if (isRecording) StopRecording();
        }

        private string ResolveEncoder()
        {
            // An encoder that refused to start stays off for the rest of this run, so it isn't tried again on every restart.
            if (encoderFailed) return "libx264";

            if (string.IsNullOrWhiteSpace(config.Encoder) ||
                config.Encoder.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                if (resolvedEncoder == null)
                {
                    resolvedEncoder = GpuDetector.GetBestEncoder();
                    if (GpuDetector.ProbeNote.Length > 0) encoderNote = GpuDetector.ProbeNote;
                }
                return resolvedEncoder;
            }

            return config.Encoder;
        }

        /// <summary>The graphics card's tier, for choosing where "automatic" load starts. Modest until a card has been identified.</summary>
        private GpuDetector.GpuTier CardTier => GpuDetector.Detected?.Tier ?? GpuDetector.GpuTier.Modest;

        /// <summary>The frame rate being recorded: the setting, or 30 at the last step of reduced load.</summary>
        private int EffectiveFps => LoadPlan.FpsFor(loadLevel, config.Fps);

        /// <summary>A line for the log and for the Diagnostics page's "recent events".</summary>
        private void Note(string text)
        {
            Console.WriteLine(text);
            lock (eventLog)
            {
                eventLog.Enqueue($"{DateTime.Now:HH:mm:ss} {text}");
                while (eventLog.Count > MaxEvents) eventLog.Dequeue();
            }
        }

        /// <summary>
        /// Drop the oldest segments beyond the buffer length.
        /// </summary>
        private void PruneBuffer()
        {
            // A clip being saved is reading these files. Pruning can wait a few seconds.
            if (Volatile.Read(ref exportsInFlight) > 0) return;

            try
            {
                int deleted = tracker.Prune(config.RequiredBufferSeconds, isRecording, currentRun);
                if (deleted > 0) Console.WriteLine($"Deleted {deleted} old segment(s)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Could not prune the buffer: {ex.Message}");
            }
        }

        // Fallback only, for when the in-progress file can't be probed. Its age overstates what has reached
        // disk (FFmpeg start-up, TS muxer and hardware encoder delay held back ~2s in testing), so count less.
        private const double LiveSegmentFallbackMarginSeconds = 3.0;

        /// <summary>
        /// Finished segments plus, last, the one FFmpeg is writing right now. Oldest first.
        /// </summary>
        private List<SegmentSpan> SnapshotSpans()
        {
            var scan = tracker.Scan(isRecording, currentRun);
            var spans = scan.Finished.ToList();

            if (scan.LivePath != null)
            {
                // Measure what is really on disk. Age alone overstates it.
                double estimate = Math.Min(SegmentSeconds,
                    (DateTime.Now - File.GetCreationTime(scan.LivePath)).TotalSeconds - LiveSegmentFallbackMarginSeconds);
                double live = MediaProbe.DurationSeconds(scan.LivePath) ?? estimate;

                if (live >= 1)
                    spans.Add(new SegmentSpan(scan.LivePath, live));
            }

            return spans;
        }
        /// <summary>
        /// Save the last N seconds from the buffer to the output folder. Copy-joins the segments when the clip should
        /// stay at the monitor's size, and otherwise shrinks it (on the NVIDIA GPU where possible) as it saves.
        /// </summary>
        public string SaveClip(int clipLengthSeconds, string clipName = "")
        {
            // Hold off the pruner for the whole job, planning included: the plan names the files the export will read.
            System.Threading.Interlocked.Increment(ref exportsInFlight);
            try
            {
                return SaveClipCore(clipLengthSeconds, clipName);
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref exportsInFlight);
            }
        }

        private string SaveClipCore(int clipLengthSeconds, string clipName)
        {
            Size? exportSize = ExportPlanner.ExportSize(config.Resolution, nativeSize);
            bool transcode = exportSize != null;

            var plan = ClipPlanner.Plan(SnapshotSpans(), ExportPlanner.PlannedSeconds(clipLengthSeconds, transcode))
                ?? throw new InvalidOperationException("No footage in the buffer yet");

            if (plan.AvailableSeconds < clipLengthSeconds)
            {
                Console.WriteLine($"⚠ Only {plan.AvailableSeconds:F0}s buffered, wanted {clipLengthSeconds}s. Saving what there is.");
            }

            // Timestamped so a second clip from the same hotkey doesn't overwrite the first.
            string filename = string.IsNullOrEmpty(clipName)
                ? SanitizeFilename(WindowDetector.GetClipFilename())
                : $"{SanitizeFilename(clipName)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";

            Directory.CreateDirectory(config.OutputFolder);
            string outputPath = UniquePath(config.OutputFolder, filename, ".mp4");

            ExportClip(plan, outputPath, exportSize, Math.Min(clipLengthSeconds, plan.AvailableSeconds));

            Console.WriteLine($"✓ Clip saved: {outputPath}");
            return outputPath;
        }

        private static string UniquePath(string folder, string name, string extension)
        {
            string path = Path.Combine(folder, name + extension);

            for (int n = 2; File.Exists(path); n++)
            {
                path = Path.Combine(folder, $"{name}_{n}{extension}");
            }

            return path;
        }

        /// <summary>
        /// Turn the planned segments into a finished clip: copy them together, or shrink them to
        /// <paramref name="size"/>. Shrinking uses NVIDIA's decoder and encoder when the recording is NVENC, and
        /// falls back to doing it in software if that fails.
        /// </summary>
        private void ExportClip(ClipPlan plan, string outputPath, Size? size, double lengthSeconds)
        {
            string concatFilePath = Path.Combine(config.TempFolder, $"concat_{Guid.NewGuid():N}.txt");

            try
            {
                var entries = plan.Segments.Select(s => $"file '{EscapeConcatPath(s.Path)}'");
                File.WriteAllLines(concatFilePath, entries, new UTF8Encoding(false));

                string encoder = ResolveEncoder();
                ExportRequest Request(ExportMode mode) => new ExportRequest(
                    concatFilePath, outputPath, plan.SeekSeconds, lengthSeconds, mode,
                    size ?? nativeSize, encoder, BitrateSizing.Resolve(config.Bitrate, size ?? nativeSize, EffectiveFps), EffectiveFps);

                if (size == null)
                {
                    RunFfmpeg(ExportCommand.Build(Request(ExportMode.Copy)), ExportTimeoutMs(lengthSeconds));
                    return;
                }

                if (PreferGpuExport && ExportPlanner.CanUseGpu(encoder))
                {
                    try
                    {
                        RunFfmpeg(ExportCommand.Build(Request(ExportMode.Gpu)), ExportTimeoutMs(lengthSeconds));
                        Console.WriteLine($"  resized {nativeSize.Width}x{nativeSize.Height} -> {size.Value.Width}x{size.Value.Height} on the GPU");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠ GPU resize failed ({ex.Message}); doing it in software instead");
                        try { File.Delete(outputPath); } catch { }
                    }
                }

                try
                {
                    RunFfmpeg(ExportCommand.Build(Request(ExportMode.Cpu)), ExportTimeoutMs(lengthSeconds));
                    Console.WriteLine($"  resized {nativeSize.Width}x{nativeSize.Height} -> {size.Value.Width}x{size.Value.Height} in software");
                    return;
                }
                catch (Exception ex) when (EncoderProfile.Normalize(encoder) != "libx264")
                {
                    // Scaling was done in software, but the picture was still encoded by the graphics card. If that is
                    // what is broken, the clip is already recorded and only needs encoding, so try it on the processor
                    // rather than lose it.
                    Console.WriteLine($"⚠ The {encoder} encoder failed ({ex.Message}); encoding this clip on the processor");
                    try { File.Delete(outputPath); } catch { }
                }

                var software = Request(ExportMode.Cpu) with { Encoder = "libx264" };
                RunFfmpeg(ExportCommand.Build(software), ExportTimeoutMs(lengthSeconds));
                Console.WriteLine($"  resized {nativeSize.Width}x{nativeSize.Height} -> {size.Value.Width}x{size.Value.Height} on the processor");
            }
            finally
            {
                try { File.Delete(concatFilePath); } catch { }
            }
        }

        /// <summary>
        /// How long to allow an export before calling it stuck. A software encode of a long clip on a slow PC is
        /// the worst case, so the allowance grows with the clip instead of being one number that a two-minute
        /// clip could exceed on an old processor.
        /// </summary>
        private static int ExportTimeoutMs(double lengthSeconds)
            => (int)Math.Clamp(60_000 + lengthSeconds * 8_000, 180_000, 900_000);

        /// <summary>Run FFmpeg to completion; throw with its own message if it fails or takes too long.</summary>
        private static void RunFfmpeg(string arguments, int timeoutMs = 180_000)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FfmpegLocator.Path,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            // Drain stderr while waiting so a chatty FFmpeg can't fill the pipe and hang.
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { }
                throw new TimeoutException("FFmpeg took too long to save the clip");
            }

            if (process.ExitCode != 0)
            {
                // The last lines say what went wrong; the first are the file's details.
                string message = stderr.Result.Trim();
                throw new Exception($"FFmpeg failed with exit code {process.ExitCode}: {(message.Length > 300 ? message.Substring(message.Length - 300) : message)}");
            }
        }

        private static string EscapeConcatPath(string path)
            => path.Replace('\\', '/').Replace("'", "'\\''");

        /// <summary>
        /// Get current buffer status
        /// </summary>
        public (int segmentCount, double totalDuration) GetBufferStatus()
        {
            var finished = tracker.Scan(isRecording, currentRun).Finished;
            return (finished.Count, finished.Sum(s => s.DurationSeconds));
        }

        /// <summary>
        /// Sanitize filename for Windows
        /// </summary>
        private string SanitizeFilename(string filename)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            return new string(filename.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        }
        /// <summary>
        /// Start monitoring for application changes
        /// </summary>
        public void StartMonitoring()
        {
            Console.WriteLine("Recorder.StartMonitoring called");
            
            // Poll active window every 1 second
            monitorTimer = new System.Threading.Timer(CheckActiveApplication, null, 0, 1000);

            // Old segments are deleted here, off the recording path, every few seconds.
            pruneTimer = new System.Threading.Timer(_ => PruneBuffer(), null, 5000, 5000);
            
            Console.WriteLine("✓ Timer created - polling every 1 second");
            Console.WriteLine("✓ Application monitoring started");
        }

        /// <summary>
        /// Stop monitoring
        /// </summary>
        public void StopMonitoring()
        {
            monitorTimer?.Dispose();
            monitorTimer = null;
            pruneTimer?.Dispose();
            pruneTimer = null;
            Console.WriteLine("✓ Application monitoring stopped");
        }

        /// <summary>
        /// Runs every second: notice foreground changes, and start or stop recording as the rules say.
        /// </summary>
        private void CheckActiveApplication(object? state)
        {
            // A tick can outlast its second: stopping a recording waits for FFmpeg to close its file. The timer would
            // start the next tick anyway, and two of them together could stop one recording and start another out of
            // order. One at a time, and a skipped tick costs nothing since the next is a second away.
            if (System.Threading.Interlocked.Exchange(ref monitorBusy, 1) == 1) return;

            try
            {
                string activeApp = WindowDetector.GetActiveApplicationName();

                if (activeApp != currentApplication)
                {
                    currentApplication = activeApp;
                    Console.WriteLine($"🔄 Active app changed to: '{activeApp}'");
                    ApplicationChanged?.Invoke(this, activeApp);
                }

                // Every tick, not only when focus changes: the game may start, or close, while something else is in front.
                HandleRecordingMode();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error in monitor: {ex.Message}");
            }
            finally
            {
                System.Threading.Volatile.Write(ref monitorBusy, 0);
            }
        }

        /// <summary>
        /// Start or stop recording according to <see cref="RecordingPolicy"/>.
        /// </summary>
        private void HandleRecordingMode()
        {
            int? gameBefore = autoGame?.Candidate.Pid;
            if (config.RecorderEnabled && config.Mode == RecorderConfig.RecordingMode.Auto) UpdateAutoGame();
            else autoGame = null;

            bool gameRunning = config.RecorderEnabled && config.Mode switch
            {
                RecorderConfig.RecordingMode.Application => WindowDetector.FindApplicationWindow(config.SelectedApplication) != IntPtr.Zero,
                RecorderConfig.RecordingMode.Auto => autoGame != null,
                _ => false
            };

            bool shouldRecord = RecordingPolicy.ShouldRecord(config.RecorderEnabled, config.Mode, config.SelectedApplication, gameRunning);

            // Screen capture (a fallback for games) must never see anything but the game: pause the moment it isn't in front.
            if (shouldRecord && isRecording && captureNeedsGameInFront && !GameInFront())
            {
                if (System.Threading.Interlocked.Exchange(ref focusBusy, 1) == 0)   // the faster check may be doing this already
                {
                    try { PauseBecauseGameLeftFront(); }
                    finally { System.Threading.Volatile.Write(ref focusBusy, 0); }
                }
                return;
            }

            // A different game took over (the old one closed and another was found): record its monitor instead.
            if (shouldRecord && isRecording && config.Mode == RecorderConfig.RecordingMode.Auto && gameBefore != null && autoGame?.Candidate.Pid != gameBefore)
            {
                Console.WriteLine($"Switching recording to {TargetName}");
                StopRecording();
                StartRecording();
            }
            else if (shouldRecord && !isRecording)
            {
                StartRecording();   // logs when it starts; does nothing (and says why, once) if there is nothing safe to record yet
            }
            else if (!shouldRecord && isRecording)
            {
                Console.WriteLine(config.RecorderEnabled ? "⏹️ STOPPING RECORDING (the game is no longer running)" : "⏹️ STOPPING RECORDING (recorder disabled)");
                StopRecording();
            }
        }

        /// <summary>What is being recorded, for the UI: the chosen game, or "display".</summary>
        public string TargetName => config.Mode switch
        {
            RecorderConfig.RecordingMode.Auto => autoGame?.Candidate.DisplayName ?? "no game running",
            RecorderConfig.RecordingMode.Application when !string.IsNullOrWhiteSpace(config.SelectedApplication) => config.SelectedApplication,
            _ => "display"
        };

        /// <summary>Keep the current game while it runs; when it has gone (or there is none), look for a new one every few seconds.</summary>
        private void UpdateAutoGame()
        {
            if (autoGame != null && scanner.IsStillRunning(autoGame.Candidate)) return;

            if (autoGame != null)
            {
                Note($"{autoGame.Candidate.DisplayName} closed");
                autoGame = null;
            }

            if (DateTime.UtcNow < nextGameScanUtc) return;
            nextGameScanUtc = DateTime.UtcNow + GameScanInterval;

            var games = scanner.FindGames();
            var chosen = GameClassifier.Choose(games.Select(g => g.Candidate).ToList(), currentPid: null);
            autoGame = games.FirstOrDefault(g => g.Candidate == chosen);
            windowCaptureFailed = false;   // a different game may well work as a window
            loggedWaiting = false;
            if (autoGame != null) Note($"Game found: {autoGame.Candidate.DisplayName} ({autoGame.Candidate.ProcessName})");
        }

        /// <summary>
        /// Manually enable/disable recorder (called from UI)
        /// </summary>
        public void SetRecorderEnabled(bool enabled)
        {
            config.RecorderEnabled = enabled;
            ConfigManager.Save(config);
            
            if (!enabled && isRecording)
            {
                StopRecording();
            }
        }

        /// <summary>
        /// Set the application to track (called from UI)
        /// </summary>
        public void SetTrackedApplication(string appName)
        {
            windowCaptureFailed = false;
            config.SelectedApplication = appName;
            ConfigManager.Save(config);
            Console.WriteLine($"Now tracking: {appName}");
        }

        /// <summary>
        /// Set recording mode (called from UI)
        /// </summary>
        public void SetRecordingMode(RecorderConfig.RecordingMode mode)
        {
            config.Mode = mode;
            ConfigManager.Save(config);
            Console.WriteLine($"Recording mode: {mode}");
        }

        /// <summary>
        /// Get list of currently running applications (for UI dropdown)
        /// </summary>
        public static List<string> GetRunningApplications()
        {
            // The Recording page asks for this every three seconds while it is open, so it walks the desktop's
            // windows once and then names only those few programs. Asking every process on the PC for its main
            // window instead (the old way) made Windows walk that list again for each of some 300 processes.
            var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var (pid, _) in ProcessScanner.ProgramWindows(includeMinimized: true))
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        if (IsSystemProcess(process.ProcessName)) continue;
                        apps.Add(WindowDetector.CleanApplicationName(process.ProcessName));
                    }
                    catch
                    {
                        // Gone already, or not ours to inspect.
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error getting running apps: {ex.Message}");
            }

            return apps.OrderBy(a => a).ToList();
        }

        private static bool IsSystemProcess(string name)
        {
            string lower = name.ToLower();
            return lower == "explorer" || 
                   lower == "dwm" || 
                   lower == "taskmgr" || 
                   lower == "systemsettings" ||
                   lower == "svchost" ||
                   lower == "csrss";
        }


        /// <summary>
        /// Check if application is a system app
        /// </summary>
        private bool IsSystemApp(string appName)
        {
            string[] systemApps = { 
                "explorer", "dwm", "taskmgr", "systemsettings", 
                "applicationframehost", "shellexperiencehost"
            };
            
            return systemApps.Any(sys => 
                appName.Contains(sys, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get current recording status for UI
        /// </summary>
        public string GetRecordingStatus()
        {
            if (!config.RecorderEnabled)
                return "DISABLED";
            
            if (isRecording)
                return $"RECORDING: {TargetName}";
            
            return config.Mode switch
            {
                RecorderConfig.RecordingMode.Application => "IDLE: Waiting for tracked app...",
                RecorderConfig.RecordingMode.Auto => "IDLE: Waiting for a game...",
                _ => "IDLE"
            };
        }
    }
}