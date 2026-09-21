using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ChronoRecorder
{
    /// <summary>What FFmpeg reported about the recording in its latest progress block.</summary>
    /// <param name="DuplicatedFrames">Frames repeated to fill the constant frame rate. Normal while a game isn't drawing
    /// (minimized, paused, a still screen); many of them while the game is busy means the recording is falling behind.</param>
    /// <param name="DroppedFrames">Frames FFmpeg had to throw away because they arrived faster than the output rate.</param>
    /// <param name="Speed">How fast the recording is being encoded compared with real time. Below 1 the encoder is behind.</param>
    public sealed record EncodeStats(
        long Frames, double Fps, long DuplicatedFrames, long DroppedFrames, double Speed, double BitrateKbps, double OutSeconds, DateTime AtUtc);

    /// <summary>
    /// Reads the lines FFmpeg prints for <c>-progress pipe:1</c>: blocks of <c>key=value</c> lines that each end with
    /// <c>progress=continue</c> (or <c>end</c>). Feed it one line at a time; it hands back the numbers when a block is complete.
    /// </summary>
    public sealed class FfmpegProgressParser
    {
        private readonly Dictionary<string, string> block = new(StringComparer.Ordinal);

        public EncodeStats? Feed(string? line, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            int eq = line.IndexOf('=');
            if (eq <= 0) return null;

            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();

            if (key != "progress")
            {
                block[key] = value;
                return null;
            }

            var stats = Build(nowUtc);
            block.Clear();
            return stats;
        }

        private EncodeStats? Build(DateTime nowUtc)
        {
            // A block with no frame count is the first one, before anything has been encoded.
            if (!block.TryGetValue("frame", out var frame) || !long.TryParse(frame, NumberStyles.Integer, CultureInfo.InvariantCulture, out long frames))
                return null;

            return new EncodeStats(
                frames,
                Number(Get("fps")),
                (long)Number(Get("dup_frames")),
                (long)Number(Get("drop_frames")),
                Number(Get("speed")),
                Number(Get("bitrate")),
                Number(Get("out_time_us")) / 1_000_000.0,
                nowUtc);
        }

        private string? Get(string key) => block.TryGetValue(key, out var v) ? v : null;

        // "0.994x", "597.8kbits/s", "59.83", " 12" all begin with the number; "N/A" (nothing measured yet) counts as 0.
        private static readonly Regex Leading = new(@"^-?\d+(\.\d+)?", RegexOptions.Compiled);

        private static double Number(string? text)
        {
            if (text == null) return 0;
            var m = Leading.Match(text.Trim());
            return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        }
    }
}
