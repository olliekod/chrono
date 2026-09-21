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
        Gdi,

        /// <summary>
        /// Windows Graphics Capture of one window (FFmpeg's gfxcapture): only that window's own picture, so another
        /// window on top of it, or the desktop showing when it is minimized, can never end up in the clip. Frames stay
        /// on the GPU like Desktop Duplication (measured 2.9% of a core against 2.4%). Used for games.
        /// </summary>
        WindowCapture
    }

    /// <summary>What to record: one monitor, or (WindowCapture) one window shown at a monitor's size.</summary>
    /// <param name="OutputIndex">Desktop Duplication's number for the monitor (on the default GPU).</param>
    /// <param name="Bounds">The monitor's rectangle on the virtual desktop, in pixels. For a window capture this is the
    /// size the video is made at; the window is scaled to fit it (keeping its shape).</param>
    /// <param name="WindowHandle">The HWND to capture. Only used by WindowCapture.</param>
    public sealed record CaptureSource(CaptureMethod Method, int OutputIndex, Rectangle Bounds, long WindowHandle = 0);

    /// <param name="Audio">Sound sources, each read from a named pipe. Empty or null for a silent recording.</param>
    /// <param name="ClockStartUnixSeconds">The start time (T0) that picture and sound are both measured from.
    /// Required whenever there is audio.</param>
    public sealed record CaptureRequest(
        string Encoder,
        int Fps,
        int BitrateKbps,
        CaptureSource Source,
        int SegmentSeconds,
        string SegmentPattern,
        IReadOnlyList<AudioInput>? Audio = null,
        double? ClockStartUnixSeconds = null);

    /// <summary>
    /// Builds the one long-running FFmpeg command that captures a monitor and writes rolling segments.
    /// Always at the monitor's native size: that is nearly free (frames never leave the GPU), whereas resizing
    /// live costs about a CPU core. Shrinking a clip is done when it is saved (see <see cref="ExportCommand"/>).
    /// Pure, so the exact arguments are unit-tested.
    /// </summary>
    public static class CaptureCommand
    {
        public static string Build(CaptureRequest r)
        {
            string encoder = EncoderProfile.Normalize(r.Encoder);
            bool software = encoder == "libx264";
            var audio = r.Audio ?? new List<AudioInput>();

            if (audio.Count > 0 && r.ClockStartUnixSeconds is null)
                throw new ArgumentException("Audio needs the shared start time so it can be lined up with the picture.", nameof(r));

            var parts = new List<string> { "-hide_banner -nostats -loglevel warning" };
            var videoFilters = new List<string>();

            // ---- inputs. Every input goes first: ffmpeg treats an option placed before an -i as an input option
            // and refuses output-only ones (like -vf) there. Input 0 is the video; the audio pipes follow.
            if (r.Source.Method == CaptureMethod.WindowCapture)
            {
                if (r.Source.WindowHandle == 0) throw new ArgumentException("A window capture needs a window.", nameof(r));

                // The size is forced so a window that starts small (a launcher, a loading screen) or changes size later
                // doesn't change the video's size; it is scaled to fit, keeping its shape. A minimized game stops sending
                // frames, so the clock-stamped constant frame rate below repeats its last frame: a frozen picture of the
                // game, never the desktop.
                var b = r.Source.Bounds;
                int w = b.Width - b.Width % 2, h = b.Height - b.Height % 2;
                parts.Add($"-f lavfi -i \"gfxcapture=hwnd={r.Source.WindowHandle}:max_framerate={r.Fps}:width={w}:height={h}:resize_mode=scale_aspect:capture_cursor=true\"");
            }
            else if (r.Source.Method == CaptureMethod.DesktopDuplication)
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
            // Louder sources are limited afterwards: summing a boosted microphone with game sound can go past full scale,
            // and clipping is harsh where a limiter is not (measured: +6.4 dB peak without it, -0.3 dB with it).
            const string Limiter = "alimiter=limit=0.97:level=0";
            string Gain(AudioInput a) => a.Gain.ToString("0.###", CultureInfo.InvariantCulture);

            if (audio.Count == 1)
            {
                if (audio[0].Gain == 1.0)
                    parts.Add("-map 0:v:0 -map 1:a:0");
                else
                    parts.Add($"-filter_complex \"[1:a]volume={Gain(audio[0])},{Limiter}[aout]\" -map 0:v:0 -map \"[aout]\"");
            }
            else if (audio.Count > 1)
            {
                var chains = audio.Select((a, i) => a.Gain == 1.0 ? "" : $"[{i + 1}:a]volume={Gain(a)}[g{i + 1}];").ToList();
                string inputs = string.Concat(audio.Select((a, i) => a.Gain == 1.0 ? $"[{i + 1}:a]" : $"[g{i + 1}]"));
                parts.Add($"-filter_complex \"{string.Concat(chains)}{inputs}amix=inputs={audio.Count}:duration=longest:normalize=0,{Limiter}[aout]\" -map 0:v:0 -map \"[aout]\"");
            }

            // ---- video filters
            if (r.ClockStartUnixSeconds is double clockStart)
            {
                // Stamp every frame with the time it was captured, counted from T0. This is what makes a second of
                // video a second of real time, which matters whether or not there is sound to line it up with:
                //  - a window only sends a frame when it draws, and a game that is minimized, paused or showing a
                //    still screen draws rarely or not at all. Without this, those stretches are dropped from the
                //    recording with no sign of it, so "the last 30 seconds" covers far more than the last 30
                //    seconds, and a minimized game jumps over the gap instead of holding its last frame. Measured
                //    over 18 s with 6 s of it minimized: 16.3 s recorded with the clock, 11.93 s without,
                //  - and when there is sound, the feeder places it at (capture time - T0) from the same instant, so
                //    the two line up without either having to guess what the other did at startup.
                // It only relabels frames, so it costs nothing and they stay on the GPU.
                string t0 = clockStart.ToString("F3", CultureInfo.InvariantCulture);
                videoFilters.Add($"setpts=(time(0)-{t0})/TB");
            }

            // Desktop Duplication frames are GPU textures. A hardware encoder takes them as they are; software
            // encoding has to bring them to system memory first, which costs CPU.
            if (r.Source.Method != CaptureMethod.Gdi && software)
                videoFilters.Add("hwdownload,format=bgra,format=yuv420p");

            if (videoFilters.Count > 0)
                parts.Add($"-vf \"{string.Join(",", videoFilters)}\"");

            // Frames stamped by the clock arrive at slightly irregular times; a constant rate evens that out, and
            // repeats the last frame through a stretch where the window sent none (a minimized game).
            if (r.ClockStartUnixSeconds is not null)
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
