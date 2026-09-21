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

    /// <summary>
    /// Decides when a recording can't keep up and should move down a step. It reads how fast FFmpeg is encoding against
    /// real time (its "speed"): 1.0 is keeping up, and a run of readings well under that means frames are being lost.
    /// A run rather than one reading, because a loading screen can starve the encoder for a moment.
    /// </summary>
    public sealed class LoadGovernor
    {
        /// <summary>Encoding this much slower than real time counts as behind.</summary>
        public const double BehindBelow = 0.92;

        /// <summary>Consecutive progress reports (two seconds apart) that must all be behind.</summary>
        public const int BehindReports = 5;

        /// <summary>Start-up is slow by nature (FFmpeg opens the encoder and the sound pipes); readings before this are ignored.</summary>
        public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(12);

        private int behind;
        private int behindAtLimit;

        /// <summary>True (once per run of bad readings) when this report means the recording should move down a step.</summary>
        public bool ShouldStepDown(EncodeStats stats, DateTime startedUtc, int currentLevel, int highestLevel, out string reason)
        {
            reason = "";
            if (currentLevel >= highestLevel) { behind = 0; return false; }
            if (stats.AtUtc - startedUtc < Warmup) return false;
            if (stats.Speed <= 0) return false;   // "N/A": nothing measured yet

            if (stats.Speed >= BehindBelow)
            {
                behind = 0;
                return false;
            }

            behind++;
            if (behind < BehindReports) return false;

            behind = 0;
            reason = $"encoding at {stats.Speed:0.00}x real time";
            return true;
        }

        /// <summary>
        /// True (once per run of bad readings) when the recording is behind and the governor has no step left it may take,
        /// so the only remedy is one the user has to choose, such as a lower frame rate.
        /// </summary>
        public bool IsBehindAtTheLimit(EncodeStats stats, DateTime startedUtc)
        {
            if (stats.AtUtc - startedUtc < Warmup || stats.Speed <= 0) return false;
            if (stats.Speed >= BehindBelow) { behindAtLimit = 0; return false; }
            if (++behindAtLimit < BehindReports) return false;
            behindAtLimit = 0;
            return true;
        }

        public void Reset() { behind = 0; behindAtLimit = 0; }
    }
}
