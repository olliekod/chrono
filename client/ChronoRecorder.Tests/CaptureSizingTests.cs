using System.Drawing;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class CaptureSizingTests
    {
        private static readonly Size Qhd = new(2560, 1440);

        [Theory]
        [InlineData("native")]
        [InlineData("NATIVE")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("garbage")]
        [InlineData("1920xabc")]
        public void NativeOrUnreadable_MeansNoScaling(string? setting)
        {
            Assert.Equal(Qhd, CaptureSizing.Resolve(setting, Qhd));
        }

        [Theory]
        [InlineData("1920x1080", 1920, 1080)]
        [InlineData("1280x720", 1280, 720)]
        public void SmallerThanTheMonitor_ScalesDown(string setting, int w, int h)
        {
            Assert.Equal(new Size(w, h), CaptureSizing.Resolve(setting, Qhd));
        }

        [Theory]
        [InlineData("2560x1440")]
        [InlineData("3840x2160")]
        public void EqualOrLarger_NeverUpscales(string setting)
        {
            Assert.Equal(Qhd, CaptureSizing.Resolve(setting, Qhd));
        }

        [Fact]
        public void SameSizeAsTheMonitor_IsNative()
        {
            Assert.Equal(new Size(1920, 1080), CaptureSizing.Resolve("1920x1080", new Size(1920, 1080)));
        }

        [Fact]
        public void TheHeightDecidesTheQuality_AndTheAspectRatioIsKept()
        {
            // 1080p on a 21:9 ultrawide is 2580x1080, not a squashed 1920x1080.
            Assert.Equal(new Size(2580, 1080), CaptureSizing.Resolve("1920x1080", new Size(3440, 1440)));
        }

        [Fact]
        public void Dimensions_AreAlwaysEven()
        {
            // 1366x768 -> 720 tall would be 1280.6 wide; encoders need even sizes.
            var size = CaptureSizing.Resolve("1280x720", new Size(1366, 768));
            Assert.Equal(0, size.Width % 2);
            Assert.Equal(0, size.Height % 2);
        }

        [Theory]
        [InlineData("native", 60, "Native60")]
        [InlineData("1920x1080", 60, "1080p60")]
        [InlineData("1280x720", 30, "720p30")]
        [InlineData("", 90, "Native90")]
        public void QualityLabel_ShowsWhatTheUserChose(string setting, int fps, string expected)
        {
            Assert.Equal(expected, CaptureSizing.QualityLabel(setting, fps));
        }

        [Theory]
        [InlineData(2560, 1440, "2560x1440")]
        [InlineData(1920, 1080, "1920x1080")]
        public void Describe_IsWhatTheServerAccepts(int w, int h, string expected)
        {
            Assert.Equal(expected, CaptureSizing.Describe(new Size(w, h)));
        }
    }
}
