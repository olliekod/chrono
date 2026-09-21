using System;
using System.Diagnostics;

namespace ChronoRecorder
{
    /// <summary>Runs FFmpeg to completion for the short jobs done on saved clips (pictures, trims).</summary>
    public static class FfmpegRunner
    {
        /// <summary>Returns FFmpeg's error output on failure (empty on success). Never throws for a failed job; throws only if FFmpeg can't start.</summary>
        public static (bool Ok, string Error) Run(string arguments, int timeoutMs = 180_000)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FfmpegLocator.Path,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            // Drain both pipes while waiting so a chatty FFmpeg can't fill one and hang.
            _ = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "FFmpeg took too long.");
            }

            string message = stderr.Result.Trim();
            return process.ExitCode == 0 ? (true, "") : (false, message.Length > 400 ? message.Substring(message.Length - 400) : message);
        }
    }
}
