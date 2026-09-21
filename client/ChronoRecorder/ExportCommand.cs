using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

namespace ChronoRecorder
{
    public enum ExportMode
    {
        /// <summary>Join the segments without re-encoding. Instant; the clip is the recording's native size.</summary>
        Copy,

        /// <summary>NVIDIA only: decode, resize and encode entirely on the GPU. No CPU scaling.</summary>
        Gpu,

        /// <summary>Resize in software, then encode. Any GPU; a few seconds of brief multi-core work.</summary>
        Cpu
    }

    /// <param name="ListPath">An FFmpeg concat list of the segment files.</param>
    /// <param name="SeekSeconds">How far into the joined footage the clip starts.</param>
    /// <param name="LengthSeconds">How long a re-encoded clip should be. Copy mode ignores it (see ExportPlanner).</param>
    /// <param name="Size">The clip's size. Copy mode ignores it.</param>
    public sealed record ExportRequest(
        string ListPath,
        string OutputPath,
        double SeekSeconds,
        double LengthSeconds,
        ExportMode Mode,
        Size Size,
        string Encoder,
        int BitrateKbps,
        int Fps);

    /// <summary>
    /// Builds the FFmpeg command that turns buffered segments into a finished clip. Pure, so it is unit-tested.
    /// Recording is always at the monitor's native size because that costs almost nothing while you play; shrinking
    /// happens here, after you save, where a few seconds of GPU work doesn't matter.
    /// </summary>
    public static class ExportCommand
    {
        public static string Build(ExportRequest r)
        {
            var parts = new List<string> { "-hide_banner -loglevel error" };

            // -ss before -i seeks within the joined footage, so extra footage at the START is skipped and the newest is kept.
            if (r.SeekSeconds > 0.05)
                parts.Add($"-ss {Number(r.SeekSeconds)}");

            parts.Add("-f concat -safe 0");

            switch (r.Mode)
            {
                case ExportMode.Copy:
                    parts.Add($"-i \"{r.ListPath}\"");
                    parts.Add("-c copy");
                    break;

                case ExportMode.Gpu:
                    // NVDEC decodes and resizes, NVENC encodes. Everything stays in GPU memory. (The decoder options
                    // belong to the input, so they go before -i.)
                    parts.Add($"-c:v h264_cuvid -resize {r.Size.Width}x{r.Size.Height} -i \"{r.ListPath}\"");
                    parts.Add($"-t {Number(r.LengthSeconds)}");
                    parts.Add(EncoderProfile.BuildArgs("h264_nvenc", r.BitrateKbps, r.Fps, EncodeSpeed.Save));
                    parts.Add("-c:a copy");
                    break;

                default:
                    parts.Add($"-i \"{r.ListPath}\"");
                    parts.Add($"-t {Number(r.LengthSeconds)}");
                    parts.Add($"-vf \"scale={r.Size.Width}:{r.Size.Height}:flags=bicubic\"");
                    parts.Add(EncoderProfile.BuildArgs(r.Encoder, r.BitrateKbps, r.Fps, EncodeSpeed.Save));
                    parts.Add("-c:a copy");
                    break;
            }

            parts.Add("-movflags +faststart");
            parts.Add($"-y \"{r.OutputPath}\"");

            return string.Join(" ", parts);
        }

        private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
    }

    /// <summary>Decisions about how to export a clip.</summary>
    public static class ExportPlanner
    {
        /// <summary>The size to shrink a clip to, or null to keep the recording's native size.</summary>
        public static Size? ExportSize(string? setting, Size native)
        {
            if (native.IsEmpty) return null;

            var size = CaptureSizing.Resolve(setting, native);
            return size == native ? null : size;
        }

        /// <summary>
        /// How much footage to plan for. Copy mode can only start on a keyframe and drops everything before the first
        /// one at or after the seek point, so it needs one keyframe interval of slack. Re-encoding cuts exactly.
        /// </summary>
        public static double PlannedSeconds(int clipLengthSeconds, bool transcode)
            => transcode ? clipLengthSeconds : clipLengthSeconds + EncoderProfile.KeyframeIntervalSeconds;

        /// <summary>The GPU path uses NVIDIA's decoder and encoder, so it needs an NVENC recording.</summary>
        public static bool CanUseGpu(string? encoder) => EncoderProfile.Normalize(encoder) == "h264_nvenc"
                                                          && !string.IsNullOrEmpty(encoder);
    }
}
