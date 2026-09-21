using System;
using System.Collections.Generic;
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
                // Every display adapter, then the best encoder among them. A laptop usually lists its Intel
                // graphics as well as its NVIDIA or AMD card, in whatever order WMI feels like, so taking the
                // first match would hand a machine with a real graphics card the weakest encoder it has.
                var found = new List<GpuInfo>();

                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString() ?? "";
                        if (name.Length == 0) continue;
                        Console.WriteLine($"Found GPU: {name}");
                        found.Add(Identify(name));
                    }
                }

                // NVENC first (the only path with a GPU decoder for exports too), then AMD, then Intel.
                foreach (var type in new[] { GpuType.NVIDIA, GpuType.AMD, GpuType.Intel })
                {
                    var match = found.FirstOrDefault(g => g.Type == type);
                    if (match != null) return match;
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

        /// <summary>Which kind of graphics card a Windows adapter name describes, and the encoder it brings.</summary>
        public static GpuInfo Identify(string name)
        {
            if (Mentions(name, "NVIDIA", "GeForce", "RTX", "GTX"))
                return new GpuInfo { Type = GpuType.NVIDIA, Name = name, Encoder = "h264_nvenc" };

            if (Mentions(name, "AMD", "Radeon", "RX "))
                return new GpuInfo { Type = GpuType.AMD, Name = name, Encoder = "h264_amf" };

            // None of the friend group is on integrated graphics, but it costs nothing to support.
            if (Mentions(name, "Intel", "UHD Graphics", "Iris"))
                return new GpuInfo { Type = GpuType.Intel, Name = name, Encoder = "h264_qsv" };

            return new GpuInfo { Type = GpuType.Unknown, Name = name, Encoder = "libx264" };
        }

        private static bool Mentions(string name, params string[] words)
            => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// verify that FFmpeg supports the detected encoder
        /// </summary>
        public static bool VerifyEncoder(string encoder)
        {
            try
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = FfmpegLocator.Path,
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