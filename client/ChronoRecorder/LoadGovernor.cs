using System;

namespace ChronoRecorder
{
    /// <summary>
    /// How hard recording works, in three steps. Measured on an RTX 4080 recording 2560x1440, the frame rate is what
    /// moves the load: 30 FPS halves both the 3D engine the game shares and the video encoder, while the encoder preset
    /// only moves the video encoder (10% at p4, 7% at p3, 5% at p1) and recording at a smaller size costs the 3D
    /// engine as much as it saves. So the steps are the preset, then the frame rate.
    /// </summary>
    public static class LoadPlan
    {
        /// <summary>0 normal; 1 the lighter encoder preset; 2 the lighter preset and at most 30 FPS.</summary>
        public const int MaxLevel = 2;

        public static EncodeLoad LoadFor(int level) => level >= 1 ? EncodeLoad.Light : EncodeLoad.Normal;

        public static int FpsFor(int level, int configuredFps) => level >= 2 ? Math.Min(configuredFps, 30) : configuredFps;

        /// <summary>
        /// Whether the lighter preset exists for this encoder. Only NVENC has one; AMD's, Intel's and the processor's are
        /// already their fastest, so for them the first step would change nothing and the governor skips it.
        /// </summary>
        public static bool HasPresetStep(string? encoder) => EncoderProfile.Normalize(encoder) == "h264_nvenc";

        /// <summary>
        /// The highest step the governor may take. The frame rate is the user's to change, so it is only lowered when they
        /// chose "autofps"; at 30 FPS or less there is nothing left to lower anyway.
        /// </summary>
        public static int HighestUsefulLevel(int configuredFps, bool presetStep = true, bool allowFrameRate = false)
            => allowFrameRate && configuredFps > 30 ? MaxLevel : presetStep ? 1 : 0;

        /// <summary>"auto" and "autofps" both let the governor move the recording down; only "autofps" may lower the frame rate.</summary>
        public static bool IsAutomatic(string? setting)
            => string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase) || string.Equals(setting, "autofps", StringComparison.OrdinalIgnoreCase);

        public static bool MayLowerFrameRate(string? setting) => string.Equals(setting, "autofps", StringComparison.OrdinalIgnoreCase);

        /// <summary>The step after this one: the preset, then the frame rate; straight to the frame rate when there is no preset to lower.</summary>
        public static int NextLevel(int level, bool presetStep)
            => Math.Min(!presetStep && level == 0 ? 2 : level + 1, MaxLevel);

        /// <summary>The step in words. Where there is no lighter preset (AMD, Intel, the processor) only the frame rate is mentioned.</summary>
        public static string Describe(int level, int configuredFps, bool presetStep = true)
        {
            bool fpsLowered = level >= 2 && configuredFps > 30;
            if (!presetStep) return fpsLowered ? "Reduced to 30 FPS" : "Normal";
            if (level <= 0) return "Normal";
            return fpsLowered ? "Light encoder, 30 FPS" : "Light encoder";
        }

