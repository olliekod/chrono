using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ChronoRecorder
{
    /// <summary>Turns config strings into Win32 RegisterHotKey arguments.</summary>
    public static class HotkeyParser
    {
        public const uint ModNone = 0x0000;
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;

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

        public static uint ParseKey(string key)
        {
            // Function keys (F1-F24)
            if (key.Length > 1 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key.Substring(1), out int fNum))
            {
                if (fNum < 1 || fNum > 24)
                    throw new ArgumentException($"Unknown key: {key}");

                return (uint)(Keys.F1 + fNum - 1);
            }

            switch (key.ToLower())
            {
                case "pageup":
                case "pgup":
                    return (uint)Keys.PageUp;
                case "pagedown":
                case "pgdn":
                case "pgdown":
                    return (uint)Keys.PageDown;
                case "home":
                    return (uint)Keys.Home;
                case "end":
                    return (uint)Keys.End;
                case "insert":
                case "ins":
                    return (uint)Keys.Insert;
                case "delete":
                case "del":
                    return (uint)Keys.Delete;
                case "space":
                    return (uint)Keys.Space;
                case "enter":
                case "return":
                    return (uint)Keys.Enter;
                default:
                    if (key.Length == 1)
                        return char.ToUpper(key[0]);
                    break;
            }

            throw new ArgumentException($"Unknown key: {key}");
        }
    }
}
