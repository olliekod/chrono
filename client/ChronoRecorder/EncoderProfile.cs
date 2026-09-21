using System;

namespace ChronoRecorder
{
    /// <summary>
    /// FFmpeg encoder flags per encoder. Presets are encoder-specific: an NVENC preset
    /// like "p4" makes the AMD/Intel/software encoders fail outright.
    /// </summary>
    /// <summary>What the encoder is being asked to do, which is what its speed setting should follow.</summary>
    public enum EncodeSpeed
    {
        /// <summary>Recording a game. Speed is everything: the encoder must never hold the game up.</summary>
        Live,

        /// <summary>Saving or trimming a clip that is already recorded. A few more seconds here buys a better picture.</summary>
        Save
    }

    /// <summary>How hard recording asks the graphics card to work. See <see cref="LoadPlan"/>.</summary>
    public enum EncodeLoad
    {
        Normal,

        /// <summary>
        /// A faster NVENC preset: p3 rather than p4. On an RTX 4080 recording 2560x1440 it takes the video encoder from
        /// about 10% to 7% for the same file size and a VMAF score within a point. Presets p1 and p2 halve it but let the
        /// bitrate overshoot by about 28%, which makes bigger clips for little gain, so they are not used.
        /// </summary>
        Light
    }

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

        public static string BuildArgs(string? encoder, int bitrateKbps, int fps, EncodeSpeed speed = EncodeSpeed.Live, EncodeLoad load = EncodeLoad.Normal)
        {
            string name = Normalize(encoder);

            // NVENC: variable bitrate with the high-quality tuning and adaptive quantisation, which spends bits on
            // flat areas (sky, walls) where blocking is most visible, and the High profile. All of it runs on the
            // encoder chip, not the game's GPU cores. The rate cap sits above the target so busy moments get room.
            //
            // The processor encoder is the one that changes with the job. While recording it has to be ultrafast, or
            // a game stutters; ultrafast also turns off most of what x264 does to save bits, so the same bitrate
            // gives a visibly worse picture. Saving a clip is not racing anything, so it gets a slower setting and a
            // cleaner result from the same bitrate.
            string tuning = name switch
            {
                "h264_nvenc" => $"-preset {(load == EncodeLoad.Light ? "p3" : "p4")} -tune hq -rc vbr -spatial-aq 1 -profile:v high",
                "h264_amf" => speed == EncodeSpeed.Save ? "-quality balanced" : "-quality speed",
                "h264_qsv" => speed == EncodeSpeed.Save ? "-preset faster" : "-preset veryfast",
                _ => speed == EncodeSpeed.Save ? "-preset veryfast" : "-preset ultrafast"
            };

            // Copy-mode clips can only start on a keyframe, so keyframes come every KeyframeIntervalSeconds
            // by the clock. -g alone counts frames, and a capture source that drops frames stretches that
            // interval. -forced-idr makes NVENC's forced keyframes real IDR frames.
            string idr = name == "h264_nvenc" ? " -forced-idr 1" : "";

            return $"-c:v {name} {tuning} " +
                   $"-b:v {bitrateKbps}k -maxrate {bitrateKbps * 3 / 2}k -bufsize {bitrateKbps * 2}k " +
                   $"-g {fps * KeyframeIntervalSeconds} " +
                   $"-force_key_frames \"expr:gte(t,n_forced*{KeyframeIntervalSeconds})\"{idr}";
        }
    }
}
