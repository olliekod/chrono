using System.Drawing;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class ExportCommandTests
    {
        private const string List = @"C:\Temp\Chrono\concat_abc.txt";
        private const string Out = @"D:\Clips\clip.mp4";

        private static ExportRequest Request(ExportMode mode, string encoder = "h264_nvenc", double seek = 4.25, double length = 15,
            Size? size = null, int fps = 60, int bitrate = 8000)
            => new(List, Out, seek, length, mode, size ?? new Size(1920, 1080), encoder, bitrate, fps);

        // ------------------------------------------------------------------- plain copy

        [Fact]
        public void Copy_JoinsTheSegmentsWithoutReencoding()
        {
            string args = ExportCommand.Build(Request(ExportMode.Copy));

            Assert.Contains($"-f concat -safe 0 -i \"{List}\"", args);
            Assert.Contains("-c copy", args);
            Assert.Contains("-movflags +faststart", args);
            Assert.EndsWith($"-y \"{Out}\"", args);
        }

        [Fact]
        public void Copy_HasNoLengthLimit_BecauseTheStartIsSnappedToAKeyframe()
        {
            // The plan already carries a keyframe interval of slack, so copy mode must not cut the end off.
            Assert.DoesNotContain(" -t ", ExportCommand.Build(Request(ExportMode.Copy)));
        }

        [Fact]
        public void Copy_SeeksBeforeTheInput_AndOnlyWhenNeeded()
        {
            Assert.Contains("-ss 4.250 -f concat", ExportCommand.Build(Request(ExportMode.Copy, seek: 4.25)));
            Assert.DoesNotContain("-ss", ExportCommand.Build(Request(ExportMode.Copy, seek: 0)));
            Assert.DoesNotContain("-ss", ExportCommand.Build(Request(ExportMode.Copy, seek: 0.01)));
        }

        // -------------------------------------------------------------- GPU (NVIDIA)

        [Fact]
        public void Gpu_DecodesAndResizesOnTheGpuThenEncodesWithNvenc()
        {
            string args = ExportCommand.Build(Request(ExportMode.Gpu));

            Assert.Contains("-c:v h264_cuvid -resize 1920x1080", args);
            Assert.Contains("-c:v h264_nvenc", args);
            Assert.Contains("-b:v 8000k", args);
            Assert.DoesNotContain("-vf", args);          // no filters: frames never leave the GPU
            Assert.DoesNotContain("scale=", args);
        }

        [Fact]
        public void Gpu_TheDecoderOptionsBelongToTheInput_SoTheyComeBeforeIt()
        {
            string args = ExportCommand.Build(Request(ExportMode.Gpu));

            int input = args.IndexOf(" -i ");
            Assert.True(args.IndexOf("-c:v h264_cuvid") < input);
            Assert.True(args.IndexOf("-resize") < input);
            Assert.True(args.IndexOf("-ss") < input);
            Assert.True(args.IndexOf("-c:v h264_nvenc") > input);    // the encoder is an output option
        }

        [Fact]
        public void Gpu_CutsToExactlyTheRequestedLength_BecauseItReencodes()
        {
            string args = ExportCommand.Build(Request(ExportMode.Gpu, length: 30));

            Assert.Contains("-t 30.000", args);
        }

        [Fact]
        public void Gpu_KeepsTheSoundTrackAsItIs()
        {
            Assert.Contains("-c:a copy", ExportCommand.Build(Request(ExportMode.Gpu)));
        }

        // ------------------------------------------------------------------ CPU fallback

        [Theory]
        [InlineData("h264_nvenc")]
        [InlineData("h264_amf")]
        [InlineData("h264_qsv")]
        [InlineData("libx264")]
        public void Cpu_ScalesInSoftwareAndUsesTheConfiguredEncoder(string encoder)
        {
            string args = ExportCommand.Build(Request(ExportMode.Cpu, encoder: encoder, size: new Size(1280, 720)));

            Assert.Contains("-vf \"scale=1280:720:flags=bicubic\"", args);
            Assert.Contains($"-c:v {encoder}", args);
            Assert.DoesNotContain("cuvid", args);
            Assert.Contains("-t 15.000", args);
            Assert.Contains("-c:a copy", args);
        }

        [Fact]
        public void Cpu_UnknownEncodersFallBackToSoftware()
        {
            Assert.Contains("-c:v libx264", ExportCommand.Build(Request(ExportMode.Cpu, encoder: "something_else")));
        }

        // ------------------------------------------------------------------------ common

        [Theory]
        [InlineData(ExportMode.Gpu)]
        [InlineData(ExportMode.Cpu)]
        public void Transcoding_AlwaysWritesFastStartMp4(ExportMode mode)
        {
            string args = ExportCommand.Build(Request(mode));

            Assert.Contains("-movflags +faststart", args);
            Assert.EndsWith($"-y \"{Out}\"", args);
            Assert.StartsWith("-hide_banner -loglevel error", args);
        }

        [Theory]
        [InlineData(ExportMode.Copy)]
        [InlineData(ExportMode.Gpu)]
        [InlineData(ExportMode.Cpu)]
        public void Numbers_AreWrittenTheSameWayInAnyCulture(ExportMode mode)
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                string args = ExportCommand.Build(Request(mode, seek: 4.25, length: 15.5));

                Assert.Contains("-ss 4.250", args);
                Assert.DoesNotContain("4,250", args);
                if (mode != ExportMode.Copy) Assert.Contains("-t 15.500", args);
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        }
    }

    public class ExportPlannerTests
    {
        private static readonly Size Qhd = new(2560, 1440);

        [Theory]
        [InlineData("native")]
        [InlineData("")]
        [InlineData("2560x1440")]
        [InlineData("3840x2160")]
        public void NoResizeIsNeeded_WhenTheSettingIsNativeOrNotSmaller(string setting)
        {
            Assert.Null(ExportPlanner.ExportSize(setting, Qhd));
        }

        [Theory]
        [InlineData("1920x1080", 1920, 1080)]
        [InlineData("1280x720", 1280, 720)]
        public void ASmallerSetting_MeansShrinkingTheClip(string setting, int w, int h)
        {
            Assert.Equal(new Size(w, h), ExportPlanner.ExportSize(setting, Qhd));
        }

        [Fact]
        public void WithoutAKnownMonitorSize_NothingIsResized()
        {
            Assert.Null(ExportPlanner.ExportSize("1920x1080", Size.Empty));
        }

        [Fact]
        public void CopyMode_PlansOneKeyframeIntervalOfSlack_ButReencodingPlansTheExactLength()
        {
            Assert.Equal(30 + EncoderProfile.KeyframeIntervalSeconds, ExportPlanner.PlannedSeconds(30, transcode: false));
            Assert.Equal(30, ExportPlanner.PlannedSeconds(30, transcode: true));
        }

        [Theory]
        [InlineData("h264_nvenc", true)]
        [InlineData("H264_NVENC", true)]
        [InlineData("h264_amf", false)]
        [InlineData("h264_qsv", false)]
        [InlineData("libx264", false)]
        [InlineData("", false)]
        public void OnlyNvidiaEncodingTakesTheGpuPath(string encoder, bool expected)
        {
            Assert.Equal(expected, ExportPlanner.CanUseGpu(encoder));
        }
    }
}
