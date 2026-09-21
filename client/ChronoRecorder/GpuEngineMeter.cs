using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;

namespace ChronoRecorder
{
    /// <summary>
    /// Names in Windows' "GPU Engine" counters look like
    /// <c>pid_12345_luid_0x00000000_0x0000D3A4_phys_0_eng_3_engtype_VideoEncode</c>: which process, which graphics
    /// chip, which engine on it, and what kind of engine (3D, VideoEncode, VideoDecode, Copy, Compute_0...).
    /// </summary>
    public static class GpuEngineName
    {
        private static readonly Regex Format = new(@"^pid_(?<pid>\d+)_.*_engtype_(?<type>.+)$", RegexOptions.Compiled);

        public static bool TryParse(string? name, out int pid, out string engineType)
        {
            pid = 0;
            engineType = "";
            var m = Format.Match(name ?? "");
            if (!m.Success || !int.TryParse(m.Groups["pid"].Value, out pid)) return false;
            engineType = m.Groups["type"].Value;
            return true;
        }
    }

    /// <summary>
    /// How busy each part of the graphics card is because of one process, as Task Manager's GPU columns show it.
    /// Read through WMI, so it needs nothing installed and works for NVIDIA, AMD and Intel alike. It costs a query
    /// each time, so only the Diagnostics page asks, and only while it is open.
    /// </summary>
    public static class GpuEngineMeter
    {
        private const string Class = "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine";
        private static bool unavailableLogged;

        /// <summary>Busy percentage per kind of engine ("3D", "VideoEncode"...) for the process, or null when Windows doesn't offer the counters.</summary>
        public static IReadOnlyDictionary<string, double>? Read(int pid)
        {
            try
            {
                var busiest = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                // LIKE's underscore matches any character, so the exact pid is checked again when parsing.
                using var searcher = new ManagementObjectSearcher($"SELECT Name, UtilizationPercentage FROM {Class} WHERE Name LIKE 'pid_{pid}_%'");
                foreach (ManagementBaseObject row in searcher.Get())
                {
                    if (!GpuEngineName.TryParse(row["Name"]?.ToString(), out int owner, out string type) || owner != pid) continue;

                    double percent = Convert.ToDouble(row["UtilizationPercentage"] ?? 0);

                    // A card can have several engines of one kind (two video encoders). What matters is how busy the
                    // busiest one is, since that is the one that saturates first.
                    busiest[type] = busiest.TryGetValue(type, out double before) ? Math.Max(before, percent) : percent;
                }
                return busiest;
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                if (!unavailableLogged)
                {
                    unavailableLogged = true;
                    Console.WriteLine($"GPU usage counters aren't available on this PC: {ex.Message}");
                }
                return null;
            }
        }
    }
}
