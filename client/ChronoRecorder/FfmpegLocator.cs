using System;
using System.IO;

namespace ChronoRecorder
{
    /// <summary>
    /// Finds FFmpeg. A copy shipped inside the app's folder wins, so Chrono works on a PC that has never heard of
    /// FFmpeg and always runs the build it was tested with; otherwise whatever is on the PATH is used (development).
    /// </summary>
    public static class FfmpegLocator
    {
        /// <summary>What to pass as the FileName when starting FFmpeg.</summary>
        public static string Path { get; } = Locate(AppContext.BaseDirectory, File.Exists);

        /// <summary>True when the copy shipped with the app is the one in use.</summary>
        public static bool IsBundled => Path != "ffmpeg";

        public static string Locate(string baseDirectory, Func<string, bool> fileExists)
        {
            string[] candidates =
            {
                System.IO.Path.Combine(baseDirectory, "ffmpeg", "ffmpeg.exe"),
                System.IO.Path.Combine(baseDirectory, "ffmpeg.exe")
            };

            foreach (var candidate in candidates)
                if (fileExists(candidate)) return candidate;

            return "ffmpeg";
        }
    }
}
