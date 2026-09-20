using System;
using System.Drawing;
using System.Text.RegularExpressions;

namespace ChronoRecorder
{
    /// <summary>
    /// Works out the size a clip is recorded at. "Native" is by far the cheapest: frames go from the GPU
    /// straight into the encoder. Any smaller size needs a CPU scaling step, which costs roughly a core.
    /// </summary>
    public static class CaptureSizing
    {
        /// <summary>
        /// The output size for a resolution setting ("native", or something like "1920x1080") on a monitor of
        /// <paramref name="native"/> pixels. The setting's height picks the quality and the monitor's aspect
        /// ratio is kept, so 1080p on an ultrawide is 2580x1080 rather than a squashed 1920x1080.
        /// Never upscales.
        /// </summary>
        public static Size Resolve(string? setting, Size native)
        {
            if (!TryGetHeight(setting, out int targetHeight) || targetHeight >= native.Height)
                return native;

            int width = Even((int)Math.Round(native.Width * (double)targetHeight / native.Height));
            return new Size(width, Even(targetHeight));
        }

        /// <summary>"1080p60", or "Native60" when the setting isn't a fixed size.</summary>
        public static string QualityLabel(string? setting, int fps)
            => TryGetHeight(setting, out int height) ? $"{height}p{fps}" : $"Native{fps}";

        /// <summary>"1920x1080", the form the upload server accepts.</summary>
        public static string Describe(Size size) => $"{size.Width}x{size.Height}";

        private static bool TryGetHeight(string? setting, out int height)
        {
            height = 0;
            var match = Regex.Match(setting ?? "", @"^\s*(\d+)x(\d+)\s*$", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[2].Value, out height) && height > 0;
        }

        private static int Even(int value) => value - (value % 2);
    }
}
