using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;

namespace ChronoRecorder
{
    // ==================================================================== what the recorder knows

    /// <summary>Everything the recorder can say about the recording in progress, for the Diagnostics page.</summary>
    public sealed record RecorderFacts(
        bool Recording, string Target, CaptureMethod? Method, Size Size, int ConfiguredFps, int EffectiveFps, string Encoder,
        int LoadLevel, string LoadSetting, bool GovernorActive, int BitrateKbps, double BufferSeconds, int FfmpegPid, DateTime? StartedUtc,
        EncodeStats? Stats, string EncoderNote, IReadOnlyList<string> AudioSources, IReadOnlyList<string> Events,
        string ScreenAdapterName, uint ScreenAdapterVendorId);

    public interface IDiagnosticsSource
    {
        RecorderFacts GetFacts();
    }

    // ========================================================================== this PC

    public sealed record GpuFact(string Name, string Driver, long VideoMemoryBytes);

    /// <summary>What kind of PC this is. Read once (it doesn't change while Chrono runs) and remembered.</summary>
    public sealed record SystemFacts(string Windows, string Cpu, int LogicalCores, double RamGb, IReadOnlyList<GpuFact> Gpus, string Ffmpeg)
    {
        private static SystemFacts? cached;
        private static readonly object gate = new object();

        public static SystemFacts Get()
        {
            lock (gate) return cached ??= Read();
        }

