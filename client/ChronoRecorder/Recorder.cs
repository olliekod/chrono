using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;

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
        private readonly Queue<SegmentInfo> segments = new Queue<SegmentInfo>();
        private string currentSegmentPath = "";
        private DateTime lastSegmentTime;
        private readonly int segmentDurationSeconds = 10; // 10 second segments

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

            // generate segment filename
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            currentSegmentPath = Path.Combine(config.TempFolder, $"segment_{timestamp}.mp4");
            lastSegmentTime = DateTime.Now;

            // Build FFmpeg command for screen capture
            string ffmpegArgs = BuildFFmpegArgs(currentSegmentPath, segmentDurationSeconds);

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
            ffmpegProcess.Exited += OnSegmentFinished;

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

            // FFmpeg command for Windows screen capture with NVENC
            // Using gdigrab (built-in Windows screen capture)
            string args = $"-f gdigrab -framerate {config.Fps} -i desktop " +
                         $"-c:v {config.Encoder} " +
                         $"-preset p4 " + // NVENC preset (p1=fastest, p7=slowest)
                         $"-b:v {config.Bitrate}k " +
                         $"-maxrate {config.Bitrate}k " +
                         $"-bufsize {config.Bitrate * 2}k " +
                         $"-pix_fmt yuv420p " +
                         $"-s {width}x{height} " +
                         $"-t {duration} " +
                         $"-y \"{outputPath}\"";

            return args;
        }

        /// <summary>
        /// Called when a segment finishes recording
        /// </summary>
        private void OnSegmentFinished(object? sender, EventArgs e)
        {
            if (File.Exists(currentSegmentPath))
            {
                // Add segment to buffer
                var segment = new SegmentInfo
                {
                    FilePath = currentSegmentPath,
                    CreatedAt = lastSegmentTime,
                    DurationSeconds = segmentDurationSeconds
                };

                segments.Enqueue(segment);
                Console.WriteLine($"✓ Segment saved: {Path.GetFileName(currentSegmentPath)}");

                // Clean up old segments (keep only buffer duration)
                CleanupOldSegments();
            }

            // Start next segment if still recording
            if (isRecording)
            {
                RecordNextSegment();
            }
        }

        /// <summary>
        /// Remove segments older than buffer duration
        /// </summary>
        private void CleanupOldSegments()
        {
            double totalDuration = segments.Sum(s => s.DurationSeconds);

            while (totalDuration > config.BufferDurationSeconds && segments.Count > 0)
            {
                var oldSegment = segments.Dequeue();
                
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

                totalDuration = segments.Sum(s => s.DurationSeconds);
            }
        }

        /// <summary>
        /// Save the last N seconds from the buffer
        /// </summary>
        public string SaveClip(int clipLengthSeconds, string clipName = "")
        {
            if (segments.Count == 0)
            {
                throw new Exception("No segments available in buffer");
            }

            // Calculate which segments we need
            var neededSegments = new List<SegmentInfo>();
            double collectedDuration = 0;

            // Go backwards through segments
            foreach (var segment in segments.Reverse())
            {
                neededSegments.Insert(0, segment);
                collectedDuration += segment.DurationSeconds;

                if (collectedDuration >= clipLengthSeconds)
                    break;
            }

            if (neededSegments.Count == 0)
            {
                throw new Exception("Not enough segments in buffer");
            }

            // generate output filename with app name detection
            string filename;
            if (string.IsNullOrEmpty(clipName))
            {
                filename = WindowDetector.GetClipFilename();
            }
            else
            {
                filename = SanitizeFilename(clipName);
            }

            string outputPath = Path.Combine(config.TempFolder, $"{filename}.mp4");



            // Concatenate segments using FFmpeg
            ConcatenateSegments(neededSegments, outputPath, clipLengthSeconds);

            Console.WriteLine($"✓ Clip saved: {outputPath}");
            return outputPath;
        }

        /// <summary>
        /// Concatenate multiple segments into one file
        /// </summary>
        private void ConcatenateSegments(List<SegmentInfo> segments, string outputPath, int targetDuration)
        {
            // Create concat file for FFmpeg
            string concatFilePath = Path.Combine(config.TempFolder, "concat_list.txt");
            
            using (var writer = new StreamWriter(concatFilePath))
            {
                foreach (var segment in segments)
                {
                    writer.WriteLine($"file '{segment.FilePath}'");
                }
            }

            // FFmpeg concat + trim to exact duration
            string args = $"-f concat -safe 0 -i \"{concatFilePath}\" " +
                         $"-t {targetDuration} " +
                         $"-c copy " + // Copy without re-encoding (fast)
                         $"-y \"{outputPath}\"";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit();

            // Clean up concat file
            try { File.Delete(concatFilePath); } catch { }

            if (process.ExitCode != 0)
            {
                throw new Exception($"FFmpeg failed with exit code {process.ExitCode}");
            }
        }

        /// <summary>
        /// Get current buffer status
        /// </summary>
        public (int segmentCount, double totalDuration) GetBufferStatus()
        {
            double totalDuration = segments.Sum(s => s.DurationSeconds);
            return (segments.Count, totalDuration);
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