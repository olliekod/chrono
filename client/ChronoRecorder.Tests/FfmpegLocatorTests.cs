using System.IO;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class FfmpegLocatorTests
    {
        private static readonly string App = Path.Combine("C:", "Apps", "Chrono");

        [Fact]
        public void PrefersTheCopyInTheAppsFfmpegFolder()
        {
            string bundled = Path.Combine(App, "ffmpeg", "ffmpeg.exe");
            Assert.Equal(bundled, FfmpegLocator.Locate(App, p => true));
        }

        [Fact]
        public void AlsoFindsOneBesideTheApp()
        {
            string beside = Path.Combine(App, "ffmpeg.exe");
            Assert.Equal(beside, FfmpegLocator.Locate(App, p => p == beside));
        }

        [Fact]
        public void WithNoBundledCopy_FallsBackToThePath()
        {
            Assert.Equal("ffmpeg", FfmpegLocator.Locate(App, p => false));
        }
    }
}
