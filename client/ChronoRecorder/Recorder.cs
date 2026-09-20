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
    public class Recorder
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

        private System.Threading.Timer? monitorTimer;
        private System.Threading.Timer? pruneTimer;
        private string currentApplication = "";
        public event EventHandler<string>? ApplicationChanged;

        /// <summary>Raised when recording stops for a reason the user should hear about. The text is fit to show.</summary>
        public event Action<string>? RecordingFailed;

        /// <summary>Something the user should know that doesn't stop recording, such as sound not being captured.</summary>
        public event Action<string>? Warning;

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
                    var source = CaptureSourceChooser.Choose(PickMonitor(), PrimaryBounds());
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

        /// <summary>
        /// The monitor to record: wherever the foreground window is. Recording starts when the tracked game gets
        /// focus, so that is the game's monitor.
        /// </summary>
        private static MonitorInfo? PickMonitor()
            => MonitorLocator.ForWindow(WindowDetector.GetForegroundWindowHandle()) ?? MonitorLocator.Primary();

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

            void Add(AudioSource source, string what)
            {
                var capture = AudioCapture.TryCreate(source, clockStart, delay, out string? problem);
                if (capture == null)
                {
                    Console.WriteLine($"⚠ No {what}: {problem}");
                    Warning?.Invoke($"Recording without {what}: {problem}.");
                    return;
                }

                capture.Failed += reason => OnAudioFailed(capture, what, reason);
                captures.Add(capture);
            }

            Add(AudioSource.SystemSound, "game and system sound");
            if (config.RecordMicrophone) Add(AudioSource.Microphone, "microphone");

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
            Size output = CaptureSizing.Resolve(config.Resolution, source.Bounds.Size);

            // Picture and sound are both timed from one instant, T0. Set before the audio sources exist.
            double clockStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            DisposeAudio();
            var audio = CreateAudioCaptures(clockStart);
            audioCaptures = audio;

            var request = new CaptureRequest(
                ResolveEncoder(), config.Fps, config.Bitrate, source, output,
                SegmentSeconds, SegmentTracker.NamePattern(config.TempFolder, run),
                Audio: audio.Select(a => a.Input).ToList(),
                ClockStartUnixSeconds: audio.Count > 0 ? clockStart : null);

            Console.WriteLine($"▶ Recording {source.Bounds.Width}x{source.Bounds.Height} monitor at {output.Width}x{output.Height} " +
                              $"{config.Fps}fps via {source.Method}{(output != source.Bounds.Size ? " (scaled on the CPU)" : "")}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
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
            CaptureResolution = CaptureSizing.Describe(output);
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
                        LaunchFfmpeg(source);
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
        /// Save the last N seconds from the buffer to the output folder
        /// </summary>
        public string SaveClip(int clipLengthSeconds, string clipName = "")
        {
            // Copy mode can only start on a keyframe and drops everything before the first one at or after the
            // seek point. Plan one keyframe interval extra so the clip lands between N and N+interval seconds,
            // never shorter than asked.
            var plan = ClipPlanner.Plan(SnapshotSpans(), clipLengthSeconds + EncoderProfile.KeyframeIntervalSeconds)
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

            ConcatenateSegments(plan, outputPath);

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
        /// Join the planned segments and start the clip at the plan's seek offset (no re-encode).
        /// </summary>
        private void ConcatenateSegments(ClipPlan plan, string outputPath)
        {
            string concatFilePath = Path.Combine(config.TempFolder, $"concat_{Guid.NewGuid():N}.txt");

            try
            {
                var entries = plan.Segments.Select(s => $"file '{EscapeConcatPath(s.Path)}'");
                File.WriteAllLines(concatFilePath, entries, new UTF8Encoding(false));

                // -ss before -i seeks within the joined footage, so the extra footage at the START is skipped
                // and the newest is kept. In copy mode the clip then starts at the first keyframe at or after
                // this point; the plan already includes one keyframe interval of slack for that.
                string seek = plan.SeekSeconds > 0.05
                    ? $"-ss {plan.SeekSeconds.ToString("F3", CultureInfo.InvariantCulture)} "
                    : "";

                string args = $"-hide_banner -loglevel error {seek}" +
                              $"-f concat -safe 0 -i \"{concatFilePath}\" " +
                              $"-c copy -movflags +faststart -y \"{outputPath}\"";

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                // Drain stderr while waiting so a chatty FFmpeg can't fill the pipe and hang.
                var stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(120_000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("FFmpeg took too long to save the clip");
                }

                if (process.ExitCode != 0)
                {
                    throw new Exception($"FFmpeg failed with exit code {process.ExitCode}: {stderr.Result.Trim()}");
                }
            }
            finally
            {
                try { File.Delete(concatFilePath); } catch { }
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
        /// Check active application and start/stop recording based on mode
        /// </summary>
        private void CheckActiveApplication(object? state)
        {
            try
            {
                string activeApp = WindowDetector.GetActiveApplicationName();
                
                // Check if application changed
                if (activeApp != currentApplication)
                {
                    currentApplication = activeApp;
                    Console.WriteLine($"🔄 Active app changed to: '{activeApp}'");
                    ApplicationChanged?.Invoke(this, activeApp);
                    
                    // Handle recording based on mode
                    HandleRecordingMode(activeApp);
                }
                
                // Also log comparison for debugging
                if (config.RecorderEnabled && !string.IsNullOrEmpty(config.SelectedApplication))
                {
                    bool matches = activeApp.Equals(config.SelectedApplication, StringComparison.OrdinalIgnoreCase);
                    if (!matches)
                    {
                        Console.WriteLine($"❌ '{activeApp}' != '{config.SelectedApplication}' (not recording)");
                    }
                    else
                    {
                        Console.WriteLine($"✅ '{activeApp}' == '{config.SelectedApplication}' (should be recording: {isRecording})");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error in monitor: {ex.Message}");
            }
        }

        /// <summary>
        /// Start or stop recording based on current mode and active app
        /// </summary>
        private void HandleRecordingMode(string activeApp)
        {
            if (!config.RecorderEnabled)
            {
                if (isRecording) 
                {
                    Console.WriteLine("Recorder disabled, stopping...");
                    StopRecording();
                }
                return;
            }

            bool shouldRecord = false;

            switch (config.Mode)
            {
                case RecorderConfig.RecordingMode.Application:
                    // Only record when selected app is focused
                    if (!string.IsNullOrEmpty(config.SelectedApplication))
                    {
                        shouldRecord = activeApp.Equals(config.SelectedApplication, StringComparison.OrdinalIgnoreCase);
                        Console.WriteLine($"Application mode: {activeApp} vs {config.SelectedApplication} = {shouldRecord}");
                    }
                    break;

                case RecorderConfig.RecordingMode.Display:
                    // Always record in display mode
                    shouldRecord = true;
                    Console.WriteLine($"Display mode: Always record = true");
                    break;
            }

            // Start or stop recording
            if (shouldRecord && !isRecording)
            {
                Console.WriteLine($"▶️ STARTING RECORDING for: {activeApp}");
                StartRecording();
            }
            else if (!shouldRecord && isRecording)
            {
                Console.WriteLine($"⏹️ STOPPING RECORDING (left: {activeApp})");
                StopRecording();
            }
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
                return $"RECORDING: {currentApplication}";
            
            return config.Mode switch
            {
                RecorderConfig.RecordingMode.Application => "IDLE: Waiting for tracked app...",
                _ => "IDLE"
            };
        }
    }
}