        /// <summary>
        /// The step to begin a recording at. "normal" and "light" are fixed on purpose; "auto" and "autofps" start as low as
        /// the graphics card deserves (or as low as an earlier run found it needed) and are moved by <see cref="LoadGovernor"/>.
        /// </summary>
        public static int StartLevel(string? setting, GpuDetector.GpuTier tier, int learned, int configuredFps, bool presetStep = true)
        {
            int level = (setting ?? "auto").ToLowerInvariant() switch
            {
                "normal" => 0,
                "light" => presetStep ? 1 : 0,
                // A modest card starts on the lighter preset. Without a lighter preset there is nothing to start on, and the
                // next step costs half the frame rate, which is only worth taking once this PC has shown it needs to.
                _ => Math.Max(learned, tier == GpuDetector.GpuTier.Modest && presetStep ? 1 : 0)
            };
            return Math.Clamp(level, 0, HighestUsefulLevel(configuredFps, presetStep, MayLowerFrameRate(setting)));
        }
    }

    /// <summary>One progress report's verdict: step the recording down a level, or (nothing left to step down to) just say so.</summary>
    public readonly record struct GovernorVerdict(bool StepDown, bool BehindAtLimit, string Reason)
    {
        public static readonly GovernorVerdict None = new(false, false, "");
    }

    /// <summary>
    /// Decides when a recording can't keep up and should move down a step. Two different signals catch two different
    /// bottlenecks, because one of them hides from the other:
    ///  - FFmpeg's own "speed" (how fast it is encoding against real time) is behind when the ENCODER can't keep up.
    ///  - A slow graphics card can also starve the CAPTURE side while the encoder has nothing to do but repeat the last
    ///    frame it got, which is nearly free to encode: "speed" stays near 1.0x and the problem is invisible to it.
    ///    Measured on a friend's clip (GTX 1650, Windows 10): 81% of the frames in a "0.99x real time" recording were
    ///    exact repeats, which plays back as a choppy slideshow at roughly a fifth of the frame rate it was saved at.
    ///    So a report also counts as behind when most of what it wrote since the last report was a repeat, not new
    ///    content: FFmpeg already reports this (<see cref="EncodeStats.DuplicatedFrames"/>), it just wasn't used before.
    /// Either way, a run of readings rather than one, because a loading screen legitimately has nothing new to draw for
    /// a moment, on either signal.
    /// </summary>
    public sealed class LoadGovernor
    {
        /// <summary>Encoding this much slower than real time counts as behind.</summary>
        public const double BehindBelow = 0.92;

        /// <summary>
        /// This share (or more) of the frames written since the last report being repeats, not new content, counts as
        /// behind. Generous on purpose: normal play always has a few repeats (a still moment, an even frame boundary),
        /// and this only needs to catch a recording that is substantially a slideshow, not shave a healthy one further.
        /// </summary>
        public const double DuplicateRatioAbove = 0.5;

        /// <summary>Consecutive progress reports (two seconds apart) that must all be behind.</summary>
        public const int BehindReports = 5;

        /// <summary>Start-up is slow by nature (FFmpeg opens the encoder and the sound pipes); readings before this are ignored.</summary>
        public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(12);

        private int behind;
        private int behindAtLimit;
        private EncodeStats? previous;

        /// <summary>
        /// Looks at one progress report and decides what it means for this recording: step down a level, or (already at
        /// the highest level this governor may take) that it is behind with nowhere left to go. Call it once per report;
        /// it compares frame counts against the report before, so calling it twice on the same one would compare it
        /// with itself and find no new duplicates.
        /// </summary>
        public GovernorVerdict Evaluate(EncodeStats stats, DateTime startedUtc, int currentLevel, int highestLevel)
        {
            bool ready = stats.AtUtc - startedUtc >= Warmup && stats.Speed > 0;   // "N/A": nothing measured yet
            string reason = "";
            bool bad = ready && IsBehind(stats, out reason);
            previous = stats;

            if (!ready)
            {
                behind = 0;
                behindAtLimit = 0;
                return GovernorVerdict.None;
            }

            if (currentLevel < highestLevel)
            {
                behindAtLimit = 0;
                if (!bad) { behind = 0; return GovernorVerdict.None; }
                if (++behind < BehindReports) return GovernorVerdict.None;
                behind = 0;
                return new GovernorVerdict(true, false, reason);
            }

            behind = 0;
            if (!bad) { behindAtLimit = 0; return GovernorVerdict.None; }
            if (++behindAtLimit < BehindReports) return GovernorVerdict.None;
            behindAtLimit = 0;
            return new GovernorVerdict(false, true, reason);
        }

        private bool IsBehind(EncodeStats stats, out string reason)
        {
            reason = "";
            if (stats.Speed < BehindBelow)
            {
                reason = $"encoding at {stats.Speed:0.00}x real time";
                return true;
            }

            if (previous != null)
            {
                long newFrames = stats.Frames - previous.Frames;
                long newDuplicates = stats.DuplicatedFrames - previous.DuplicatedFrames;
                if (newFrames > 0 && (double)newDuplicates / newFrames >= DuplicateRatioAbove)
                {
                    reason = "the graphics card can't produce new frames fast enough";
                    return true;
                }
            }

            return false;
        }

        public void Reset()
        {
            behind = 0;
            behindAtLimit = 0;
            previous = null;
        }
    }
}
