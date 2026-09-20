using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;

namespace ChronoRecorder
{
    /// <summary>
    /// handles FFmpeg screen recording with circular buffer
    /// </summary>
    public class Recorder
    {
        private readonly RecorderConfig config;
        private Process? ffmpegProcess;
        private bool isRecording = false;
        // Finished segments, oldest first. Touched from the FFmpeg exit thread, the monitor timer and the
        // UI thread (hotkey), so every access goes through segmentLock.
        private readonly Queue<SegmentInfo> segments = new Queue<SegmentInfo>();
        private readonly object segmentLock = new object();
        private string currentSegmentPath = "";
        private DateTime currentSegmentStart;
        private static int SegmentSeconds => RecorderConfig.SegmentSeconds;
        private string? resolvedEncoder;

        private System.Threading.Timer? monitorTimer;
        private string currentApplication = "";
        public event EventHandler<string>? ApplicationChanged;
        public bool IsRecordingActive => isRecording;


        public class SegmentInfo
        {
            public string FilePath { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            public double DurationSeconds { get; set; }
        }

        public Recorder(RecorderConfig config)
        {
            this.config = config;
            Directory.CreateDirectory(config.TempFolder);
            DeleteStaleSegments();
        }

        /// <summary>
        /// Segments left behind by a crashed or killed previous run aren't in the queue, so nothing else would delete them.
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
        /// start recording screen in background
        /// </summary>
        public void StartRecording()
        {
            if (isRecording)
            {
                Console.WriteLine("⚠ Already recording");
                return;
            }

            try
            {
                // starts segment recording loop
                isRecording = true;
                RecordNextSegment();
                Console.WriteLine("✓ Recording started");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Failed to start recording: {ex.Message}");
                isRecording = false;
            }
        }

        /// <summary>
        /// stop recording
        /// </summary>
        public void StopRecording()
        {
            // gotta have an off button
            isRecording = false;
            
            if (ffmpegProcess != null && !ffmpegProcess.HasExited)
            {
                try
                {
                    ffmpegProcess.StandardInput.WriteLine("q"); // Graceful stop
                    ffmpegProcess.WaitForExit(3000);
                    
                    if (!ffmpegProcess.HasExited)
                    {
                        ffmpegProcess.Kill();
                    }
                }
                catch { }
                
                ffmpegProcess = null;
            }

            Console.WriteLine("✓ Recording stopped");
        }

