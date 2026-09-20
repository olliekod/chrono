using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ChronoRecorder
{
    /// <summary>Reads facts about media files with ffprobe.</summary>
    public static class MediaProbe
    {
        /// <summary>The picture size of a media file as "1920x1080", or null if it can't be read.</summary>
        public static string? VideoSizeText(string path)
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });

                if (probe == null) return null;

                var output = probe.StandardOutput.ReadToEndAsync();
                probe.StandardError.ReadToEndAsync();
                if (!probe.WaitForExit(5000)) { try { probe.Kill(); } catch { } return null; }

                string text = output.Result.Trim();
                return System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{2,5}x\d{2,5}$") ? text : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Could not read the size of {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Real duration of a media file, or null if ffprobe isn't available or can't read it.
        /// </summary>
        public static double? DurationSeconds(string path)
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });

                if (probe == null) return null;

                var output = probe.StandardOutput.ReadToEndAsync();
                probe.StandardError.ReadToEndAsync();

                if (!probe.WaitForExit(5000))
                {
                    try { probe.Kill(); } catch { }
                    return null;
                }

                return double.TryParse(output.Result.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    ? seconds
                    : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Could not probe {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }
    }
}
