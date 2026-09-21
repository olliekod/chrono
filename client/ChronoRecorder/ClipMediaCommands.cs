using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace ChronoRecorder
{
    /// <summary>
    /// FFmpeg command lines for what the library does with a saved clip: a poster picture for its card, a strip of
    /// frames for the trim timeline, and the trim itself. Pure functions, so they are unit-tested.
    /// </summary>
    public static class ClipMediaCommands
    {
        private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private const string Quiet = "-hide_banner -nostats -loglevel error";

        // ------------------------------------------------------------------ poster

        /// <summary>The moment used for a clip's card picture: a little in, so it isn't a black first frame.</summary>
        public static double PosterTime(double? durationSeconds)
        {
            if (durationSeconds is not double d || d <= 0) return 0;
            return Math.Min(1.5, d / 2);
        }

        /// <summary>One frame, scaled to <paramref name="width"/>, as a JPEG.</summary>
        public static string Poster(string input, string output, double atSeconds, int width)
            => $"{Quiet} -ss {Num(atSeconds)} -i \"{input}\" -frames:v 1 -vf scale={width}:-2 -q:v 4 -y \"{output}\"";

        // ---------------------------------------------------------------- filmstrip

        /// <summary>The most tiles a strip gets, so a very long clip doesn't make a huge picture.</summary>
        public const int MaxFilmstripFrames = 90;

        /// <summary>
        /// How many tiles the strip has: one per keyframe. Chrono's clips have a keyframe every
        /// <see cref="EncoderProfile.KeyframeIntervalSeconds"/> seconds, and decoding only those is what makes the strip
        /// fast (0.3 s for a 30 s 1440p clip, against 3 s for seeking to evenly spaced moments).
        /// </summary>
        public static int FilmstripFrames(double durationSeconds)
            => Math.Clamp((int)Math.Round(durationSeconds / EncoderProfile.KeyframeIntervalSeconds), 1, MaxFilmstripFrames);

        /// <summary>The size of one tile of the strip: <paramref name="height"/> tall, in the clip's own shape, even width.</summary>
        public static Size TileSize(Size video, int height)
        {
            if (video.Width <= 0 || video.Height <= 0) return new Size(height * 16 / 9 / 2 * 2, height);
            int width = (int)Math.Round(height * (double)video.Width / video.Height);
            return new Size(width - width % 2, height);
        }

        /// <summary>
        /// One picture of a clip's keyframes side by side, in one pass that decodes only keyframes. A clip whose
        /// keyframes are spaced differently just gets blank space or a cut-off end; the strip is only a guide for
        /// where things happen. Written as yuvj420p because the JPEG encoder refuses limited-range video.
        /// </summary>
        public static string Filmstrip(string input, string output, double durationSeconds, Size tile)
        {
            int frames = FilmstripFrames(durationSeconds);
            return $"{Quiet} -skip_frame nokey -i \"{input}\" -vf \"scale={tile.Width}:{tile.Height},tile={frames}x1,format=yuvj420p\" " +
                   $"-frames:v 1 -q:v 5 -y \"{output}\"";
        }

        // --------------------------------------------------------------------- trim

        public sealed record TrimRequest(
            string Input, string Output, double StartSeconds, double EndSeconds,
            string Encoder, int BitrateKbps, int Fps, bool GpuDecode);

        /// <summary>The shortest clip a trim may leave.</summary>
        public const double MinimumTrimSeconds = 0.5;

        /// <summary>
        /// Cut a clip to [start, end]. The picture is re-encoded, because copying can only cut on keyframes and the
        /// funny part rarely starts on one. Seeking before the input makes the cut frame-accurate; the sound is copied.
        /// </summary>
        public static string Trim(TrimRequest r)
        {
            if (r.StartSeconds < 0) throw new ArgumentException("The start can't be before the beginning.", nameof(r));
            if (r.EndSeconds - r.StartSeconds < MinimumTrimSeconds)
                throw new ArgumentException($"A trimmed clip must be at least {MinimumTrimSeconds} seconds long.", nameof(r));

            string decode = r.GpuDecode ? "-c:v h264_cuvid " : "";
            double length = r.EndSeconds - r.StartSeconds;

            return $"-hide_banner -nostats -loglevel warning -ss {Num(r.StartSeconds)} {decode}-i \"{r.Input}\" -t {Num(length)} " +
                   $"{EncoderProfile.BuildArgs(r.Encoder, r.BitrateKbps, r.Fps, EncodeSpeed.Save)} -c:a copy -movflags +faststart -y \"{r.Output}\"";
        }
    }
}
