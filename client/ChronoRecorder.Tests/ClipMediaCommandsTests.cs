using System;
using System.Drawing;
using System.Linq;
using Xunit;
using static ChronoRecorder.ClipMediaCommands;

namespace ChronoRecorder.Tests
{
    public class ClipMediaCommandsTests
    {
        // ------------------------------------------------------------------ poster

        [Theory]
        [InlineData(60.0, 1.5)]
        [InlineData(2.0, 1.0)]      // a very short clip: its middle
        [InlineData(0.0, 0.0)]
        public void ThePosterIsTakenAlittleIn(double duration, double expected)
        {
            Assert.Equal(expected, PosterTime(duration));
        }

        [Fact]
        public void ThePosterTimeCopesWithAnUnknownLength()
        {
            Assert.Equal(0, PosterTime(null));
        }

        [Fact]
        public void PosterTakesOneScaledFrame()
        {
            string args = Poster(@"C:\Clips\a.mp4", @"C:\Thumbs\a.jpg", 1.5, 480);

            Assert.Contains("-ss 1.5 -i \"C:\\Clips\\a.mp4\"", args);
            Assert.Contains("-frames:v 1", args);
            Assert.Contains("scale=480:-2", args);
            Assert.EndsWith("-y \"C:\\Thumbs\\a.jpg\"", args);
        }

        // ---------------------------------------------------------------- filmstrip

        [Theory]
        [InlineData(32.47, 16)]     // a keyframe every 2 s
        [InlineData(60.0, 30)]
        [InlineData(0.4, 1)]         // never fewer than one tile
        [InlineData(1000.0, 90)]     // and never a huge picture
        public void TheStripHasOneTilePerKeyframe(double duration, int expected)
        {
            Assert.Equal(expected, FilmstripFrames(duration));
        }

        [Theory]
        [InlineData(2560, 1440, 90, 160)]
        [InlineData(1920, 1080, 90, 160)]
        [InlineData(1440, 2560, 90, 50)]     // rotated monitor
        [InlineData(0, 0, 90, 160)]          // unknown shape: assume 16:9
        public void TilesKeepTheClipsShape_WithAnEvenWidth(int w, int h, int height, int expectedWidth)
        {
            var tile = TileSize(new Size(w, h), height);

            Assert.Equal(height, tile.Height);
            Assert.Equal(expectedWidth, tile.Width);
            Assert.Equal(0, tile.Width % 2);
        }

        [Fact]
        public void TheStripDecodesOnlyKeyframesInOnePass()
        {
            string args = Filmstrip(@"C:\Clips\a.mp4", @"C:\Thumbs\a_strip.jpg", 32.47, new Size(160, 90));

            Assert.Equal(1, args.Split("-i \"").Length - 1);               // one input, not one per tile
            Assert.Contains("-skip_frame nokey -i \"C:\\Clips\\a.mp4\"", args);
            Assert.Contains("scale=160:90,tile=16x1,format=yuvj420p", args);
            Assert.Contains("-frames:v 1", args);
            Assert.EndsWith("-y \"C:\\Thumbs\\a_strip.jpg\"", args);
        }

        // --------------------------------------------------------------------- trim

        private static TrimRequest Req(double start = 10, double end = 30, string encoder = "h264_nvenc", bool gpu = true)
            => new(@"C:\Clips\a.mp4", @"C:\Clips\a_trim.mp4", start, end, encoder, 20000, 60, gpu);

        [Fact]
        public void TrimSeeksBeforeTheInput_AndKeepsTheRequestedLength()
        {
            string args = Trim(Req(10, 30));

            Assert.Contains("-ss 10 ", args);
            Assert.True(args.IndexOf("-ss 10") < args.IndexOf("-i \""));
            Assert.Contains("-t 20 ", args);
        }

        [Fact]
        public void TrimReEncodesThePictureAndCopiesTheSound()
        {
            string args = Trim(Req());

            Assert.Contains("-c:v h264_nvenc", args);
            Assert.Contains("-b:v 20000k", args);
            Assert.Contains("-c:a copy", args);
            Assert.Contains("-movflags +faststart", args);
            Assert.EndsWith("-y \"C:\\Clips\\a_trim.mp4\"", args);
        }

        [Fact]
        public void TrimDecodesOnTheGpuOnlyWhenAsked()
        {
            Assert.Contains("-c:v h264_cuvid -i", Trim(Req(gpu: true)));
            Assert.DoesNotContain("cuvid", Trim(Req(gpu: false, encoder: "libx264")));
        }

        [Fact]
        public void TrimKeepsFractionsOfASecond_InAnyCulture()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                string args = Trim(Req(12.345, 20.5));

                Assert.Contains("-ss 12.345 ", args);
                Assert.Contains("-t 8.155 ", args);
                Assert.DoesNotContain("12,345", args);
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        }

        [Fact]
        public void TrimRefusesNonsense()
        {
            Assert.Throws<ArgumentException>(() => Trim(Req(-1, 10)));
            Assert.Throws<ArgumentException>(() => Trim(Req(10, 10)));
            Assert.Throws<ArgumentException>(() => Trim(Req(10, 10.2)));   // shorter than the minimum
            Assert.Throws<ArgumentException>(() => Trim(Req(20, 10)));
        }
    }
}
