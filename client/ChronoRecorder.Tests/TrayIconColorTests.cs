using System.Drawing;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class TrayIconColorTests
    {
        [Fact]
        public void TheLogosRedDotIsRed_ButItsRingAndDiscAreNot()
        {
            Assert.True(TrayIcons.Redness(243, 65, 64) > 0.95);        // the dot
            Assert.Equal(0, TrayIcons.Redness(87, 102, 247));          // the blue ring
            Assert.Equal(0, TrayIcons.Redness(30, 30, 33));            // the dark disc
            Assert.Equal(0, TrayIcons.Redness(255, 255, 255));         // the dot's white highlight
        }

        [Fact]
        public void OnlyTheDotChangesColour()
        {
            var grey = Color.FromArgb(148, 155, 164);

            Assert.Equal((87, 102, 247), TrayIcons.Recolor(87, 102, 247, grey));   // ring untouched
            Assert.Equal((30, 30, 33), TrayIcons.Recolor(30, 30, 33, grey));       // disc untouched
            var dot = TrayIcons.Recolor(243, 65, 64, grey);
            Assert.InRange(dot.R, 140, 160);                                       // now grey
            Assert.InRange(dot.G, 145, 165);
        }

        [Fact]
        public void TheDotsSoftEdgeBlendsInsteadOfLeavingARedFringe()
        {
            var amber = Color.FromArgb(250, 168, 26);
            // Halfway between the dot's red and the dark disc.
            var edge = TrayIcons.Recolor(135, 47, 48, amber);

            Assert.True(edge.R > 100 && edge.R < 200);
            Assert.True(edge.G > 47, "the green channel moved towards amber");
        }

        [Fact]
        public void RecordingKeepsTheLogosOwnRed() => Assert.Equal(Color.FromArgb(240, 65, 64), TrayIcons.DotColor(TrayState.Recording));

        [Fact]
        public void WaitingIsGrey_AndPausedIsAmber()
        {
            Assert.Equal(Color.FromArgb(148, 155, 164), TrayIcons.DotColor(TrayState.Idle));
            Assert.Equal(Color.FromArgb(250, 168, 26), TrayIcons.DotColor(TrayState.Paused));
        }

        [Fact]
        public void EveryStateBuildsARealIcon()
        {
            foreach (var state in new[] { TrayState.Recording, TrayState.Idle, TrayState.Paused })
            {
                var icon = TrayIcons.For(state);
                Assert.True(icon.Width >= 16 && icon.Height >= 16);
            }
        }

        [Fact]
        public void TheLogoIsEmbeddedInTheProgram()
        {
            using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("logo-256.png");
            Assert.NotNull(stream);
            using var bitmap = new Bitmap(stream!);
            Assert.Equal(256, bitmap.Width);
        }
    }
}
