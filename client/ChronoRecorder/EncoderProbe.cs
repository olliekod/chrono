using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace ChronoRecorder
{
    /// <summary>
    /// Finding out whether a hardware encoder really works on this PC, and saying why in words a person can act on.
    /// FFmpeg lists an encoder as available whenever it was built in, whether or not the graphics driver can run it, so
    /// the only way to know is to start one. An out-of-date NVIDIA driver, another program using up the card's
    /// encoder sessions, and a laptop whose display sits on a different graphics chip all end here.
    /// </summary>
    public static class EncoderProbe
    {
        public sealed record Result(bool Ok, string Message);

        /// <summary>Encodes a few blank frames with the settings recording uses. Takes well under a second on a working card.</summary>
        public static Result Test(string encoder)
        {
            string name = EncoderProfile.Normalize(encoder);
            if (name == "libx264") return new Result(true, "");

            // The same options recording uses, so an option this card refuses shows up here and not mid-game.
            string arguments = "-hide_banner -loglevel error -f lavfi -i color=c=black:s=1280x720:r=30:d=1 -frames:v 15 " +
                               $"{EncoderProfile.BuildArgs(name, 6000, 30)} -f null -";
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = FfmpegLocator.Path,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });
                if (process == null) return new Result(false, "FFmpeg didn't start.");

                _ = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(20_000))
                {
                    try { process.Kill(); } catch { }
                    return new Result(false, "The encoder took too long to start.");
                }

                string text = stderr.Result;
                return process.ExitCode == 0 ? new Result(true, "") : new Result(false, Explain(text));
            }
            catch (Exception ex)
            {
                return new Result(false, $"Couldn't test the encoder ({ex.Message}).");
            }
        }

        /// <summary>True when FFmpeg's error output says the video encoder itself wouldn't start (rather than, say, a capture problem).</summary>
        public static bool LooksLikeEncoderFailure(string? ffmpegOutput)
        {
            if (string.IsNullOrWhiteSpace(ffmpegOutput)) return false;
            // FFmpeg tags a message with the encoder's name, but AMD's runtime reports itself as just "AMF".
            if (Regex.IsMatch(ffmpegOutput, @"\[(h264_nvenc|h264_amf|h264_qsv|AMF)\b", RegexOptions.IgnoreCase)) return true;

            string[] phrases =
            {
                "OpenEncodeSessionEx", "nvcuda", "amfrt64", "No capable devices found", "Driver does not support the required nvenc",
                "minimum required Nvidia driver", "Error while opening encoder", "Could not open encoder"
            };
            return phrases.Any(p => ffmpegOutput.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>What went wrong with an encoder, in plain words, with what to try.</summary>
        public static string Explain(string? ffmpegOutput)
        {
            string text = ffmpegOutput ?? "";

            bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);

            if (Has("Driver does not support the required nvenc") || Has("minimum required Nvidia driver"))
                return "The NVIDIA graphics driver is too old for Chrono's video encoder. Updating it from nvidia.com fixes this.";

            if (Has("nvcuda"))
                return "Chrono couldn't find NVIDIA's video encoder. Installing or repairing the NVIDIA graphics driver fixes this.";

            if (Has("OpenEncodeSessionEx") && (Has("out of memory") || Has("(10)") || Has("(2)")))
                return "The graphics card is out of memory or already running its limit of video encoders. Closing other recording programs (OBS, ShadowPlay) may fix this.";

            if (Has("amfrt64"))
                return "AMD's video encoder isn't installed on this PC. Installing or repairing the AMD graphics driver fixes this.";

            if (Has("No capable devices found"))
                return "No graphics card in this PC can use NVIDIA's video encoder.";

            string first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "the encoder didn't start";
            return first.Length > 160 ? first.Substring(0, 160) : first;
        }
    }
}
