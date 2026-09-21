using System.Drawing;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class BitrateSizingTests
    {
        [Fact]
        public void AChosenBitrate_IsUsedAsItIs()
        {
            Assert.Equal(12000, BitrateSizing.Resolve(12000, new Size(2560, 1440), 60));
        }

        [Theory]
        [InlineData(2560, 1440, 60, 20000)]   // the case that looked blocky at the old fixed 8000
        [InlineData(1920, 1080, 60, 11000)]
        [InlineData(2560, 1440, 30, 10000)]
        public void Automatic_ScalesWithPixelsAndFrameRate(int w, int h, int fps, int expected)
        {
            Assert.Equal(expected, BitrateSizing.Resolve(0, new Size(w, h), fps));
        }

        [Fact]
        public void Automatic_NeverGoesBelowAFloor()
        {
            Assert.Equal(6000, BitrateSizing.Resolve(0, new Size(1280, 720), 30));
        }

        [Fact]
        public void Automatic_NeverGoesAboveACeiling()
        {
            Assert.Equal(40000, BitrateSizing.Resolve(0, new Size(3840, 2160), 144));
        }

        [Fact]
        public void ASizeWrittenAsText_IsUnderstood_AndGarbageFallsBackTo1080p()
        {
            Assert.Equal(20000, BitrateSizing.Resolve(0, "2560x1440", 60));
            Assert.Equal(BitrateSizing.Resolve(0, new Size(1920, 1080), 60), BitrateSizing.Resolve(0, "nonsense", 60));
            Assert.Equal(BitrateSizing.Resolve(0, new Size(1920, 1080), 60), BitrateSizing.Resolve(0, (string?)null, 60));
        }
    }
}
