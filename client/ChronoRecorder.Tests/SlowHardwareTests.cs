using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class FfmpegProgressParserTests
    {
        private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

        // What FFmpeg printed for -progress pipe:1 -stats_period 2 on the dev PC.
        private static readonly string[] Block =
        {
            "frame=1794", "fps=59.83", "stream_0_0_q=-1.0", "bitrate= 597.8kbits/s", "total_size=1114112",
            "out_time_us=29900000", "out_time_ms=29900000", "out_time=00:00:29.900000", "dup_frames=188", "drop_frames=3",
            "speed=0.994x", "progress=continue"
        };

        private static EncodeStats? Feed(FfmpegProgressParser parser, params string[] lines)
        {
            EncodeStats? last = null;
            foreach (var line in lines) last = parser.Feed(line, Now) ?? last;
            return last;
        }

        [Fact]
        public void ABlock_IsReportedWhenItsProgressLineArrives_AndNotBefore()
        {
            var parser = new FfmpegProgressParser();

            foreach (var line in Block.Take(Block.Length - 1)) Assert.Null(parser.Feed(line, Now));

            var stats = parser.Feed(Block[^1], Now)!;
            Assert.Equal(1794, stats.Frames);
            Assert.Equal(59.83, stats.Fps, 2);
            Assert.Equal(188, stats.DuplicatedFrames);
            Assert.Equal(3, stats.DroppedFrames);
            Assert.Equal(0.994, stats.Speed, 3);
            Assert.Equal(597.8, stats.BitrateKbps, 1);
            Assert.Equal(29.9, stats.OutSeconds, 2);
            Assert.Equal(Now, stats.AtUtc);
        }

        [Fact]
        public void NotAvailableValues_CountAsZero_SoNothingThrows()
        {
            var stats = Feed(new FfmpegProgressParser(), "frame=0", "fps=0.00", "bitrate=N/A", "out_time_us=N/A", "dup_frames=0", "drop_frames=0", "speed=N/A", "progress=continue")!;

            Assert.Equal(0, stats.Speed);
            Assert.Equal(0, stats.BitrateKbps);
            Assert.Equal(0, stats.OutSeconds);
        }

        [Fact]
        public void EachBlockStartsFresh()
        {
            var parser = new FfmpegProgressParser();
            Feed(parser, Block);

            var second = Feed(parser, "frame=1900", "speed=1.00x", "progress=continue")!;

            Assert.Equal(1900, second.Frames);
            Assert.Equal(0, second.DroppedFrames);   // the first block's 3 must not carry over
        }

        [Fact]
        public void ABlockWithNoFrameCount_IsIgnored()
        {
            Assert.Null(Feed(new FfmpegProgressParser(), "speed=1x", "progress=continue"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("no equals sign")]
        [InlineData("=leading")]
        public void NoiseIsIgnored(string line)
        {
            Assert.Null(new FfmpegProgressParser().Feed(line, Now));
        }

        [Fact]
        public void TheEndBlock_IsReportedToo()
        {
            var stats = Feed(new FfmpegProgressParser(), "frame=600", "speed=0.99x", "progress=end");
            Assert.NotNull(stats);
        }
    }

    public class GpuReadingTests
    {
        [Fact]
        public void EngineNames_GiveTheProcessAndTheKindOfEngine()
        {
            Assert.True(GpuEngineName.TryParse("pid_12345_luid_0x00000000_0x0000D3A4_phys_0_eng_3_engtype_VideoEncode", out int pid, out string type));
            Assert.Equal(12345, pid);
            Assert.Equal("VideoEncode", type);

            Assert.True(GpuEngineName.TryParse("pid_7_luid_0x00000000_0x0000D3A4_phys_0_eng_0_engtype_3D", out pid, out type));
            Assert.Equal(7, pid);
            Assert.Equal("3D", type);

            Assert.True(GpuEngineName.TryParse("pid_9_luid_0x0_0x1_phys_0_eng_1_engtype_Compute_0", out _, out type));
            Assert.Equal("Compute_0", type);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("engtype_3D")]
        [InlineData("pid_abc_luid_x_engtype_3D")]
        public void OtherNames_AreNotEngines(string? name)
        {
            Assert.False(GpuEngineName.TryParse(name, out _, out _));
        }

        [Theory]
        [InlineData("NVIDIA GeForce RTX 4080", "32.0.15.7602", "576.02")]
        [InlineData("NVIDIA GeForce GTX 1650", "31.0.15.3623", "536.23")]
        [InlineData("NVIDIA GeForce RTX 3060", "32.0.15.6094", "560.94")]
        [InlineData("AMD Radeon RX 6600", "31.0.21001.45002", "31.0.21001.45002")]
        [InlineData("Intel(R) UHD Graphics 770", "31.0.101.5186", "31.0.101.5186")]
        [InlineData("NVIDIA GeForce RTX 4080", "", "unknown")]
        [InlineData("NVIDIA GeForce RTX 4080", "1.2", "1.2")]
        public void NvidiaDriversAreShownTheWayEveryoneKnowsThem(string gpu, string windowsVersion, string expected)
        {
            Assert.Equal(expected, DriverVersions.Friendly(gpu, windowsVersion));
        }

        [Fact]
        public void CpuUse_IsThePercentageOfOneCoreOverTheInterval()
        {
            var t0 = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

            Assert.Equal(25, CpuSampler.PercentOfOneCore(TimeSpan.FromSeconds(10), t0, TimeSpan.FromSeconds(10.5), t0.AddSeconds(2)), 3);
            Assert.Equal(200, CpuSampler.PercentOfOneCore(TimeSpan.Zero, t0, TimeSpan.FromSeconds(4), t0.AddSeconds(2)), 3);   // two cores busy
            Assert.Equal(0, CpuSampler.PercentOfOneCore(TimeSpan.Zero, t0, TimeSpan.FromSeconds(1), t0));                       // no time passed
        }
    }

    public class GraphicsCardTierTests
    {
        [Theory]
        [InlineData("NVIDIA GeForce GTX 1650")]
        [InlineData("NVIDIA GeForce GTX 1650 with Max-Q Design")]
        [InlineData("NVIDIA GeForce GTX 1660 Ti")]
        [InlineData("NVIDIA GeForce GTX 1060 6GB")]
        [InlineData("NVIDIA GeForce GTX 960")]
        [InlineData("NVIDIA GeForce RTX 2060")]
        [InlineData("NVIDIA GeForce RTX 2080 Ti")]
        [InlineData("NVIDIA GeForce RTX 3050")]
        [InlineData("NVIDIA GeForce RTX 3060")]
        [InlineData("NVIDIA GeForce RTX 3060 Ti")]
        [InlineData("NVIDIA GeForce RTX 3060 Laptop GPU")]
        [InlineData("NVIDIA GeForce RTX 4050 Laptop GPU")]
        [InlineData("NVIDIA GeForce MX450")]
        [InlineData("NVIDIA GeForce GT 1030")]
        public void ModestNvidiaCards(string name)
        {
            Assert.Equal(GpuDetector.GpuTier.Modest, GpuDetector.TierOf(GpuDetector.GpuType.NVIDIA, name));
        }

        [Theory]
        [InlineData("NVIDIA GeForce RTX 3070")]
        [InlineData("NVIDIA GeForce RTX 3070 Ti")]
        [InlineData("NVIDIA GeForce RTX 3080")]
        [InlineData("NVIDIA GeForce RTX 3090")]
        [InlineData("NVIDIA GeForce RTX 4060")]
        [InlineData("NVIDIA GeForce RTX 4070 SUPER")]
        [InlineData("NVIDIA GeForce RTX 4080")]
        [InlineData("NVIDIA GeForce RTX 5090")]
        public void CapableNvidiaCards(string name)
        {
            Assert.Equal(GpuDetector.GpuTier.Capable, GpuDetector.TierOf(GpuDetector.GpuType.NVIDIA, name));
        }

        [Theory]
        [InlineData("AMD Radeon RX 580", GpuDetector.GpuTier.Modest)]
        [InlineData("AMD Radeon RX 5500 XT", GpuDetector.GpuTier.Modest)]
        [InlineData("AMD Radeon RX 6600", GpuDetector.GpuTier.Modest)]
        [InlineData("AMD Radeon RX 6800 XT", GpuDetector.GpuTier.Capable)]
        [InlineData("AMD Radeon RX 7900 XTX", GpuDetector.GpuTier.Capable)]
        [InlineData("AMD Radeon(TM) Graphics", GpuDetector.GpuTier.Modest)]
        public void AmdCards(string name, GpuDetector.GpuTier expected)
        {
            Assert.Equal(expected, GpuDetector.TierOf(GpuDetector.GpuType.AMD, name));
        }

        [Theory]
        [InlineData("Intel(R) UHD Graphics 770", GpuDetector.GpuTier.Modest)]
        [InlineData("Intel(R) Iris(R) Xe Graphics", GpuDetector.GpuTier.Modest)]
        [InlineData("Intel(R) Arc(TM) A380 Graphics", GpuDetector.GpuTier.Modest)]
        [InlineData("Intel(R) Arc(TM) A770 Graphics", GpuDetector.GpuTier.Capable)]
        public void IntelCards(string name, GpuDetector.GpuTier expected)
        {
            Assert.Equal(expected, GpuDetector.TierOf(GpuDetector.GpuType.Intel, name));
        }

        [Fact]
        public void AnUnknownCard_IsTreatedAsModest_BecauseStartingLightCostsLessThanStartingHeavy()
        {
            Assert.Equal(GpuDetector.GpuTier.Modest, GpuDetector.TierOf(GpuDetector.GpuType.Unknown, "Microsoft Basic Display Adapter"));
        }

        [Fact]
        public void TheTierComesWithTheIdentifiedCard()
        {
            Assert.Equal(GpuDetector.GpuTier.Modest, GpuDetector.Identify("NVIDIA GeForce GTX 1650").Tier);
            Assert.Equal(GpuDetector.GpuTier.Capable, GpuDetector.Identify("NVIDIA GeForce RTX 4080").Tier);
        }
    }

    public class LoadPlanTests
    {
        [Fact]
        public void TheStepsAreThePresetThenTheFrameRate()
        {
            Assert.Equal(EncodeLoad.Normal, LoadPlan.LoadFor(0));
            Assert.Equal(EncodeLoad.Light, LoadPlan.LoadFor(1));
            Assert.Equal(EncodeLoad.Light, LoadPlan.LoadFor(2));

            Assert.Equal(60, LoadPlan.FpsFor(0, 60));
            Assert.Equal(60, LoadPlan.FpsFor(1, 60));
            Assert.Equal(30, LoadPlan.FpsFor(2, 60));
            Assert.Equal(24, LoadPlan.FpsFor(2, 24));   // never raised
        }

        [Fact]
        public void ThereIsNothingLeftToLower_WhenTheFrameRateIsAlreadyThirty()
        {
            Assert.Equal(2, LoadPlan.HighestUsefulLevel(60, allowFrameRate: true));
            Assert.Equal(1, LoadPlan.HighestUsefulLevel(30, allowFrameRate: true));
            Assert.Equal(1, LoadPlan.HighestUsefulLevel(24, allowFrameRate: true));
        }

        [Theory]
        [InlineData("h264_nvenc", true)]
        [InlineData("H264_NVENC", true)]
        [InlineData("h264_amf", false)]
        [InlineData("h264_qsv", false)]
        [InlineData("libx264", false)]
        [InlineData("auto", false)]
        public void OnlyNvencHasALighterPresetToStepTo(string encoder, bool expected)
        {
            Assert.Equal(expected, LoadPlan.HasPresetStep(encoder));
        }

        [Fact]
        public void WithoutAPresetStep_TheGovernorGoesStraightToTheFrameRate()
        {
            // The first step would change nothing for AMD, Intel or the processor, and cost 20 seconds of falling behind to find out.
            Assert.Equal(1, LoadPlan.NextLevel(0, presetStep: true));
            Assert.Equal(2, LoadPlan.NextLevel(0, presetStep: false));
            Assert.Equal(2, LoadPlan.NextLevel(1, presetStep: true));
            Assert.Equal(2, LoadPlan.NextLevel(2, presetStep: true));   // never beyond the last
        }

        [Fact]
        public void WithoutAPresetStep_ThereIsNothingToLower_AtThirtyFps()
        {
            Assert.Equal(2, LoadPlan.HighestUsefulLevel(60, presetStep: false, allowFrameRate: true));
            Assert.Equal(0, LoadPlan.HighestUsefulLevel(30, presetStep: false, allowFrameRate: true));
        }

        [Theory]
        [InlineData("auto", GpuDetector.GpuTier.Modest, 0, 0)]    // no lighter preset to start on, so a modest AMD or Intel card starts normal
        [InlineData("light", GpuDetector.GpuTier.Capable, 0, 0)]  // "light" can't mean anything here
        [InlineData("autofps", GpuDetector.GpuTier.Capable, 2, 2)]   // but what an earlier run learned still applies
        [InlineData("auto", GpuDetector.GpuTier.Capable, 2, 0)]      // unless it lowered the frame rate, which plain "auto" no longer does
        public void WhereARecordingStarts_WithoutALighterPreset(string setting, GpuDetector.GpuTier tier, int learned, int expected)
        {
            Assert.Equal(expected, LoadPlan.StartLevel(setting, tier, learned, configuredFps: 60, presetStep: false));
        }

        [Theory]
        [InlineData("normal", GpuDetector.GpuTier.Modest, 2, 0)]     // fixed settings ignore the card and what was learned
        [InlineData("light", GpuDetector.GpuTier.Capable, 0, 1)]
        [InlineData("auto", GpuDetector.GpuTier.Capable, 0, 0)]      // a strong card starts at normal
        [InlineData("auto", GpuDetector.GpuTier.Modest, 0, 1)]       // a modest one starts light
        [InlineData("autofps", GpuDetector.GpuTier.Capable, 2, 2)]   // and an earlier run's finding wins over the guess
        [InlineData("autofps", GpuDetector.GpuTier.Modest, 2, 2)]
        [InlineData("auto", GpuDetector.GpuTier.Capable, 2, 1)]      // plain "auto" keeps the frame rate, even if an older version learned to lower it
        [InlineData("auto", GpuDetector.GpuTier.Modest, 2, 1)]
        [InlineData(null, GpuDetector.GpuTier.Capable, 0, 0)]
        public void WhereARecordingStarts(string? setting, GpuDetector.GpuTier tier, int learned, int expected)
        {
            Assert.Equal(expected, LoadPlan.StartLevel(setting, tier, learned, configuredFps: 60));
        }

        [Theory]
        [InlineData("auto", false)]
        [InlineData("AUTO", false)]
        [InlineData("autofps", true)]
        [InlineData("normal", false)]
        [InlineData("light", false)]
        [InlineData(null, false)]
        public void OnlyAutofps_MayLowerTheFrameRate(string? setting, bool expected)
        {
            Assert.Equal(expected, LoadPlan.MayLowerFrameRate(setting));
        }

        [Theory]
        [InlineData("auto", true)]
        [InlineData("autofps", true)]
        [InlineData("normal", false)]
        [InlineData("light", false)]
        public void OnlyTheAutomaticSettings_LetTheGovernorMoveTheRecording(string setting, bool expected)
        {
            Assert.Equal(expected, LoadPlan.IsAutomatic(setting));
        }

        [Fact]
        public void AtThirtyFps_TheStartIsNeverAboveTheLastUsefulStep()
        {
            Assert.Equal(1, LoadPlan.StartLevel("autofps", GpuDetector.GpuTier.Modest, learned: 2, configuredFps: 30));
        }

        [Fact]
        public void Steps_AreDescribedInWords()
        {
            Assert.Equal("Normal", LoadPlan.Describe(0, 60));
            Assert.Equal("Light encoder", LoadPlan.Describe(1, 60));
            Assert.Equal("Light encoder, 30 FPS", LoadPlan.Describe(2, 60));
            Assert.Equal("Light encoder", LoadPlan.Describe(2, 30));   // it can't be said to run at 30 when it always did
        }

        [Fact]
        public void WithoutALighterPreset_OnlyTheFrameRateIsMentioned()
        {
            Assert.Equal("Normal", LoadPlan.Describe(0, 60, presetStep: false));
            Assert.Equal("Normal", LoadPlan.Describe(1, 60, presetStep: false));
            Assert.Equal("Reduced to 30 FPS", LoadPlan.Describe(2, 60, presetStep: false));
        }
    }

    public class LoadGovernorTests
    {
        private static readonly DateTime Start = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

        // Frames constant across reports (no duplicate-ratio signal) unless a test says otherwise: these exercise the
        // speed-based half of the governor only.
        private static EncodeStats Report(double secondsIn, double speed)
            => new(1000, 60, 0, 0, speed, 20000, secondsIn, Start.AddSeconds(secondsIn));

        private static bool Step(LoadGovernor g, double secondsIn, double speed, int level = 0, int highest = 2)
            => g.Evaluate(Report(secondsIn, speed), Start, level, highest).StepDown;

        private static bool AtLimit(LoadGovernor g, double secondsIn, double speed)
            => g.Evaluate(Report(secondsIn, speed), Start, currentLevel: 2, highestLevel: 2).BehindAtLimit;

        [Fact]
        public void BehindWithNoStepLeft_IsReportedOncePerRun_SoTheUserCanChooseALowerFrameRate()
        {
            var g = new LoadGovernor();
            var said = new List<bool>();
            for (int i = 0; i < 10; i++) said.Add(AtLimit(g, 20 + i * 2, 0.76));

            Assert.Equal(new[] { false, false, false, false, true, false, false, false, false, true }, said);
        }

        [Fact]
        public void BehindWithNoStepLeft_IsNotReported_DuringWarmUpOrWhenKeepingUp()
        {
            var g = new LoadGovernor();
            for (int i = 0; i < 5; i++) Assert.False(AtLimit(g, 2 + i * 2, 0.5));   // still starting up
            for (int i = 0; i < 20; i++) Assert.False(AtLimit(g, 30 + i * 2, 1.0));
        }

        [Fact]
        public void ARecordingThatKeepsUp_IsLeftAlone()
        {
            var g = new LoadGovernor();
            for (int i = 0; i < 100; i++) Assert.False(Step(g, 20 + i * 2, 1.0));
        }

        [Fact]
        public void FiveReportsInARowThatAreBehind_StepDownOnce()
        {
            var g = new LoadGovernor();
            var results = Enumerable.Range(0, 5).Select(i => Step(g, 20 + i * 2, 0.80)).ToList();

            Assert.Equal(new[] { false, false, false, false, true }, results);
        }

        [Fact]
        public void OneGoodReport_BreaksTheRun()
        {
            // A loading screen can starve the encoder for a moment; that is not a reason to lower anyone's quality.
            var g = new LoadGovernor();
            Assert.False(Step(g, 20, 0.7));
            Assert.False(Step(g, 22, 0.7));
            Assert.False(Step(g, 24, 0.7));
            Assert.False(Step(g, 26, 0.7));
            Assert.False(Step(g, 28, 1.0));   // recovered
            Assert.False(Step(g, 30, 0.7));
            Assert.False(Step(g, 32, 0.7));
        }

        [Fact]
        public void StartUpIsSlowByNature_SoTheFirstSecondsDontCount()
        {
            var g = new LoadGovernor();
            for (int i = 0; i < 20; i++) Assert.False(Step(g, i * 0.5, 0.2));   // all inside the warm-up
        }

        [Fact]
        public void NoReadingYet_IsNotBehind()
        {
            var g = new LoadGovernor();
            for (int i = 0; i < 20; i++) Assert.False(Step(g, 20 + i * 2, 0));
        }

        [Fact]
        public void AtTheLastStep_ThereIsNowhereToGo()
        {
            var g = new LoadGovernor();
            for (int i = 0; i < 20; i++) Assert.False(Step(g, 20 + i * 2, 0.5, level: 2, highest: 2));
        }

        [Fact]
        public void JustAboveTheLine_IsStillKeepingUp()
        {
            var g = new LoadGovernor();
            for (int i = 0; i < 20; i++) Assert.False(Step(g, 20 + i * 2, LoadGovernor.BehindBelow + 0.01));
        }

        [Fact]
        public void TheReasonSaysHowFarBehind()
        {
            var g = new LoadGovernor();
            GovernorVerdict verdict = default;
            for (int i = 0; i < 5; i++) verdict = g.Evaluate(Report(20 + i * 2, 0.71), Start, 0, 2);

            Assert.Contains("0.71x", verdict.Reason);
        }

        // ------------------------------------------------------- duplicate frames: the encoder looks fine, capture isn't

        // A friend's GTX 1650 clip: FFmpeg reported ~1.0x real time throughout while 81% of the frames it wrote were
        // exact repeats of the one before, because the graphics card couldn't hand the game and Desktop Duplication
        // enough real frames. speed alone never caught this; these reports carry growing Frames/DuplicatedFrames instead.
        private static EncodeStats DupReport(double secondsIn, long frames, long duplicates, double speed = 0.99)
            => new(frames, 60, duplicates, 0, speed, 20000, secondsIn, Start.AddSeconds(secondsIn));

        [Fact]
        public void MostlyRepeatedFrames_CountsAsBehind_EvenWhenTheEncoderSpeedLooksFine()
        {
            var g = new LoadGovernor();
            g.Evaluate(DupReport(20, frames: 1200, duplicates: 0), Start, 0, 2);   // establishes a baseline to compare against

            var results = new List<bool>();
            for (int i = 1; i <= 5; i++)
                // +120 frames (2s at 60fps) each report, 100 of them repeats: 83%, well past the 50% line.
                results.Add(g.Evaluate(DupReport(20 + i * 2, frames: 1200 + i * 120, duplicates: i * 100), Start, 0, 2).StepDown);

            Assert.Equal(new[] { false, false, false, false, true }, results);
        }

        [Fact]
        public void ReasonMentionsTheGraphicsCard_NotASpeedNumber_ForRepeatedFrames()
        {
            var g = new LoadGovernor();
            g.Evaluate(DupReport(20, frames: 1200, duplicates: 0), Start, 0, 2);

            GovernorVerdict verdict = default;
            for (int i = 1; i <= 5; i++) verdict = g.Evaluate(DupReport(20 + i * 2, frames: 1200 + i * 120, duplicates: i * 100), Start, 0, 2);

            Assert.Contains("graphics card", verdict.Reason);
        }

        [Fact]
        public void AFewRepeatedFrames_IsNormalPlay_AndIsLeftAlone()
        {
            // A still moment or an even frame boundary, not a slideshow: comfortably under the 50% line.
            var g = new LoadGovernor();
            g.Evaluate(DupReport(20, frames: 1200, duplicates: 0), Start, 0, 2);

            for (int i = 1; i <= 20; i++)
                Assert.False(g.Evaluate(DupReport(20 + i * 2, frames: 1200 + i * 120, duplicates: i * 20), Start, 0, 2).StepDown);   // ~17%
        }

        [Fact]
        public void TheFirstReportAfterWarmUp_HasNothingToCompareAgainst_SoItIsNeverBehindOnDuplicatesAlone()
        {
            var g = new LoadGovernor();
            // No baseline yet: even a report that would look 100% duplicated against a real earlier one can't be judged.
            Assert.False(g.Evaluate(DupReport(20, frames: 5000, duplicates: 4900), Start, 0, 2).StepDown);
        }

        [Fact]
        public void RepeatedFrames_AlsoTriggerTheAtTheLimitWarning()
        {
            var g = new LoadGovernor();
            g.Evaluate(DupReport(20, frames: 1200, duplicates: 0), Start, 2, 2);

            var said = new List<bool>();
            for (int i = 1; i <= 5; i++) said.Add(g.Evaluate(DupReport(20 + i * 2, frames: 1200 + i * 120, duplicates: i * 100), Start, 2, 2).BehindAtLimit);

            Assert.Equal(new[] { false, false, false, false, true }, said);
        }

        [Fact]
        public void CallingEvaluateTwiceOnTheSameReport_ComparesItWithItself_SoTheSecondCallFindsNoNewDuplicates()
        {
            var g = new LoadGovernor();
            g.Evaluate(DupReport(20, frames: 1200, duplicates: 0), Start, 0, 2);

            var same = DupReport(22, frames: 1320, duplicates: 100);
            Assert.False(g.Evaluate(same, Start, 0, 2).StepDown);
            for (int i = 0; i < 10; i++) Assert.False(g.Evaluate(same, Start, 0, 2).StepDown);   // frames never advance from here
        }
    }

    public class EncoderProbeTests
    {
        [Fact]
        public void AnOldDriver_IsSaidPlainly()
        {
            string text = "[h264_nvenc @ 000001f2] Driver does not support the required nvenc API version. Required: 13.0 Found: 12.2\n" +
                          "[h264_nvenc @ 000001f2] The minimum required Nvidia driver for nvenc is 570.0 or newer";

            Assert.True(EncoderProbe.LooksLikeEncoderFailure(text));
            Assert.Contains("too old", EncoderProbe.Explain(text));
            Assert.Contains("nvidia.com", EncoderProbe.Explain(text));
        }

        [Fact]
        public void EncoderSessionsUsedUp_PointsAtOtherRecordingPrograms()
        {
            string text = "[h264_nvenc @ 000002] OpenEncodeSessionEx failed: out of memory (10): (no details)";

            Assert.True(EncoderProbe.LooksLikeEncoderFailure(text));
            Assert.Contains("OBS", EncoderProbe.Explain(text));
        }

        [Fact]
        public void AMissingDriverLibrary_SaysToInstallTheDriver()
        {
            Assert.Contains("driver", EncoderProbe.Explain("[h264_nvenc @ 0000] Cannot load nvcuda.dll"));
            Assert.Contains("driver", EncoderProbe.Explain("Failed loading nvcuda.dll"));
        }

        [Fact]
        public void AnAmdEncoderThatIsntInstalled_IsExplained_AsSeenOnAPcWithNoAmdCard()
        {
            // The exact line FFmpeg printed when the AMD encoder was tried on an NVIDIA PC.
            string text = "[AMF @ 000002247ce90880] DLL amfrt64.dll failed to open";

            Assert.True(EncoderProbe.LooksLikeEncoderFailure(text));
            Assert.Contains("AMD", EncoderProbe.Explain(text));
            Assert.Contains("driver", EncoderProbe.Explain(text));
        }

        [Fact]
        public void NoCapableDevice_IsSaid()
        {
            Assert.Contains("No graphics card", EncoderProbe.Explain("[h264_nvenc @ 0000] No capable devices found"));
        }

        [Theory]
        [InlineData("[h264_amf @ 0000] AMF failed to initialise")]
        [InlineData("[h264_qsv @ 0000] Error initializing an internal MFX session")]
        [InlineData("Error while opening encoder for output stream #0:0")]
        public void OtherEncoderFailures_AreRecognisedByTheirEncoderName(string text)
        {
            Assert.True(EncoderProbe.LooksLikeEncoderFailure(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("[lavfi @ 0000] Impossible to open graph")]
        [InlineData("[gfxcapture @ 0000] Failed to create capture item")]
        public void ACaptureProblem_IsNotBlamedOnTheEncoder(string? text)
        {
            // Blaming the encoder for a capture failure would send a working card to the processor.
            Assert.False(EncoderProbe.LooksLikeEncoderFailure(text));
        }

        [Fact]
        public void AnUnknownFailure_ShowsFfmpegsOwnFirstLine_ShortEnough()
        {
            string longLine = new string('x', 500);
            string said = EncoderProbe.Explain(longLine + "\nsecond line");

            Assert.Equal(160, said.Length);
        }

        [Fact]
        public void TheProcessorEncoderNeedsNoTest()
        {
            Assert.True(EncoderProbe.Test("libx264").Ok);
        }
    }

    public class LightEncoderTests
    {
        [Fact]
        public void Light_UsesTheFasterNvencPreset_AndNothingElseChanges()
        {
            string normal = EncoderProfile.BuildArgs("h264_nvenc", 20000, 60, EncodeSpeed.Live, EncodeLoad.Normal);
            string light = EncoderProfile.BuildArgs("h264_nvenc", 20000, 60, EncodeSpeed.Live, EncodeLoad.Light);

            Assert.Contains("-preset p4", normal);
            Assert.Contains("-preset p3", light);
            Assert.Equal(normal.Replace("-preset p4", "-preset p3"), light);   // same rate control, AQ, keyframes, bitrate
        }

        [Fact]
        public void AdaptiveQuantisation_StaysOn_BecauseItCostsTheEncoderNothing()
        {
            // Measured on an RTX 4080 at 1440p60: p1 with spatial-aq 4.8% of the video encoder, without it 5.0%.
            Assert.Contains("-spatial-aq 1", EncoderProfile.BuildArgs("h264_nvenc", 20000, 60, EncodeSpeed.Live, EncodeLoad.Light));
        }

        [Theory]
        [InlineData("h264_amf")]
        [InlineData("h264_qsv")]
        [InlineData("libx264")]
        public void OtherEncoders_AreAlreadyAsLightAsTheyGet(string encoder)
        {
            Assert.Equal(
                EncoderProfile.BuildArgs(encoder, 20000, 60, EncodeSpeed.Live, EncodeLoad.Normal),
                EncoderProfile.BuildArgs(encoder, 20000, 60, EncodeSpeed.Live, EncodeLoad.Light));
        }

        private static CaptureRequest Request(EncodeLoad load, int fps = 60) => new(
            "h264_nvenc", fps, 20000, new CaptureSource(CaptureMethod.WindowCapture, 0, new Rectangle(0, 0, 2560, 1440), 1234),
            10, @"C:\temp\segment_%05d.ts", ClockStartUnixSeconds: 1758400000.0, Load: load);

        [Fact]
        public void TheCaptureCommand_CarriesTheLoad()
        {
            Assert.Contains("-preset p4", CaptureCommand.Build(Request(EncodeLoad.Normal)));
            Assert.Contains("-preset p3", CaptureCommand.Build(Request(EncodeLoad.Light)));
        }

        [Fact]
        public void TheCaptureCommand_AsksForProgressNumbers_OnStandardOutput()
        {
            string args = CaptureCommand.Build(Request(EncodeLoad.Normal));

            Assert.Contains("-progress pipe:1 -stats_period 2", args);
            Assert.StartsWith("-hide_banner -nostats -loglevel warning", args);   // still quiet on the console
        }

        [Fact]
        public void TheFrameRateGoesIntoTheCapture_SoThirtyMeansThirty()
        {
            string args = CaptureCommand.Build(Request(EncodeLoad.Light, fps: 30));

            Assert.Contains("max_framerate=30", args);
            Assert.Contains("-fps_mode cfr -r 30", args);
            Assert.Contains("-g 60", args);   // a keyframe every two seconds at any frame rate
        }
    }

    public class DiagnosticsFindingsTests
    {
        private static FindingInputs Healthy() => new(
            Recording: true, Encoder: "h264_nvenc", EncoderNote: "", GpuType: GpuDetector.GpuType.NVIDIA, GpuTier: GpuDetector.GpuTier.Capable,
            Method: CaptureMethod.WindowCapture, Speed: 1.0, Dropped: 0, FfmpegCpuOneCore: 3, EncodeEnginePercent: 10, ThreeDEnginePercent: 20,
            LoadLevel: 0, ConfiguredFps: 60, ScreenAdapterVendorId: 0x10DE, ScreenAdapterName: "NVIDIA GeForce RTX 4080", RamGb: 64, GpuName: "NVIDIA GeForce RTX 4080");

        private static List<string> Tones(FindingInputs f) => DiagnosticsFindings.Evaluate(f).Select(x => x.Tone).ToList();
        private static string AllText(FindingInputs f) => string.Join("\n", DiagnosticsFindings.Evaluate(f).Select(x => x.Text));

        [Fact]
        public void AHealthyRecording_HasNothingToReport()
        {
            var findings = DiagnosticsFindings.Evaluate(Healthy());

            Assert.Single(findings);
            Assert.Equal("good", findings[0].Tone);
        }

        [Fact]
        public void MostlyRepeatedFrames_IsAFinding_EvenWithAHealthySpeed()
        {
            // A friend's real clip: 81% repeated frames at a reported 0.99x. Encoding speed alone missed it entirely.
            var findings = DiagnosticsFindings.Evaluate(Healthy() with { Frames = 2722, Duplicated = 2210 });

            Assert.Contains(findings, f => f.Tone == "bad" && f.Text.Contains("81%") && f.Text.Contains("choppy"));
        }

        [Fact]
        public void AFewRepeatedFrames_IsNotAFinding()
        {
            // The exact numbers from a real alt-tab burst (ADroppedFramesCount_IsExplainedNotAlarming): well under the line.
            var findings = DiagnosticsFindings.Evaluate(Healthy() with { Frames = 9000, Duplicated = 344 });

            Assert.DoesNotContain(findings, f => f.Tone == "bad" || f.Tone == "warn");
        }

        [Fact]
        public void NoFramesYet_IsNotJudged()
        {
            // Frames defaults to 0 (not recording, or nothing measured yet): dividing by it would be nonsense, not a finding.
            var findings = DiagnosticsFindings.Evaluate(Healthy() with { Frames = 0, Duplicated = 0 });

            Assert.Single(findings);
            Assert.Equal("good", findings[0].Tone);
        }

        [Fact]
        public void NotRecording_SaysThereIsNothingToMeasure_NotThatAllIsWell()
        {
            var findings = DiagnosticsFindings.Evaluate(Healthy() with { Recording = false });

            Assert.Contains("nothing to measure", findings[0].Text);
        }

        [Fact]
        public void ABrokenHardwareEncoder_IsTheFirstThingSaid_WithItsReason()
        {
            var f = Healthy() with { Encoder = "libx264", EncoderNote = "The NVIDIA graphics driver is too old." };

            var findings = DiagnosticsFindings.Evaluate(f);

            Assert.Equal("bad", findings[0].Tone);
            Assert.Contains("too old", findings[0].Text);
        }

        [Fact]
        public void TheProcessorEncoding_WithACardThatCouldHelp_IsWarnedAbout()
        {
            Assert.Contains("warn", Tones(Healthy() with { Encoder = "libx264" }));
        }

        [Fact]
        public void TheProcessorEncoding_WithNoCardToUse_IsJustHowItIs()
        {
            var f = Healthy() with { Encoder = "libx264", GpuType = GpuDetector.GpuType.Unknown };
            Assert.DoesNotContain("processor although", AllText(f));
        }

        [Fact]
        public void AnEncoderThatIsBehind_IsBad()
        {
            var f = Healthy() with { Speed = 0.80 };

            Assert.Contains("bad", Tones(f));
            Assert.Contains("0.80x", AllText(f));
        }

        [Fact]
        public void JustUnderRealTime_IsNotAlarming()
        {
            Assert.DoesNotContain("bad", Tones(Healthy() with { Speed = 0.97 }));
        }

        [Fact]
        public void DroppedFrames_AloneAreNeverFlagged()
        {
            Assert.DoesNotContain("dropped", AllText(Healthy() with { Dropped = 12 }));
        }

        [Fact]
        public void ReducedLoad_IsSaidSoNobodyWondersWhyItIsThirtyFps()
        {
            Assert.Contains("30 FPS", AllText(Healthy() with { LoadLevel = 2 }));
        }

        [Fact]
        public void ABusyEncoderAndABusy3DEngine_AreFlagged()
        {
            Assert.Contains("video encoder is 85% busy", AllText(Healthy() with { EncodeEnginePercent = 85 }));
            Assert.Contains("3D engine", AllText(Healthy() with { ThreeDEnginePercent = 70 }));
        }

        [Fact]
        public void ARunawayProcessorCore_IsFlagged()
        {
            Assert.Contains("core", AllText(Healthy() with { FfmpegCpuOneCore = 90 }));
        }

        [Fact]
        public void GdiCapture_IsExplained()
        {
            Assert.Contains("GDI", AllText(Healthy() with { Method = CaptureMethod.Gdi }));
        }

        [Fact]
        public void ALaptopWhoseScreenIsOnIntelWhileNvidiaEncodes_IsFlagged()
        {
            var f = Healthy() with { ScreenAdapterVendorId = 0x8086, ScreenAdapterName = "Intel(R) UHD Graphics" };

            string text = AllText(f);

            Assert.Contains("Intel(R) UHD Graphics", text);
            Assert.Contains("laptops", text);
        }

        [Fact]
        public void AScreenOnTheSameCardThatEncodes_IsNotFlagged()
        {
            Assert.DoesNotContain("laptops", AllText(Healthy()));
        }

        [Fact]
        public void LowMemory_IsMentioned()
        {
            Assert.Contains("4 GB", AllText(Healthy() with { RamGb = 4 }));
        }
    }

    public class DiagnosticsReportTests
    {
        private sealed class FakeSource : IDiagnosticsSource
        {
            public RecorderFacts Facts = Recording();

            public RecorderFacts GetFacts() => Facts;

            public static RecorderFacts Recording() => new(
                Recording: true, Target: "Deadlock", Method: CaptureMethod.WindowCapture, Size: new Size(2560, 1440), ConfiguredFps: 60, EffectiveFps: 60,
                Encoder: "h264_nvenc", LoadLevel: 0, LoadSetting: "auto", GovernorActive: true, BitrateKbps: 19900, BufferSeconds: 140, FfmpegPid: 0,
                StartedUtc: DateTime.UtcNow.AddMinutes(-3), Stats: new EncodeStats(9000, 59.9, 120, 0, 0.998, 19500, 150, DateTime.UtcNow),
                EncoderNote: "", AudioSources: new[] { "game sound" }, Events: new[] { "12:00:01 Recording started" },
                ScreenAdapterName: "NVIDIA GeForce RTX 4080", ScreenAdapterVendorId: 0x10DE);
        }

        [Theory]
        [InlineData("h264_nvenc", "NVIDIA NVENC H.264")]
        [InlineData("h264_amf", "AMD AMF H.264")]
        [InlineData("h264_qsv", "Intel Quick Sync H.264")]
        [InlineData("libx264", "x264 on the processor")]
        [InlineData("anything else", "x264 on the processor")]
        public void EncodersAreNamedForPeople(string encoder, string expected)
        {
            Assert.Equal(expected, DiagnosticsText.EncoderName(encoder));
        }

        [Theory]
        [InlineData(0, "0 sec")]
        [InlineData(45, "45 sec")]
        [InlineData(140, "2 min 20 sec")]
        [InlineData(3900, "1 h 05 min")]
        public void DurationsReadNaturally(double seconds, string expected)
        {
            Assert.Equal(expected, DiagnosticsText.Span(seconds));
        }

        [Fact]
        public void ThePageHasTheThreeSections_AndTheRowsThePersonAskedFor()
        {
            var dto = new DiagnosticsCollector(new FakeSource()).Collect();

            Assert.Equal(new[] { "Recording", "Performance", "This PC" }, dto.Sections.Select(s => s.Title));

            var labels = dto.Sections.SelectMany(s => s.Rows).Select(r => r.Label).ToList();
            foreach (var wanted in new[] { "Capture", "Encoder", "Input", "Bitrate", "Buffer", "FFmpeg CPU", "FFmpeg RAM", "GPU video encode", "Dropped frames", "Encoding speed", "Windows", "Processor", "Graphics", "FFmpeg" })
                Assert.Contains(wanted, labels);
        }

        [Fact]
        public void TheRecordingRows_ReadTheWayTheyWereAskedFor()
        {
            var dto = new DiagnosticsCollector(new FakeSource()).Collect();
            string Value(string label) => dto.Sections.SelectMany(s => s.Rows).First(r => r.Label == label).Value;

            Assert.Equal("Recording Deadlock", Value("Status"));
            Assert.Equal("Windows Graphics Capture (the game's window)", Value("Capture"));
            Assert.Equal("NVIDIA NVENC H.264", Value("Encoder"));
            Assert.Equal("2560\u00D71440 @ 60", Value("Input"));
            Assert.StartsWith("19.9 Mbps target", Value("Bitrate"));
            Assert.Equal("2 min 20 sec", Value("Buffer"));
            Assert.Equal("0", Value("Dropped frames"));
            Assert.Equal("1.00x real time", Value("Encoding speed"));
        }

        [Fact]
        public void ADroppedFramesCount_IsExplainedNotAlarming()
        {
            // FFmpeg's count climbs in bursts when a window is restored after an alt-tab. Nothing is lost by it.
            var source = new FakeSource();
            source.Facts = source.Facts with { Stats = new EncodeStats(9000, 60.3, 344, 1845, 1.0, 0, 150, DateTime.UtcNow) };

            var dto = new DiagnosticsCollector(source).Collect();

            var dropped = dto.Sections.SelectMany(s => s.Rows).First(r => r.Label == "Dropped frames");
            Assert.StartsWith("1845 (", dropped.Value);
            Assert.Contains("normal", dropped.Value);
            Assert.Equal("", dropped.Tone);
            Assert.DoesNotContain(dto.Findings, f => f.Tone == "warn" || f.Tone == "bad");
        }

        [Fact]
        public void RepeatedFrames_ShowsTheShare_AndIsToned()
        {
            var source = new FakeSource();
            source.Facts = source.Facts with { Stats = new EncodeStats(2722, 59.9, 2210, 0, 0.99, 10500, 45, DateTime.UtcNow) };

            var dto = new DiagnosticsCollector(source).Collect();
            var repeated = dto.Sections.SelectMany(s => s.Rows).First(r => r.Label == "Repeated frames");

            Assert.StartsWith("2210 (81%", repeated.Value);
            Assert.Equal("bad", repeated.Tone);
        }

        [Fact]
        public void ReducedFrameRate_ShowsWhatItWasSetTo()
        {
            var source = new FakeSource();
            source.Facts = source.Facts with { EffectiveFps = 30, LoadLevel = 2 };

            var dto = new DiagnosticsCollector(source).Collect();

            Assert.Equal("2560\u00D71440 @ 30 (set to 60)", dto.Sections[0].Rows.First(r => r.Label == "Input").Value);
        }

        [Fact]
        public void NotRecording_SaysSoInsteadOfShowingZeros()
        {
            var source = new FakeSource();
            source.Facts = source.Facts with { Recording = false, Stats = null, FfmpegPid = 0, Size = Size.Empty };

            var dto = new DiagnosticsCollector(source).Collect();
            string Value(string label) => dto.Sections.SelectMany(s => s.Rows).First(r => r.Label == label).Value;

            Assert.Equal("Not recording", Value("Status"));
            Assert.Equal("not recording", Value("FFmpeg CPU"));
            Assert.Equal("not recording", Value("Dropped frames"));
        }

        [Fact]
        public void TheReport_IsPlainText_WithNothingPersonalInIt()
        {
            var collector = new DiagnosticsCollector(new FakeSource());

            string report = collector.Report();

            Assert.StartsWith("Chrono ", report);
            Assert.Contains("[Recording]", report);
            Assert.Contains("[Performance]", report);
            Assert.Contains("[This PC]", report);
            Assert.Contains("[Findings]", report);
            Assert.Contains("12:00:01 Recording started", report);

            // Nothing that identifies a person or reaches their server.
            Assert.DoesNotContain(Environment.UserName, report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UploadKey", report);
            Assert.DoesNotContain("workers.dev", report);
        }
    }
}