        private static SystemFacts Read()
        {
            string windows = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            string cpu = "unknown";
            double ramGb = 0;
            var gpus = new List<GpuFact>();

            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Caption, BuildNumber FROM Win32_OperatingSystem"))
                    foreach (ManagementBaseObject o in s.Get())
                        windows = $"{(o["Caption"]?.ToString() ?? "Windows").Replace("Microsoft ", "").Trim()} (build {o["BuildNumber"]})";

                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                    foreach (ManagementBaseObject o in s.Get()) { cpu = Regex1(o["Name"]?.ToString() ?? cpu); break; }

                using (var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                    foreach (ManagementBaseObject o in s.Get()) ramGb = Convert.ToDouble(o["TotalPhysicalMemory"] ?? 0) / (1024.0 * 1024 * 1024);

                var adapters = MonitorLocator.Adapters();
                using var video = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
                foreach (ManagementBaseObject o in video.Get())
                {
                    string name = o["Name"]?.ToString() ?? "";
                    if (name.Length == 0) continue;
                    long vram = adapters.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?.VideoMemoryBytes ?? 0;
                    gpus.Add(new GpuFact(name, DriverVersions.Friendly(name, o["DriverVersion"]?.ToString()), vram));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Couldn't read every detail of this PC: {ex.Message}");
            }

            return new SystemFacts(windows, cpu, Environment.ProcessorCount, ramGb, gpus, ReadFfmpegVersion());
        }

        // CPU names come padded with runs of spaces ("Intel(R) Core(TM) i7-13700K   CPU @ 3.40GHz").
        private static string Regex1(string text) => System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");

        private static string ReadFfmpegVersion()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = FfmpegLocator.Path, Arguments = "-version", UseShellExecute = false,
                    RedirectStandardOutput = true, CreateNoWindow = true
                });
                if (p == null) return "unknown";
                string first = p.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(3000);
                var m = System.Text.RegularExpressions.Regex.Match(first, @"version\s+(\S+)");
                return m.Success ? m.Groups[1].Value : "unknown";
            }
            catch { return "unknown"; }
        }
    }

    public static class DriverVersions
    {
        /// <summary>
        /// Windows reports an NVIDIA driver as "32.0.15.7602", but everyone knows it as 576.02: the last five digits of the
        /// last two groups. Other vendors' numbers are shown as Windows gives them.
        /// </summary>
        public static string Friendly(string gpuName, string? windowsVersion)
        {
            string version = (windowsVersion ?? "").Trim();
            if (version.Length == 0) return "unknown";

            bool nvidia = gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase);
            var parts = version.Split('.');
            if (!nvidia || parts.Length < 4) return version;

            string digits = new string((parts[2] + parts[3]).Where(char.IsDigit).ToArray());
            if (digits.Length < 5) return version;
            digits = digits.Substring(digits.Length - 5);
            return $"{digits.Substring(0, 3)}.{digits.Substring(3)}";
        }
    }

    // ===================================================================== reading the numbers

    public static class CpuSampler
    {
        /// <summary>How much of one processor core a process used between two readings, as a percentage (100 = one core fully busy).</summary>
        public static double PercentOfOneCore(TimeSpan cpuBefore, DateTime before, TimeSpan cpuNow, DateTime now)
        {
            double wall = (now - before).TotalSeconds;
            if (wall <= 0.05) return 0;
            return Math.Max(0, (cpuNow - cpuBefore).TotalSeconds / wall * 100.0);
        }
    }

    // ===================================================================== what to tell people

    public sealed record DiagRow(string Label, string Value, string Tone = "");
    public sealed record DiagSection(string Title, IReadOnlyList<DiagRow> Rows);
    public sealed record DiagFinding(string Tone, string Text);
    public sealed record DiagnosticsDto(
        string Version, DateTime AtUtc, IReadOnlyList<DiagSection> Sections, IReadOnlyList<DiagFinding> Findings, IReadOnlyList<string> Events);

    /// <summary>The numbers the findings are worked out from. Plain values, so the rules can be tested without a PC.</summary>
    public sealed record FindingInputs(
        bool Recording, string Encoder, string EncoderNote, GpuDetector.GpuType GpuType, GpuDetector.GpuTier GpuTier, CaptureMethod? Method,
        double Speed, long Dropped, double FfmpegCpuOneCore, double EncodeEnginePercent, double ThreeDEnginePercent,
        int LoadLevel, int ConfiguredFps, uint ScreenAdapterVendorId, string ScreenAdapterName, double RamGb, string GpuName);

    public static class DiagnosticsFindings
    {
        public static IReadOnlyList<DiagFinding> Evaluate(FindingInputs f)
        {
            var found = new List<DiagFinding>();

            if (f.EncoderNote.Length > 0)
                found.Add(new DiagFinding("bad", $"The graphics card's video encoder isn't working, so Chrono records on the processor. {f.EncoderNote}"));
            else if (f.Encoder == "libx264" && f.GpuType != GpuDetector.GpuType.Unknown)
                found.Add(new DiagFinding("warn", "Recording is being encoded by the processor although this PC has a graphics card with an encoder. That is much heavier on the game."));

            if (f.Recording && f.Speed > 0 && f.Speed < 0.95)
                found.Add(new DiagFinding("bad", $"The encoder is behind, working at {f.Speed:0.00}x real time. Clips may stutter or skip."));

            if (f.Recording && f.Dropped > 0)
                found.Add(new DiagFinding("warn", $"{f.Dropped} frames have been dropped since this recording started."));

            if (f.LoadLevel > 0)
                found.Add(new DiagFinding("info", $"Recording is running at reduced load ({LoadPlan.Describe(f.LoadLevel, f.ConfiguredFps, LoadPlan.HasPresetStep(f.Encoder))})."));

            if (f.Recording && f.EncodeEnginePercent >= 80)
                found.Add(new DiagFinding("warn", $"The graphics card's video encoder is {f.EncodeEnginePercent:0}% busy, which leaves little room."));

            if (f.Recording && f.ThreeDEnginePercent >= 60)
                found.Add(new DiagFinding("warn", $"Recording is using {f.ThreeDEnginePercent:0}% of the graphics card's 3D engine, which the game needs too."));

            if (f.Recording && f.FfmpegCpuOneCore >= 50)
                found.Add(new DiagFinding("warn", $"Recording is using {f.FfmpegCpuOneCore:0}% of a processor core. Normally it uses a few percent."));

            if (f.Recording && f.Method == CaptureMethod.Gdi)
                found.Add(new DiagFinding("warn", "The screen is being captured through GDI, which is heavy on the processor. Windows uses it for rotated screens and screens on a second graphics card."));

            // A laptop's own screen is often wired to the Intel chip while the NVIDIA card does the drawing. Recording
            // then has to move frames between chips, which can fail or cost performance.
            if (f.Recording && f.ScreenAdapterVendorId != 0 && f.GpuType != GpuDetector.GpuType.Unknown)
            {
                var screenVendor = f.ScreenAdapterVendorId switch { 0x10DE => GpuDetector.GpuType.NVIDIA, 0x1002 or 0x1022 => GpuDetector.GpuType.AMD, 0x8086 => GpuDetector.GpuType.Intel, _ => GpuDetector.GpuType.Unknown };
                if (screenVendor != GpuDetector.GpuType.Unknown && screenVendor != f.GpuType)
                    found.Add(new DiagFinding("warn", $"The screen is driven by {f.ScreenAdapterName} but recording encodes on {f.GpuName}. On laptops that can make recording fail or run slowly."));
            }

            if (f.RamGb > 0 && f.RamGb < 8)
                found.Add(new DiagFinding("info", $"This PC has {f.RamGb:0.#} GB of memory, which is low for gaming and recording together."));

            if (found.Count == 0)
                found.Add(new DiagFinding("good", f.Recording ? "Nothing needs attention." : "Nothing is being recorded right now, so there is nothing to measure yet."));

            return found;
        }
    }

    public static class DiagnosticsText
    {
        public static string EncoderName(string encoder) => EncoderProfile.Normalize(encoder) switch
        {
            "h264_nvenc" => "NVIDIA NVENC H.264",
            "h264_amf" => "AMD AMF H.264",
            "h264_qsv" => "Intel Quick Sync H.264",
            _ => "x264 on the processor"
        };

        public static string CaptureName(CaptureMethod? method) => method switch
        {
            CaptureMethod.WindowCapture => "Windows Graphics Capture (the game's window)",
            CaptureMethod.DesktopDuplication => "Desktop Duplication (GPU screen capture)",
            CaptureMethod.Gdi => "GDI (processor screen capture)",
            _ => "none"
        };

        public static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):0} MB";

        public static string Mbps(double kbps) => $"{kbps / 1000.0:0.0} Mbps";

        /// <summary>"2 min 20 sec", "45 sec", "1 h 05 min".</summary>
        public static string Span(double seconds)
        {
            int total = (int)Math.Round(Math.Max(0, seconds));
            if (total >= 3600) return $"{total / 3600} h {total % 3600 / 60:00} min";
            if (total >= 60) return $"{total / 60} min {total % 60:00} sec";
            return $"{total} sec";
        }

        /// <summary>A report to paste into a chat: the same rows the page shows, as plain text.</summary>
        public static string Report(DiagnosticsDto d)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Chrono {d.Version} diagnostics, {d.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
            foreach (var section in d.Sections)
            {
                sb.AppendLine();
                sb.AppendLine($"[{section.Title}]");
                foreach (var row in section.Rows) sb.AppendLine($"{row.Label}: {row.Value}");
            }

            sb.AppendLine();
            sb.AppendLine("[Findings]");
            foreach (var finding in d.Findings) sb.AppendLine($"- {finding.Text}");

            if (d.Events.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("[Recent events]");
                foreach (var e in d.Events) sb.AppendLine(e);
            }
            return sb.ToString().TrimEnd();
        }
    }

    // ======================================================================== the collector

    /// <summary>
    /// Builds the Diagnostics page. Nothing here runs unless the page is open: the numbers that cost something to read
    /// (GPU usage through WMI, a look at FFmpeg's process) are read only when the page asks.
    /// </summary>
    public sealed class DiagnosticsCollector
    {
        private readonly IDiagnosticsSource source;
        private readonly DateTime appStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

        private (int Pid, TimeSpan Cpu, DateTime At)? lastFfmpeg;

        public DiagnosticsCollector(IDiagnosticsSource source) => this.source = source;

        public static string AppVersion
        {
            get
            {
                string? v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                return (v ?? "unknown").Split('+')[0];
            }
        }

        public DiagnosticsDto Collect()
        {
            var facts = source.GetFacts();
            var system = SystemFacts.Get();
            var now = DateTime.UtcNow;

            // ---- FFmpeg's own footprint
            double cpuOneCore = 0;
            string cpuText = "not recording", ramText = "not recording";
            IReadOnlyDictionary<string, double>? engines = null;
            bool measuring = false;

            if (facts.Recording && facts.FfmpegPid > 0)
            {
                try
                {
                    using var p = Process.GetProcessById(facts.FfmpegPid);
                    var cpu = p.TotalProcessorTime;
                    ramText = DiagnosticsText.Mb(p.WorkingSet64);

                    if (lastFfmpeg is { } before && before.Pid == facts.FfmpegPid)
                    {
                        cpuOneCore = CpuSampler.PercentOfOneCore(before.Cpu, before.At, cpu, now);
                        cpuText = $"{cpuOneCore / system.LogicalCores:0.0}% of the processor ({cpuOneCore:0.0}% of one core)";
                    }
                    else
                    {
                        cpuText = "measuring...";
                        measuring = true;
                    }
                    lastFfmpeg = (facts.FfmpegPid, cpu, now);
                    engines = GpuEngineMeter.Read(facts.FfmpegPid);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    cpuText = ramText = "not available";   // FFmpeg ended between asking and looking
                }
            }
            else
            {
                lastFfmpeg = null;
            }

            double Engine(string type) => engines != null && engines.TryGetValue(type, out double v) ? v : -1;
            double encode = Engine("VideoEncode"), threeD = Engine("3D");

            string EngineText(double v) => engines == null ? "not available on this PC" : v < 0 ? "0%" : $"{v:0}%";

            using var self = Process.GetCurrentProcess();

            // ---- the rows
            var stats = facts.Stats;
            string speedText = !facts.Recording ? "not recording" : stats == null ? "measuring..." : $"{stats.Speed:0.00}x real time";
            string Tone(bool bad, bool warn = false) => bad ? "bad" : warn ? "warn" : "good";

            var recording = new List<DiagRow>
            {
                new("Status", facts.Recording ? $"Recording {facts.Target}" : "Not recording"),
                new("Capture", facts.Recording ? DiagnosticsText.CaptureName(facts.Method) : "none"),
                new("Encoder", DiagnosticsText.EncoderName(facts.Encoder)),
                new("Input", facts.Recording && facts.Size.Width > 0
                    ? $"{facts.Size.Width}×{facts.Size.Height} @ {facts.EffectiveFps}" + (facts.EffectiveFps != facts.ConfiguredFps ? $" (set to {facts.ConfiguredFps})" : "")
                    : "none"),
                new("Bitrate", facts.Recording ? $"{DiagnosticsText.Mbps(facts.BitrateKbps)} target" + (stats != null && stats.BitrateKbps > 0 ? $", {DiagnosticsText.Mbps(stats.BitrateKbps)} measured" : "") : "none"),
                new("Buffer", facts.Recording ? DiagnosticsText.Span(facts.BufferSeconds) : "none"),
                new("Load", $"{LoadPlan.Describe(facts.LoadLevel, facts.ConfiguredFps, LoadPlan.HasPresetStep(facts.Encoder))}{(facts.LoadSetting == "auto" ? " (automatic)" : $" ({facts.LoadSetting})")}"),
            };

            var performance = new List<DiagRow>
            {
                new("FFmpeg CPU", cpuText, facts.Recording && !measuring ? Tone(cpuOneCore >= 50, cpuOneCore >= 25) : ""),
                new("FFmpeg RAM", ramText),
                new("Chrono RAM", DiagnosticsText.Mb(self.WorkingSet64)),
                new("GPU video encode", facts.Recording ? EngineText(encode) : "not recording", facts.Recording && encode >= 0 ? Tone(encode >= 80, encode >= 50) : ""),
                new("GPU 3D (recording's share)", facts.Recording ? EngineText(threeD) : "not recording", facts.Recording && threeD >= 0 ? Tone(threeD >= 60, threeD >= 35) : ""),
                new("Encoding speed", speedText, stats != null ? Tone(stats.Speed > 0 && stats.Speed < 0.95, stats.Speed > 0 && stats.Speed < 0.99) : ""),
                new("Encoded FPS", stats == null ? (facts.Recording ? "measuring..." : "not recording") : $"{stats.Fps:0.0}"),
                new("Dropped frames", stats == null ? (facts.Recording ? "measuring..." : "not recording") : stats.DroppedFrames.ToString(CultureInfo.InvariantCulture), stats != null ? Tone(stats.DroppedFrames > 0) : ""),
                new("Repeated frames", stats == null ? (facts.Recording ? "measuring..." : "not recording") : $"{stats.DuplicatedFrames} (normal while the game isn't drawing)"),
            };

            var pc = new List<DiagRow>
            {
                new("Chrono", $"{AppVersion}, running for {DiagnosticsText.Span((now - appStartedUtc).TotalSeconds)}"),
                new("Windows", system.Windows),
                new("Processor", $"{system.Cpu} ({system.LogicalCores} threads)"),
                new("Memory", system.RamGb > 0 ? $"{system.RamGb:0.#} GB" : "unknown"),
            };
            foreach (var gpu in system.Gpus)
                pc.Add(new DiagRow("Graphics", $"{gpu.Name}{(gpu.VideoMemoryBytes > 0 ? $", {gpu.VideoMemoryBytes / (1024.0 * 1024 * 1024):0.#} GB" : "")}, driver {gpu.Driver}"));

            var screens = MonitorLocator.Enumerate();
            foreach (var m in screens)
                pc.Add(new DiagRow("Screen", $"{m.Bounds.Width}×{m.Bounds.Height}{(m.Rotated ? " rotated" : "")} on {(m.AdapterName.Length > 0 ? m.AdapterName : $"graphics chip {m.AdapterIndex}")}"));

            pc.Add(new DiagRow("FFmpeg", system.Ffmpeg));
            pc.Add(new DiagRow("Sound", facts.AudioSources.Count == 0 ? "none recorded" : string.Join(", ", facts.AudioSources)));

            var gpuInfo = GpuDetector.Detected;
            var findings = DiagnosticsFindings.Evaluate(new FindingInputs(
                facts.Recording, facts.Encoder, facts.EncoderNote,
                gpuInfo?.Type ?? GpuDetector.GpuType.Unknown, gpuInfo?.Tier ?? GpuDetector.GpuTier.Modest, facts.Method,
                stats?.Speed ?? 0, stats?.DroppedFrames ?? 0, cpuOneCore, Math.Max(encode, 0), Math.Max(threeD, 0),
                facts.LoadLevel, facts.ConfiguredFps, facts.ScreenAdapterVendorId, facts.ScreenAdapterName, system.RamGb, gpuInfo?.Name ?? ""));

            return new DiagnosticsDto(
                AppVersion, now,
                new[] { new DiagSection("Recording", recording), new DiagSection("Performance", performance), new DiagSection("This PC", pc) },
                findings, facts.Events);
        }

        /// <summary>The report as text, with nothing personal in it: no name, folders, server address or key.</summary>
        public string Report() => DiagnosticsText.Report(Collect());
    }
}
