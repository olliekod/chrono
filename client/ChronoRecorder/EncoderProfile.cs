using System;

namespace ChronoRecorder
{
    /// <summary>
    /// FFmpeg encoder flags per encoder. Presets are encoder-specific: an NVENC preset
    /// like "p4" makes the AMD/Intel/software encoders fail outright.
    /// </summary>
    public static class EncoderProfile
    {
        /// <summary>Time between keyframes in recorded segments; also how far a copy-mode clip start can snap.</summary>
        public const int KeyframeIntervalSeconds = 2;

        private static readonly string[] Known = { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" };

        /// <summary>Known encoder name (canonical case), or libx264 for anything unrecognised.</summary>
        public static string Normalize(string? encoder)
        {
            foreach (var known in Known)
            {
                if (string.Equals(encoder, known, StringComparison.OrdinalIgnoreCase))
                    return known;
            }
            return "libx264";
        }

        public static string BuildArgs(string? encoder, int bitrateKbps, int fps)
        {
            string name = Normalize(encoder);

            string tuning = name switch
            {
                "h264_nvenc" => "-preset p4",
                "h264_amf" => "-quality speed",
                "h264_qsv" => "-preset veryfast",
                _ => "-preset ultrafast"
            };

            // Copy-mode clips can only start on a keyframe, so keyframes come every KeyframeIntervalSeconds
            // by the clock. -g alone counts frames, and a capture source that drops frames stretches that
            // interval. -forced-idr makes NVENC's forced keyframes real IDR frames.
            string idr = name == "h264_nvenc" ? " -forced-idr 1" : "";

            return $"-c:v {name} {tuning} " +
                   $"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k " +
                   $"-g {fps * KeyframeIntervalSeconds} " +
                   $"-force_key_frames \"expr:gte(t,n_forced*{KeyframeIntervalSeconds})\"{idr}";
        }
    }
}
