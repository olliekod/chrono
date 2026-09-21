using System.Globalization;
using System.Text.RegularExpressions;

namespace ChronoRecorder
{
    /// <summary>
    /// Reads the facts FFmpeg prints when it opens a file (<c>ffmpeg -i file</c>). This replaces shipping a second
    /// 140 MB program (ffprobe) just to learn a clip's length and picture size.
    /// </summary>
    public static class MediaInfoParser
    {
        /// <summary>Length in seconds from a "Duration: 00:00:32.47" line, or null if it says N/A or is missing.</summary>
        public static double? Duration(string? ffmpegOutput)
        {
            var m = Regex.Match(ffmpegOutput ?? "", @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)");
            if (!m.Success) return null;

            return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>The first video stream's picture size as "2560x1440", or null.</summary>
        public static string? VideoSize(string? ffmpegOutput)
        {
            // "Video: h264 (Main) (avc1 / 0x31637661), yuv420p(...), 2560x1440 [SAR 1:1 DAR 16:9], ..."
            // The codec tag holds "0x3163..." too, so require a comma before and a space, comma or bracket after.
            var m = Regex.Match(ffmpegOutput ?? "", @"Video:[^\r\n]*?, (\d{2,5})x(\d{2,5})[ ,\[]");
            return m.Success ? $"{m.Groups[1].Value}x{m.Groups[2].Value}" : null;
        }
    }
}
