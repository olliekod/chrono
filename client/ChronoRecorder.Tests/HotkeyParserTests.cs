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
        [InlineData("Backslash", Keys.OemPipe)]          // Ctrl + \ was refused before
        [InlineData("Slash", Keys.OemQuestion)]
        [InlineData("Comma", Keys.Oemcomma)]
        [InlineData("Period", Keys.OemPeriod)]
        [InlineData("Semicolon", Keys.OemSemicolon)]
        [InlineData("Quote", Keys.OemQuotes)]
        [InlineData("BracketLeft", Keys.OemOpenBrackets)]
        [InlineData("BracketRight", Keys.OemCloseBrackets)]
        [InlineData("Minus", Keys.OemMinus)]
        [InlineData("Equal", Keys.Oemplus)]
        [InlineData("Backquote", Keys.Oemtilde)]
        [InlineData("IntlBackslash", Keys.OemBackslash)]
        [InlineData("Tab", Keys.Tab)]
        [InlineData("Backspace", Keys.Back)]
        [InlineData("Escape", Keys.Escape)]
        [InlineData("ArrowUp", Keys.Up)]
        [InlineData("ArrowLeft", Keys.Left)]
        [InlineData("PrintScreen", Keys.PrintScreen)]
        [InlineData("Pause", Keys.Pause)]
        [InlineData("ScrollLock", Keys.Scroll)]
        [InlineData("Numpad0", Keys.NumPad0)]
        [InlineData("Numpad7", Keys.NumPad7)]
        [InlineData("NumpadAdd", Keys.Add)]
        [InlineData("NumpadDivide", Keys.Divide)]
        [InlineData("5", Keys.D5)]
        public void ParseKey_UnderstandsEveryKeyTheBrowserNames(string input, Keys expected)
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

        // ---------------------------------------------------- what a hotkey may be

        [Theory]
        [InlineData("Control", "Backslash")]                 // Ctrl + \
        [InlineData("Control,Shift", "Backslash")]           // Ctrl + Shift + \  (three keys)
        [InlineData("Control,Alt", "Delete")]
        [InlineData("Alt,Shift", "Q")]
        [InlineData("Win", "F9")]
        [InlineData("Control,Shift", "Slash")]
        [InlineData("Control", "Numpad5")]
        [InlineData("Shift", "ArrowUp")]
        public void CombinationsOfUpToThreeKeys_AreAllowed(string mods, string key)
        {
            Assert.Null(HotkeyParser.Problem(mods.Split(','), key));
        }

        [Fact]
        public void FourKeysAreTooMany()
        {
            string? problem = HotkeyParser.Problem(new[] { "Control", "Alt", "Shift" }, "A");
            Assert.Contains("at most 3", problem);
        }

        [Fact]
        public void TheSameModifierTwiceIsStillOne()
        {
            Assert.Null(HotkeyParser.Problem(new[] { "Control", "ctrl" }, "A"));
        }

        [Theory]
        [InlineData("A")]
        [InlineData("5")]
        [InlineData("Backslash")]
        [InlineData("Space")]
        [InlineData("Enter")]
        [InlineData("ArrowLeft")]
        [InlineData("Tab")]
        [InlineData("Escape")]
        public void KeysPeopleTypeNeedAModifier(string key)
        {
            Assert.Contains("Add Ctrl", HotkeyParser.Problem(new string[0], key));
        }

        [Theory]
        [InlineData("F1")]
        [InlineData("F24")]
        [InlineData("PageUp")]
        [InlineData("End")]
        [InlineData("Insert")]
        [InlineData("PrintScreen")]
        public void FunctionAndNavigationKeysWorkAlone(string key)
        {
            Assert.Null(HotkeyParser.Problem(new string[0], key));
        }

        [Fact]
        public void AnUnknownKeyIsRefusedWithItsName()
        {
            Assert.Contains("Fn", HotkeyParser.Problem(new[] { "Control" }, "Fn"));
        }

        [Fact]
        public void ParseModifiers_Empty_IsNone()
        {
            Assert.Equal(0u, HotkeyParser.ParseModifiers(new List<string>()));
        }
    }
}
