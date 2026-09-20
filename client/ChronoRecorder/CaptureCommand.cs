using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

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

    /// <param name="Audio">Sound sources, each read from a named pipe. Empty or null for a silent recording.</param>
    /// <param name="ClockStartUnixSeconds">The start time (T0) that picture and sound are both measured from.
    /// Required whenever there is audio.</param>
    public sealed record CaptureRequest(
        string Encoder,
        int Fps,
        int BitrateKbps,
        CaptureSource Source,
        Size Output,
        int SegmentSeconds,
        string SegmentPattern,
        IReadOnlyList<AudioInput>? Audio = null,
        double? ClockStartUnixSeconds = null);

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
            var audio = r.Audio ?? new List<AudioInput>();

            if (audio.Count > 0 && r.ClockStartUnixSeconds is null)
                throw new ArgumentException("Audio needs the shared start time so it can be lined up with the picture.", nameof(r));

            var parts = new List<string> { "-hide_banner -nostats -loglevel warning" };
            var videoFilters = new List<string>();

            // ---- inputs. Every input goes first: ffmpeg treats an option placed before an -i as an input option
            // and refuses output-only ones (like -vf) there. Input 0 is the video; the audio pipes follow.
            if (r.Source.Method == CaptureMethod.DesktopDuplication)
            {
                parts.Add($"-f lavfi -i \"ddagrab=output_idx={r.Source.OutputIndex}:framerate={r.Fps}\"");
            }
            else
            {
                var b = r.Source.Bounds;
                parts.Add($"-f gdigrab -framerate {r.Fps} -offset_x {b.X} -offset_y {b.Y} -video_size {b.Width}x{b.Height} -i desktop");
            }

            foreach (var input in audio)
                parts.Add($"-f {input.FfmpegFormat} -ar {input.SampleRate} -ac {input.Channels} -i {input.PipePath}");

            // ---- stream selection
            if (audio.Count == 1)
            {
                parts.Add("-map 0:v:0 -map 1:a:0");
            }
            else if (audio.Count > 1)
            {
                string inputs = string.Concat(audio.Select((_, i) => $"[{i + 1}:a]"));
                parts.Add($"-filter_complex \"{inputs}amix=inputs={audio.Count}:duration=longest:normalize=0[aout]\" -map 0:v:0 -map \"[aout]\"");
            }

            // ---- video filters
            if (audio.Count > 0)
            {
                // FFmpeg zeroes each input's clock at that input's own first data, and video and audio start
                // seconds apart by an amount nobody can observe. So don't rely on either: stamp every frame with
                // (now - T0), and the audio feeder places sound at (capture time - T0). Both count from the same
                // instant. This only relabels frames, so it costs nothing and they stay on the GPU.
                string t0 = r.ClockStartUnixSeconds!.Value.ToString("F3", CultureInfo.InvariantCulture);
                videoFilters.Add($"setpts=(time(0)-{t0})/TB");
            }

            if (r.Source.Method == CaptureMethod.DesktopDuplication)
            {
                // Frames arrive as GPU textures. A hardware encoder takes them as they are; anything else
                // (scaling, software encoding) has to bring them to system memory first, which is what costs CPU.
                if (scaled)
                    videoFilters.Add($"hwdownload,format=bgra,scale={r.Output.Width}:{r.Output.Height}:flags=fast_bilinear,format={(software ? "yuv420p" : "nv12")}");
                else if (software)
                    videoFilters.Add("hwdownload,format=bgra,format=yuv420p");
            }
            else if (scaled)
            {
                videoFilters.Add($"scale={r.Output.Width}:{r.Output.Height}:flags=fast_bilinear");
            }

            if (videoFilters.Count > 0)
                parts.Add($"-vf \"{string.Join(",", videoFilters)}\"");

            // Frames stamped by the clock arrive at slightly irregular times; a constant rate evens that out.
            if (audio.Count > 0)
                parts.Add($"-fps_mode cfr -r {r.Fps}");

            if (r.Source.Method == CaptureMethod.Gdi)
                parts.Add("-pix_fmt yuv420p");

            parts.Add(EncoderProfile.BuildArgs(encoder, r.BitrateKbps, r.Fps));

            // Clips are always stereo AAC at 48 kHz, whatever the device gave us.
            if (audio.Count > 0)
                parts.Add("-c:a aac -b:a 160k -ar 48000 -ac 2");

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
