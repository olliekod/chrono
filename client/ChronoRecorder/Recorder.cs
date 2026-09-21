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
    public class Recorder : IRecorder
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
        private string currentApplication = "";

        // Auto mode: the game found by the scanner, and when to look again.
        private readonly ProcessScanner scanner = new ProcessScanner();

        // Games are captured as their own window. If that can't start for a game, the monitor is used instead, but only while
        // the game is in front (see GameCapturePlanner); these track that.
        private bool windowCaptureFailed;
        private bool captureNeedsGameInFront;
        private bool loggedWaiting;
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

            if (config.Mode == RecorderConfig.RecordingMode.Display)
                return CaptureSourceChooser.Choose(PickMonitor(), PrimaryBounds());

            IntPtr window = ResolveGameWindow();
            var plan = GameCapturePlanner.Plan(config.GameCapture, windowCaptureFailed, window != IntPtr.Zero);
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
                    return CaptureSourceChooser.Choose(monitor, PrimaryBounds());

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
            return PickMonitor()?.Bounds.Size ?? PrimaryBounds().Size;
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
            lock (processLock)
            {
                // Clearing these first tells the exit handler this stop was deliberate.
                isRecording = false;
                process = ffmpegProcess;
                ffmpegProcess = null;
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

            DisposeAudio();
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

            var request = new CaptureRequest(
                ResolveEncoder(), config.Fps, BitrateSizing.Resolve(config.Bitrate, source.Bounds.Size, config.Fps), source,
                SegmentSeconds, SegmentTracker.NamePattern(config.TempFolder, run),
                Audio: audio.Select(a => a.Input).ToList(),
                ClockStartUnixSeconds: audio.Count > 0 ? clockStart : null);

            Console.WriteLine($"▶ Recording the {source.Bounds.Width}x{source.Bounds.Height} monitor at {config.Fps}fps via {source.Method}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FfmpegLocator.Path,
                    Arguments = CaptureCommand.Build(request),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
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

            // FFmpeg opens the audio pipes as it starts; the captures wait for that and then feed them.
            foreach (var capture in audio) capture.Start();

            ffmpegProcess = process;
            currentRun = run;
            nativeSize = source.Bounds.Size;
            CaptureResolution = CaptureSizing.Describe(nativeSize);
            isRecording = true;
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

                try
                {
                    if (DateTime.UtcNow - started < StartupFailureWindow && source.Method == CaptureMethod.WindowCapture)
                    {
                        // This game can't be captured as a window. Never fall back to "the monitor, always": ChooseSource uses
                        // the monitor only while the game is in front, and the next check (within a second) starts that.
                        Console.WriteLine($"⚠ Window capture failed to start ({detail}). Using screen capture while the game is in front.");
                        windowCaptureFailed = true;
                        Warning?.Invoke($"Chrono couldn't capture {TargetName} directly, so it records the screen while the game is in front and pauses when it isn't.");
                        return;
                    }

                    if (DateTime.UtcNow - started < StartupFailureWindow && source.Method == CaptureMethod.DesktopDuplication)
                    {
                        // GPU capture never got going (another GPU, an unsupported setup...). GDI is heavier but works.
                        Console.WriteLine($"⚠ GPU screen capture failed to start ({detail}). Falling back to GDI capture.");
                        LaunchFfmpeg(source with { Method = CaptureMethod.Gdi });
                        return;
                    }

                    var now = DateTime.UtcNow;
                    unexpectedExits.Enqueue(now);
                    while (unexpectedExits.Count > 0 && now - unexpectedExits.Peek() > UnexpectedExitWindow)
                        unexpectedExits.Dequeue();

                    if (DateTime.UtcNow - started >= StartupFailureWindow && unexpectedExits.Count <= MaxUnexpectedExits)
                    {
                        // It was working and then the capture dropped (a resolution change, a lock screen...).
                        Console.WriteLine($"⚠ Capture stopped ({detail}). Restarting.");
                        var again = RefreshedSource(source);
                        if (again == null) { Console.WriteLine("The game's window is gone; waiting for it."); return; }
                        LaunchFfmpeg(again);
                        return;
                    }
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
            if (isRecording) StopRecording();
        }

        private string ResolveEncoder()
        {
            if (string.IsNullOrWhiteSpace(config.Encoder) ||
                config.Encoder.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return resolvedEncoder ??= GpuDetector.GetBestEncoder();
            }

            return config.Encoder;
        }

        /// <summary>
        /// Drop the oldest segments beyond the buffer length.
        /// </summary>
        private void PruneBuffer()
        {
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
                    size ?? nativeSize, encoder, BitrateSizing.Resolve(config.Bitrate, size ?? nativeSize, config.Fps), config.Fps);

                if (size == null)
                {
                    RunFfmpeg(ExportCommand.Build(Request(ExportMode.Copy)));
                    return;
                }

                if (PreferGpuExport && ExportPlanner.CanUseGpu(encoder))
                {
                    try
                    {
                        RunFfmpeg(ExportCommand.Build(Request(ExportMode.Gpu)));
                        Console.WriteLine($"  resized {nativeSize.Width}x{nativeSize.Height} -> {size.Value.Width}x{size.Value.Height} on the GPU");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠ GPU resize failed ({ex.Message}); doing it in software instead");
                        try { File.Delete(outputPath); } catch { }
                    }
                }

                RunFfmpeg(ExportCommand.Build(Request(ExportMode.Cpu)));
                Console.WriteLine($"  resized {nativeSize.Width}x{nativeSize.Height} -> {size.Value.Width}x{size.Value.Height} in software");
            }
            finally
            {
                try { File.Delete(concatFilePath); } catch { }
            }
        }

        /// <summary>Run FFmpeg to completion; throw with its own message if it fails or takes too long.</summary>
        private static void RunFfmpeg(string arguments)
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

            if (!process.WaitForExit(180_000))
            {
                try { process.Kill(); } catch { }
                throw new TimeoutException("FFmpeg took too long to save the clip");
            }

            if (process.ExitCode != 0)
            {
                string message = stderr.Result.Trim();
                throw new Exception($"FFmpeg failed with exit code {process.ExitCode}: {(message.Length > 300 ? message.Substring(0, 300) : message)}");
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
                Console.WriteLine($"⏸️ {TargetName} isn't in front: pausing screen capture so nothing else is recorded");
                StopRecording();
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
                Console.WriteLine($"{autoGame.Candidate.DisplayName} closed");
                autoGame = null;
            }

            if (DateTime.UtcNow < nextGameScanUtc) return;
            nextGameScanUtc = DateTime.UtcNow + GameScanInterval;

            var games = scanner.FindGames();
            var chosen = GameClassifier.Choose(games.Select(g => g.Candidate).ToList(), currentPid: null);
            autoGame = games.FirstOrDefault(g => g.Candidate == chosen);
            windowCaptureFailed = false;   // a different game may well work as a window
            loggedWaiting = false;
            if (autoGame != null) Console.WriteLine($"Game found: {autoGame.Candidate.DisplayName} ({autoGame.Candidate.ProcessName})");
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
            var apps = new HashSet<string>();
            
            try
            {
                Console.WriteLine("Getting running applications...");
                var processes = System.Diagnostics.Process.GetProcesses();
                Console.WriteLine($"Found {processes.Length} total processes");
                
                foreach (var process in processes)
                {
                    try
                    {
                        // Only include processes with windows
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            string name = process.ProcessName;
                            
                            // Skip system processes
                            if (!IsSystemProcess(name))
                            {
                                // Clean up name
                                name = WindowDetector.CleanApplicationName(name);
                                apps.Add(name);
                                Console.WriteLine($"  Added: {name}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Some processes can't be accessed
                        Console.WriteLine($"  Skipped process: {ex.Message}");
                    }
                }
                
                Console.WriteLine($"✓ Returning {apps.Count} applications");
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