using System;

namespace ChronoRecorder
{
    /// <summary>What to do about a window capture that has stopped getting new pictures.</summary>
    public enum StarvationResponse
    {
        /// <summary>Start the capture again, which also looks up the game's window again.</summary>
        RestartCapture,

        /// <summary>Restarting didn't last: record the monitor while the game is in front instead.</summary>
        UseScreenCapture
    }

    /// <summary>
    /// Notices a window capture that has gone quiet. Windows only sends a frame when the captured window draws, and the
    /// recording runs at a constant frame rate, so when the window stops delivering FFmpeg fills every gap with a copy of
    /// the last frame. That is cheap to encode, so "speed" stays at 1.00x and nothing looks wrong, but the clip is a
    /// slideshow. Seen on a Destiny 2 run: over 96 minutes 97% of the frames were repeats (about 1.5 new pictures a second)
    /// while the graphics card sat idle, and it never recovered by itself.
    ///
    /// This only counts while the game is the window in front, because a game that is minimised, loading or paused really
    /// does hold one picture. It needs a long unbroken run (<see cref="Reports"/> reports, 30 seconds) at
    /// <see cref="RepeatRatio"/> or more, which is far past the 50% that <see cref="LoadGovernor"/> treats as a slow card,
    /// so a loading screen or a quiet menu doesn't trip it. Like the governor it compares each report with the one before
    /// (FFmpeg reports running totals), so call <see cref="Observe"/> once per report.
    /// </summary>
    public sealed class CaptureStarvation
    {
        /// <summary>This share (or more) of the frames written since the last report being repeats counts as starved.</summary>
        public const double RepeatRatio = 0.9;

        /// <summary>Consecutive progress reports (two seconds apart) that must all be starved.</summary>
        public const int Reports = 15;

        /// <summary>A second starvation this soon after a restart means restarting doesn't help.</summary>
        public static readonly TimeSpan EscalateWithin = TimeSpan.FromMinutes(10);

        private int starved;
        private EncodeStats? previous;

        /// <summary>Looks at one report. True once the capture has been starved long enough to act on; counting then starts over.</summary>
        public bool Observe(EncodeStats stats, DateTime startedUtc, bool gameInFront)
        {
            var before = previous;
            previous = stats;

            bool ready = stats.AtUtc - startedUtc >= LoadGovernor.Warmup && stats.Speed > 0;
            bool bad = false;
            if (ready && gameInFront && before != null)
            {
                long newFrames = stats.Frames - before.Frames;
                long newRepeats = stats.DuplicatedFrames - before.DuplicatedFrames;
                bad = newFrames > 0 && (double)newRepeats / newFrames >= RepeatRatio;
            }

            if (!bad) { starved = 0; return false; }
            if (++starved < Reports) return false;
            starved = 0;
            return true;
        }

        /// <summary>The first starvation restarts the capture; one soon after the last means the window can't be captured.</summary>
        public static StarvationResponse Respond(DateTime? lastStarvationUtc, DateTime nowUtc)
            => lastStarvationUtc != null && nowUtc - lastStarvationUtc.Value < EscalateWithin
                ? StarvationResponse.UseScreenCapture
                : StarvationResponse.RestartCapture;
    }
}
