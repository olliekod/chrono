using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace ChronoRecorder
{
    /// <summary>Turns config strings into Win32 RegisterHotKey arguments, and holds the rules for what a hotkey may be.</summary>
    public static class HotkeyParser
    {
        public const uint ModNone = 0x0000;
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;

        /// <summary>Stops a held key from firing again and again (each fire would save another clip).</summary>
        public const uint ModNoRepeat = 0x4000;

        /// <summary>The most keys in one hotkey, counting modifiers: Ctrl + Shift + \ is three.</summary>
        public const int MaxKeys = 3;

        public static uint ParseModifiers(IEnumerable<string> modifiers)
        {
            uint result = ModNone;

            foreach (var mod in modifiers)
            {
                switch (mod.ToLower())
                {
                    case "control":
                    case "ctrl":
                        result |= ModControl;
                        break;
                    case "alt":
                        result |= ModAlt;
                        break;
                    case "shift":
                        result |= ModShift;
                        break;
                    case "win":
                    case "windows":
                        result |= ModWin;
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Named keys other than letters, digits and F1-F24. The names are the ones the browser calls a key
        /// (<c>KeyboardEvent.code</c>), so what the settings page records is exactly what is stored here.
        /// The older short spellings (PgUp, Del...) still work.
        /// </summary>
        private static readonly Dictionary<string, Keys> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PageUp"] = Keys.PageUp, ["PgUp"] = Keys.PageUp,
            ["PageDown"] = Keys.PageDown, ["PgDn"] = Keys.PageDown, ["PgDown"] = Keys.PageDown,
            ["Home"] = Keys.Home, ["End"] = Keys.End,
            ["Insert"] = Keys.Insert, ["Ins"] = Keys.Insert,
            ["Delete"] = Keys.Delete, ["Del"] = Keys.Delete,
            ["Space"] = Keys.Space, ["Enter"] = Keys.Enter, ["Return"] = Keys.Enter,
            ["Tab"] = Keys.Tab, ["Backspace"] = Keys.Back, ["Escape"] = Keys.Escape,
            ["ArrowUp"] = Keys.Up, ["ArrowDown"] = Keys.Down, ["ArrowLeft"] = Keys.Left, ["ArrowRight"] = Keys.Right,
            ["PrintScreen"] = Keys.PrintScreen, ["ScrollLock"] = Keys.Scroll, ["Pause"] = Keys.Pause,
            ["CapsLock"] = Keys.CapsLock, ["NumLock"] = Keys.NumLock, ["ContextMenu"] = Keys.Apps,

            // Punctuation, by the US keyboard's names for them (the same physical keys on other layouts).
            ["Backslash"] = Keys.OemPipe,          // \ |
            ["Slash"] = Keys.OemQuestion,          // / ?
            ["Comma"] = Keys.Oemcomma,             // , <
            ["Period"] = Keys.OemPeriod,           // . >
            ["Semicolon"] = Keys.OemSemicolon,     // ; :
            ["Quote"] = Keys.OemQuotes,            // ' "
            ["BracketLeft"] = Keys.OemOpenBrackets,   // [ {
            ["BracketRight"] = Keys.OemCloseBrackets, // ] }
            ["Minus"] = Keys.OemMinus,             // - _
            ["Equal"] = Keys.Oemplus,              // = +
            ["Backquote"] = Keys.Oemtilde,         // ` ~
            ["IntlBackslash"] = Keys.OemBackslash, // the extra key next to left Shift on ISO keyboards

            ["NumpadMultiply"] = Keys.Multiply, ["NumpadAdd"] = Keys.Add, ["NumpadSubtract"] = Keys.Subtract,
            ["NumpadDecimal"] = Keys.Decimal, ["NumpadDivide"] = Keys.Divide,
        };

        public static uint ParseKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("No key given");

            // Function keys (F1-F24)
            if (key.Length > 1 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key.Substring(1), out int fNum))
            {
                if (fNum < 1 || fNum > 24)
                    throw new ArgumentException($"Unknown key: {key}");

                return (uint)(Keys.F1 + fNum - 1);
            }

            // Numpad0-Numpad9
            if (key.Length == 7 && key.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) && char.IsDigit(key[6]))
                return (uint)(Keys.NumPad0 + (key[6] - '0'));

            if (Named.TryGetValue(key, out var named)) return (uint)named;

            // A letter or a digit: its character is its virtual-key code.
            if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return char.ToUpper(key[0]);

            throw new ArgumentException($"Unknown key: {key}");
        }

        // ----------------------------------------------------------------------- what a hotkey may be

        /// <summary>
        /// Keys that are safe to use on their own. Everything else (letters, digits, punctuation, arrows, Space, Enter...)
        /// is something people type or press all the time, so it needs Ctrl, Alt, Shift or Win with it.
        /// </summary>
        public static bool WorksAlone(string key)
        {
            if (key.Length > 1 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key.Substring(1), out int n)) return n >= 1 && n <= 24;
            return key.ToLowerInvariant() is "pageup" or "pgup" or "pagedown" or "pgdn" or "pgdown" or "home" or "end" or "insert" or "ins"
                or "delete" or "del" or "printscreen" or "scrolllock" or "pause";
        }

        /// <summary>Why this combination can't be a hotkey, or null when it can.</summary>
        public static string? Problem(IEnumerable<string> modifiers, string key)
        {
            var distinct = modifiers.Select(m => m.ToLowerInvariant() switch { "ctrl" => "control", "windows" => "win", var x => x }).Distinct().ToList();
            if (distinct.Count + 1 > MaxKeys) return $"Use at most {MaxKeys} keys, for example Ctrl + Shift + a key.";
            if (distinct.Count == 0 && !WorksAlone(key)) return "Add Ctrl, Alt, Shift or Win. On its own this key would trigger whenever you type or navigate.";

            try { ParseKey(key); }
            catch (ArgumentException) { return $"Chrono can't use the key \"{key}\"."; }
            return null;
        }
    }
}
