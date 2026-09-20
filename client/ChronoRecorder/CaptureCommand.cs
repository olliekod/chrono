using System.Collections.Generic;
using System.Drawing;

namespace ChronoRecorder
{
    public enum CaptureMethod
    {
        /// <summary>Desktop Duplication (FFmpeg's ddagrab): frames stay on the GPU. Light, and the default.</summary>
        DesktopDuplication,

        /// <summary>GDI screen grab of a region: CPU-bound and much heavier, kept as a fallback.</summary>
        Gdi
    }

    /// <summary>What to record: one monitor.</summary>
    /// <param name="OutputIndex">Desktop Duplication's number for the monitor (on the default GPU).</param>
    /// <param name="Bounds">The monitor's rectangle on the virtual desktop, in pixels.</param>
    public sealed record CaptureSource(CaptureMethod Method, int OutputIndex, Rectangle Bounds);

    public sealed record CaptureRequest(
        string Encoder,
        int Fps,
        int BitrateKbps,
        CaptureSource Source,
        Size Output,
        int SegmentSeconds,
        string SegmentPattern);

    /// <summary>
    /// Builds the one long-running FFmpeg command that captures a monitor and writes rolling segments.
    /// Pure, so the exact arguments are unit-tested.
    /// </summary>
    public static class CaptureCommand
    {
        public static string Build(CaptureRequest r)
        {
            string encoder = EncoderProfile.Normalize(r.Encoder);
            bool software = encoder == "libx264";
            bool scaled = r.Output != r.Source.Bounds.Size;

            var parts = new List<string> { "-hide_banner -nostats -loglevel warning" };

            if (r.Source.Method == CaptureMethod.DesktopDuplication)
            {
                parts.Add($"-f lavfi -i \"ddagrab=output_idx={r.Source.OutputIndex}:framerate={r.Fps}\"");

                // Frames arrive as GPU textures. A hardware encoder takes them as they are; anything else
                // (scaling, software encoding) has to bring them to system memory first, which is what costs CPU.
                if (scaled)
                    parts.Add($"-vf \"hwdownload,format=bgra,scale={r.Output.Width}:{r.Output.Height}:flags=fast_bilinear,format={(software ? "yuv420p" : "nv12")}\"");
                else if (software)
                    parts.Add("-vf \"hwdownload,format=bgra,format=yuv420p\"");
            }
            else
            {
                var b = r.Source.Bounds;
                parts.Add($"-f gdigrab -framerate {r.Fps} -offset_x {b.X} -offset_y {b.Y} -video_size {b.Width}x{b.Height} -i desktop");

                if (scaled)
                    parts.Add($"-vf \"scale={r.Output.Width}:{r.Output.Height}:flags=fast_bilinear\"");
                parts.Add("-pix_fmt yuv420p");
            }

            parts.Add(EncoderProfile.BuildArgs(encoder, r.BitrateKbps, r.Fps));

            // One process for the whole recording. It starts a new file whenever a keyframe arrives after
            // SegmentSeconds, and forced keyframes every 2s make those splits land exactly on the boundary.
            // flush_packets keeps the file being written readable, so a clip can include the newest seconds.
            parts.Add($"-f segment -segment_time {r.SegmentSeconds} -segment_format mpegts " +
                      "-segment_format_options flush_packets=1 -reset_timestamps 1");

            parts.Add($"-y \"{r.SegmentPattern}\"");

            return string.Join(" ", parts);
        }
    }
}
