using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

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

        /// <summary>
        /// Roughly how much room a graphics card has to spare for recording while a game runs. A guess made from the
        /// card's name, used only to choose where "automatic" starts; the load governor corrects it from measurements.
        /// </summary>
        public enum GpuTier
        {
            Capable,
            Modest
        }

        public class GpuInfo
        {
            public GpuType Type { get; set; }
            public string Name { get; set; } = "";
            public string Encoder { get; set; } = "libx264";
            public GpuTier Tier { get; set; } = GpuTier.Modest;
        }

        /// <summary>The card chosen by the last detection, for the Diagnostics page.</summary>
        public static GpuInfo? Detected { get; private set; }

        /// <summary>Why the hardware encoder isn't being used, or "" when it is (or was never tried). Shown on the Diagnostics page.</summary>
        public static string ProbeNote { get; private set; } = "";


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
                return new GpuInfo { Type = GpuType.NVIDIA, Name = name, Encoder = "h264_nvenc", Tier = TierOf(GpuType.NVIDIA, name) };

            if (Mentions(name, "AMD", "Radeon", "RX "))
                return new GpuInfo { Type = GpuType.AMD, Name = name, Encoder = "h264_amf", Tier = TierOf(GpuType.AMD, name) };

            // None of the friend group is on integrated graphics, but it costs nothing to support.
            if (Mentions(name, "Intel", "UHD Graphics", "Iris"))
                return new GpuInfo { Type = GpuType.Intel, Name = name, Encoder = "h264_qsv", Tier = TierOf(GpuType.Intel, name) };

            return new GpuInfo { Type = GpuType.Unknown, Name = name, Encoder = "libx264", Tier = GpuTier.Modest };
        }

        /// <summary>
        /// Whether a card is on the modest side, from its name. NVIDIA: the GTX 9/10/16 series, RTX 20 series and the
        /// entry cards of the 30 series (the 3050 and 3060, but not the 3070) and of the 40 and 50 series (the xx50) are modest, as is
        /// anything called MX or GT. AMD: the RX 400/500 series, the 5500 to 7600 classes of the 5000 to 7000 series, and integrated
        /// Radeon. Intel: integrated graphics and Arc A3/A5. Anything unrecognised is treated as modest, because starting
        /// light costs a little quality and starting heavy on a weak card costs the game its frame rate.
        /// </summary>
        public static GpuTier TierOf(GpuType type, string name)
        {
            switch (type)
            {
                case GpuType.NVIDIA:
                {
                    if (Regex.IsMatch(name, @"\b(MX|GT)\s*\d", RegexOptions.IgnoreCase)) return GpuTier.Modest;

                    var m = Regex.Match(name, @"\b(?:GTX|RTX)\s*(\d{3,4})\b", RegexOptions.IgnoreCase);
                    if (!m.Success) return GpuTier.Capable;   // a workstation card or a name we don't know: assume it is fine

                    int number = int.Parse(m.Groups[1].Value);
                    int series = number / 100;   // 1650 -> 16, 3060 -> 30, 960 -> 9
                    int model = number % 100;                                       // 50, 60, 70, 80, 90
                    if (series <= 20) return GpuTier.Modest;

                    // The 30 series goes up to the 3060 (a 3060 Ti is the same class); later series' entry cards are the
                    // xx50 ones, since an RTX 4060 is already faster than a 3060 Ti.
                    int ceiling = series == 30 ? 60 : 50;
                    return model != 0 && model <= ceiling ? GpuTier.Modest : GpuTier.Capable;
                }

                case GpuType.AMD:
                {
                    var m = Regex.Match(name, @"\bRX\s*(\d{3,4})\b", RegexOptions.IgnoreCase);
                    if (!m.Success) return GpuTier.Modest;   // Vega, "Radeon Graphics": integrated
                    int number = int.Parse(m.Groups[1].Value);
                    if (number < 1000) return GpuTier.Modest;   // RX 400 and 500 series
                    int tier = number / 100 % 10;                // the hundreds digit is the class: 5500, 6400 and 6600 are low, 6800 and 7900 high
                    return tier <= 6 ? GpuTier.Modest : GpuTier.Capable;
                }

                case GpuType.Intel:
                    return Regex.IsMatch(name, @"\bArc\b.*\bA7\d\d\b", RegexOptions.IgnoreCase) ? GpuTier.Capable : GpuTier.Modest;

                default:
                    return GpuTier.Modest;
            }
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
        /// The encoder to record with: the best graphics card's hardware encoder if it really starts, otherwise the
        /// processor. Being built into FFmpeg is not enough (see <see cref="EncoderProbe"/>), so it is started once for real.
        /// </summary>
        public static string GetBestEncoder()
        {
            var gpu = DetectGpu();
            Detected = gpu;
            ProbeNote = "";

            if (gpu.Encoder == "libx264")
            {
                Console.WriteLine("⚠ No hardware encoder for this graphics card; using the processor");
                return "libx264";
            }

            if (!VerifyEncoder(gpu.Encoder))
            {
                ProbeNote = $"FFmpeg has no {gpu.Encoder} encoder.";
                Console.WriteLine("⚠ Hardware encoding not available, falling back to software encoding");
                return "libx264";
            }

            var probe = EncoderProbe.Test(gpu.Encoder);
            if (!probe.Ok)
            {
                ProbeNote = probe.Message;
                Console.WriteLine($"⚠ The {gpu.Encoder} encoder won't start on this PC: {probe.Message}");
                Console.WriteLine("  Recording will use the processor instead.");
                return "libx264";
            }

            Console.WriteLine($"✓ Using {gpu.Type} hardware encoding: {gpu.Encoder} ({gpu.Tier.ToString().ToLowerInvariant()} card)");
            return gpu.Encoder;
        }
    }
}
