using System.Windows.Forms;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class HotkeyParserTests
    {
        [Theory]
        [InlineData("F8", Keys.F8)]
        [InlineData("f1", Keys.F1)]
        [InlineData("F24", Keys.F24)]
        [InlineData("PageUp", Keys.PageUp)]
        [InlineData("pgup", Keys.PageUp)]
        [InlineData("Home", Keys.Home)]
        [InlineData("a", Keys.A)]
        [InlineData("F", Keys.F)]
        public void ParseKey_MapsToVirtualKeys(string input, Keys expected)
        {
            Assert.Equal((uint)expected, HotkeyParser.ParseKey(input));
        }

        [Theory]
        [InlineData("F0")]
        [InlineData("F25")]
        [InlineData("Banana")]
        [InlineData("")]
        public void ParseKey_RejectsInvalidKeys(string input)
        {
            Assert.Throws<ArgumentException>(() => HotkeyParser.ParseKey(input));
        }

        [Fact]
        public void ParseModifiers_CombinesFlags_CaseInsensitively()
        {
            uint mods = HotkeyParser.ParseModifiers(new List<string> { "Control", "SHIFT", "alt" });

            Assert.Equal(HotkeyParser.ModControl | HotkeyParser.ModShift | HotkeyParser.ModAlt, mods);
        }

        [Fact]
        public void ParseModifiers_AcceptsAliases()
        {
            Assert.Equal(HotkeyParser.ModControl, HotkeyParser.ParseModifiers(new List<string> { "ctrl" }));
            Assert.Equal(HotkeyParser.ModWin, HotkeyParser.ParseModifiers(new List<string> { "windows" }));
        }

        [Fact]
        public void ParseModifiers_Empty_IsNone()
        {
            Assert.Equal(0u, HotkeyParser.ParseModifiers(new List<string>()));
        }
    }
}
