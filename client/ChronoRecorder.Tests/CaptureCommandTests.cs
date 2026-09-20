using System.Drawing;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class CaptureCommandTests
    {
        private const string Pattern = @"C:\Temp\Chrono\segment_20260920120000_%05d.ts";

        private static CaptureSource Dda(int output = 0, int w = 2560, int h = 1440)
            => new(CaptureMethod.DesktopDuplication, output, new Rectangle(0, 0, w, h));

        private static CaptureRequest Request(string encoder, CaptureSource source, Size? output = null, int fps = 60)
            => new(encoder, fps, 8000, source, output ?? source.Bounds.Size, 10, Pattern);

        // ---------------------------------------------------------------- GPU capture

        [Fact]
        public void Native_OnNvidia_CapturesAndEncodesWithoutTouchingTheCpu()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda(output: 1)));

            Assert.Contains("-f lavfi -i \"ddagrab=output_idx=1:framerate=60\"", args);
            Assert.DoesNotContain("-vf", args);          // no filters: frames stay on the GPU
            Assert.DoesNotContain("-pix_fmt", args);     // would force a CPU conversion
            Assert.DoesNotContain("gdigrab", args);
            Assert.Contains("-c:v h264_nvenc", args);
        }

        [Theory]
        [InlineData("h264_amf")]
        [InlineData("h264_qsv")]
        public void Native_OnOtherHardwareEncoders_AlsoStaysOnTheGpu(string encoder)
        {
            string args = CaptureCommand.Build(Request(encoder, Dda()));

            Assert.DoesNotContain("-vf", args);
            Assert.Contains($"-c:v {encoder}", args);
        }

        [Fact]
        public void Native_WithSoftwareEncoding_MustDownloadTheFrame()
        {
            string args = CaptureCommand.Build(Request("libx264", Dda()));

            Assert.Contains("-vf \"hwdownload,format=bgra,format=yuv420p\"", args);
        }

        [Fact]
        public void Scaled_DownloadsAndScalesOnTheCpu()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda(), new Size(1920, 1080)));

            Assert.Contains("-vf \"hwdownload,format=bgra,scale=1920:1080:flags=fast_bilinear,format=nv12\"", args);
        }

        [Fact]
        public void Scaled_WithSoftwareEncoding_EndsInYuv420p()
        {
            string args = CaptureCommand.Build(Request("libx264", Dda(), new Size(1280, 720)));

            Assert.Contains("scale=1280:720:flags=fast_bilinear,format=yuv420p\"", args);
        }

        [Fact]
        public void UsesTheConfiguredFrameRateAndBitrate()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda(), fps: 90));

            Assert.Contains("framerate=90", args);
            Assert.Contains("-b:v 8000k", args);
            Assert.Contains("-g 180", args);
        }

        // ------------------------------------------------------------ GDI fallback

        [Fact]
        public void Gdi_CapturesOnlyThatMonitorsRegionOfTheDesktop()
        {
            // The second monitor sits left of and above the primary, so its offsets are negative.
            var source = new CaptureSource(CaptureMethod.Gdi, 0, new Rectangle(-1440, -246, 1440, 2560));

            string args = CaptureCommand.Build(Request("h264_nvenc", source));

            Assert.Contains("-f gdigrab -framerate 60 -offset_x -1440 -offset_y -246 -video_size 1440x2560 -i desktop", args);
            Assert.Contains("-pix_fmt yuv420p", args);
            Assert.DoesNotContain("ddagrab", args);
            Assert.DoesNotContain("hwdownload", args);
            Assert.DoesNotContain("-vf", args);
        }

        [Fact]
        public void Gdi_Scaled_UsesASimpleScaleFilter()
        {
            var source = new CaptureSource(CaptureMethod.Gdi, 0, new Rectangle(0, 0, 2560, 1440));

            string args = CaptureCommand.Build(Request("h264_nvenc", source, new Size(1920, 1080)));

            Assert.Contains("-vf \"scale=1920:1080:flags=fast_bilinear\"", args);
        }

        // ---------------------------------------------------------- segmenting

        [Fact]
        public void WritesOneContinuousStreamOfRollingSegments()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda()));

            Assert.Contains("-f segment", args);
            Assert.Contains("-segment_time 10", args);
            Assert.Contains("-segment_format mpegts", args);
            Assert.Contains("-segment_format_options flush_packets=1", args);   // in-progress segment stays readable
            Assert.Contains("-reset_timestamps 1", args);
            Assert.EndsWith($"-y \"{Pattern}\"", args);
        }

        [Fact]
        public void KeyframesAreForcedByTime_SoSegmentsSplitOnBoundaries()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda()));

            Assert.Contains("-force_key_frames \"expr:gte(t,n_forced*2)\"", args);
        }

        [Fact]
        public void KeepsFfmpegQuiet()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda()));

            Assert.StartsWith("-hide_banner -nostats -loglevel warning", args);
        }
    }
}