        /// <summary>
        /// record a single segment
        /// </summary>
        private void RecordNextSegment()
        {
            if (!isRecording) return;

            // MPEG-TS, not MP4: an MP4 isn't readable until FFmpeg finishes it (the index is written last),
            // but a TS file can be read mid-write, which lets a clip include the segment still in progress.
            Directory.CreateDirectory(config.TempFolder);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string segmentPath = Path.Combine(config.TempFolder, $"segment_{timestamp}.ts");
            DateTime segmentStart = DateTime.Now;

            lock (segmentLock)
            {
                currentSegmentPath = segmentPath;
                currentSegmentStart = segmentStart;
            }

            // Build FFmpeg command for screen capture
            string ffmpegArgs = BuildFFmpegArgs(segmentPath, SegmentSeconds);

            // Start FFmpeg process
            ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffmpegArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            ffmpegProcess.EnableRaisingEvents = true;
            // Bind the path to this process so a late Exited event can't be attributed to a newer segment.
            ffmpegProcess.Exited += (s, e) => OnSegmentFinished(segmentPath, segmentStart);

            ffmpegProcess.Start();

            // Read output asynchronously to prevent blocking
            ffmpegProcess.BeginOutputReadLine();
            ffmpegProcess.BeginErrorReadLine();
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
        /// Build FFmpeg arguments for screen capture
        /// </summary>
        private string BuildFFmpegArgs(string outputPath, int duration)
        {
            // Parse resolution
            string[] resParts = config.Resolution.Split('x');
            string width = resParts[0];
            string height = resParts[1];

            // gdigrab = built-in Windows screen capture. Encoder flags depend on the encoder (see EncoderProfile).
            // -flush_packets 1 writes each packet straight to disk so the in-progress segment is readable.
            string args = $"-hide_banner -nostats -loglevel warning " +
                         $"-f gdigrab -framerate {config.Fps} -i desktop " +
                         EncoderProfile.BuildArgs(ResolveEncoder(), config.Bitrate, config.Fps) + " " +
                         $"-s {width}x{height} " +
                         $"-flush_packets 1 " +
                         $"-t {duration} " +
                         $"-y \"{outputPath}\"";

            return args;
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
        /// Called when a segment finishes recording
        /// </summary>
        private void OnSegmentFinished(string segmentPath, DateTime segmentStart)
        {
            if (File.Exists(segmentPath))
            {
                lock (segmentLock)
                {
                    // Add segment to buffer. A segment cut short by StopRecording is shorter than the
                    // nominal length; the planner only needs a duration close to the truth.
                    double duration = Math.Min(SegmentSeconds, (DateTime.Now - segmentStart).TotalSeconds);

                    segments.Enqueue(new SegmentInfo
                    {
                        FilePath = segmentPath,
                        CreatedAt = segmentStart,
                        DurationSeconds = duration
                    });
                    Console.WriteLine($"✓ Segment saved: {Path.GetFileName(segmentPath)}");

                    // Clean up old segments (keep only buffer duration)
                    CleanupOldSegments();
                }
            }

            // Start next segment if still recording
            if (isRecording)
            {
                RecordNextSegment();
            }
        }

        /// <summary>
        /// Remove segments beyond the required buffer length. Caller holds segmentLock.
        /// </summary>
        private void CleanupOldSegments()
        {
            double totalDuration = segments.Sum(s => s.DurationSeconds);
            int keepSeconds = config.RequiredBufferSeconds;

            while (totalDuration > keepSeconds && segments.Count > 0)
            {
                var oldSegment = segments.Dequeue();
                totalDuration -= oldSegment.DurationSeconds;

                try
                {
                    if (File.Exists(oldSegment.FilePath))
                    {
                        File.Delete(oldSegment.FilePath);
                        Console.WriteLine($"Deleted old segment: {Path.GetFileName(oldSegment.FilePath)}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠ Could not delete {oldSegment.FilePath}: {ex.Message}");
                }
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
            List<SegmentSpan> spans;
            string? livePath = null;
            double liveEstimate = 0;

            lock (segmentLock)
            {
                spans = segments.Select(s => new SegmentSpan(s.FilePath, s.DurationSeconds)).ToList();

                // Skip if it was just queued but currentSegmentPath hasn't moved on to the next segment yet.
                bool alreadyQueued = spans.Any(s => s.Path == currentSegmentPath);

                if (isRecording && !alreadyQueued && currentSegmentPath != "" && File.Exists(currentSegmentPath))
                {
                    livePath = currentSegmentPath;
                    double elapsed = (DateTime.Now - currentSegmentStart).TotalSeconds;
                    liveEstimate = Math.Min(SegmentSeconds, elapsed) - LiveSegmentFallbackMarginSeconds;
                }
            }

            // Measure what is really on disk, outside the lock since it spawns ffprobe.
            if (livePath != null)
            {
                double live = ProbeDurationSeconds(livePath) ?? liveEstimate;

                if (live >= 1)
                    spans.Add(new SegmentSpan(livePath, live));
            }

            return spans;
        }

        /// <summary>
        /// Real duration of a media file, or null if ffprobe isn't available or can't read it.
        /// </summary>
        private static double? ProbeDurationSeconds(string path)
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });

                if (probe == null) return null;

                var output = probe.StandardOutput.ReadToEndAsync();
                probe.StandardError.ReadToEndAsync();

                if (!probe.WaitForExit(5000))
                {
                    try { probe.Kill(); } catch { }
                    return null;
                }

                return double.TryParse(output.Result.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    ? seconds
                    : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Could not probe {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
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
            lock (segmentLock)
            {
                return (segments.Count, segments.Sum(s => s.DurationSeconds));
            }
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