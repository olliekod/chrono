using System;
using System.Linq;
using System.Management;

namespace ChronoRecorder
{
    /// <summary>
    /// automatically detects the installed GPU and returns best FFmpeg encoder option
    /// </summary>
    public class GpuDetector
    {
        public enum GpuType
        {
            NVIDIA,
            AMD,
            Intel,
            Unknown
        }

        public class GpuInfo
        {
            public GpuType Type { get; set; }
            public string Name { get; set; }
            public string Encoder { get; set; }
        }


        // the detector
        public static GpuInfo DetectGpu()
        {
            try
            {
                // Query Windows Management Instrumentation for video controllers
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
                {
                    // get each object from win32_videocontroller
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        // parse
                        string name = obj["Name"]?.ToString() ?? "";
                        string driverVersion = obj["DriverVersion"]?.ToString() ?? "";
                        
                        Console.WriteLine($"Found GPU: {name}");

                        // Check for nvidia gpu using string comparisons
                        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("GTX", StringComparison.OrdinalIgnoreCase))
                        {
                            return new GpuInfo
                            {
                                Type = GpuType.NVIDIA,
                                Name = name,
                                Encoder = "h264_nvenc"
                            };
                        }

                        // Check for AMD gpu through common string comparisons
                        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("RX ", StringComparison.OrdinalIgnoreCase))
                        {
                            return new GpuInfo
                            {
                                Type = GpuType.AMD,
                                Name = name,
                                Encoder = "h264_amf"
                            };
                        }

                        // Check for Intel (none of my friends are on integrated so we're chilling but I added for completions sake)
                        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Iris", StringComparison.OrdinalIgnoreCase))
                        {
                            return new GpuInfo
                            {
                                Type = GpuType.Intel,
                                Name = name,
                                Encoder = "h264_qsv"
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ GPU detection failed: {ex.Message}");
            }

            // Fallback to software encoding
            Console.WriteLine("No hardware encoder detected, using software encoding (slower)");
            return new GpuInfo
            {
                Type = GpuType.Unknown,
                Name = "Unknown",
                Encoder = "libx264"
            };
        }

        /// <summary>
        /// verify that FFmpeg supports the detected encoder
        /// </summary>
        public static bool VerifyEncoder(string encoder)
        {
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-encoders",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                bool supported = output.Contains(encoder);
                
                if (supported)
                {
                    Console.WriteLine($"✓ Encoder '{encoder}' is supported by FFmpeg");
                }
                else
                {
                    Console.WriteLine($"✗ Encoder '{encoder}' is NOT supported by FFmpeg");
                }

                return supported;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Could not verify encoder: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get encoder with fallback if primary not available
        /// </summary>
        public static string GetBestEncoder()
        {
            var gpu = DetectGpu();
            
            // Try detected encoder first
            if (VerifyEncoder(gpu.Encoder))
            {
                Console.WriteLine($"✓ Using {gpu.Type} hardware encoding: {gpu.Encoder}");
                return gpu.Encoder;
            }

            // Fallback to software encoding
            Console.WriteLine("⚠ Hardware encoding not available, falling back to software encoding");
            return "libx264";
        }
    }
}