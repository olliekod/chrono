using System;
using System.Drawing;

namespace ChronoRecorder
{
    /// <summary>
    /// Picks how many kilobits per second to encode with. Quality depends on bits per pixel, so a fixed number
    /// is wrong for every size and frame rate but one: 8000 kbps, fine for 1080p30, leaves 1440p60 with big
    /// visible blocks in anything that moves.
    /// </summary>
    public static class BitrateSizing
    {
        /// <summary>Bits per pixel per frame that keep game footage clean. 1440p60 comes to about 20 Mbps, 1080p60 to 11.</summary>
        private const double BitsPerPixel = 0.09;

        private const int MinKbps = 6000;
        private const int MaxKbps = 40000;

        /// <summary>Old configs saved the former default of 8000 without the user having chosen it.</summary>
        public const int LegacyDefaultKbps = 8000;

        /// <summary>The configured bitrate, or (when it is 0, meaning automatic) one that suits the picture size and frame rate.</summary>
        public static int Resolve(int configuredKbps, Size size, int fps)
        {
            if (configuredKbps > 0) return configuredKbps;

            double kbps = size.Width * (double)size.Height * Math.Max(fps, 1) * BitsPerPixel / 1000.0;
            int rounded = (int)(Math.Round(kbps / 500.0) * 500);
            return Math.Clamp(rounded, MinKbps, MaxKbps);
        }

        /// <summary>Same, for a size written like "1920x1080". An unreadable size is treated as 1080p.</summary>
        public static int Resolve(int configuredKbps, string? sizeText, int fps)
        {
            var parts = (sizeText ?? "").Split('x');
            bool ok = parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0;
            return Resolve(configuredKbps, ok ? new Size(int.Parse(parts[0]), int.Parse(parts[1])) : new Size(1920, 1080), fps);
        }
    }
}
