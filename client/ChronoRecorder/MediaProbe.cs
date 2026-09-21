using System;
using System.Diagnostics;
using System.IO;

namespace ChronoRecorder
{
    /// <summary>Reads facts about media files by asking FFmpeg to open them (see <see cref="MediaInfoParser"/>).</summary>
    public static class MediaProbe
    {
        /// <summary>The picture size of a media file as "1920x1080", or null if it can't be read.</summary>
        public static string? VideoSizeText(string path)
        {
            string? output = Inspect(path);
            return output == null ? null : MediaInfoParser.VideoSize(output);
        }

        /// <summary>Real duration of a media file, or null if FFmpeg isn't available or can't read it.</summary>
        public static double? DurationSeconds(string path)
        {
            string? output = Inspect(path);
            return output == null ? null : MediaInfoParser.Duration(output);
        }

        /// <summary>
        /// Length and picture size together. One look at the file: FFmpeg prints both, so asking twice would start
        /// a second process to read the same lines. The library fills these in for every clip it doesn't know yet.
        /// </summary>
        public static (double? Duration, string? Size) Details(string path)
        {
            string? output = Inspect(path);
            return output == null ? (null, null) : (MediaInfoParser.Duration(output), MediaInfoParser.VideoSize(output));
        }

        /// <summary>FFmpeg's description of the file. It exits with an error because no output is given; that is expected.</summary>
        private static string? Inspect(string path)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = FfmpegLocator.Path,
                    Arguments = $"-hide_banner -i \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });

                if (process == null) return null;

                _ = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch { }
                    return null;
                }

                return stderr.Result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Could not read {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }
    }
}
