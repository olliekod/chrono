using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ChronoRecorder
{
    /// <summary>
    /// Manages global hotkeys using Windows API
    /// </summary>
    public class HotkeyManager : IDisposable
    {
        // Windows API imports
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Modifier keys
        private const uint MOD_NONE = 0x0000;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;

        private readonly NativeWindow messageWindow;
        private readonly RecorderConfig config;

        public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

        public class HotkeyPressedEventArgs : EventArgs
        {
            public HotkeyConfig Hotkey { get; set; }
            public HotkeyPressedEventArgs(HotkeyConfig hotkey)
            {
                Hotkey = hotkey;
            }
        }

        public HotkeyManager(RecorderConfig config)
        {
            this.config = config;
            this.messageWindow = new HotkeyMessageWindow(this);
        }

        /// <summary>
        /// Register all hotkeys from config
        /// </summary>
        public void RegisterHotkeys()
        {
            Console.WriteLine($"Registering {config.Hotkeys.Count} hotkeys...");
            
            for (int i = 0; i < config.Hotkeys.Count; i++)
            {
                var hotkey = config.Hotkeys[i];
                RegisterHotkey(hotkey, i + 1); // Pass ID explicitly
            }
            
            Console.WriteLine($"✓ Registered hotkeys\n");
        }

        /// <summary>
        /// Register a single hotkey
        /// </summary>
        private void RegisterHotkey(HotkeyConfig hotkey, int id)
        {
            try
            {
                uint modifiers = ParseModifiers(hotkey.Modifiers);
                uint vkCode = ParseKey(hotkey.Key);

                bool success = RegisterHotKey(messageWindow.Handle, id, modifiers, vkCode);
                
                if (success)
                {
                    Console.WriteLine($"  ✓ {FormatHotkey(hotkey)} → {hotkey.ClipLengthSeconds}s");
                }
                else
                {
                    Console.WriteLine($"  ✗ Failed: {FormatHotkey(hotkey)} (key already in use by another app)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Error: {hotkey.Name} - {ex.Message}");
            }
        }

        /// <summary>
        /// Unregister all hotkeys
        /// </summary>
        public void UnregisterAll()
        {
            for (int i = 1; i <= config.Hotkeys.Count; i++)
            {
                UnregisterHotKey(messageWindow.Handle, i);
            }
            Console.WriteLine("✓ Unregistered all hotkeys");
        }

        /// <summary>
        /// Parse modifier keys from config
        /// </summary>
        private uint ParseModifiers(System.Collections.Generic.List<string> modifiers)
        {
            uint result = MOD_NONE;
            
            foreach (var mod in modifiers)
            {
                switch (mod.ToLower())
                {
                    case "control":
                    case "ctrl":
                        result |= MOD_CONTROL;
                        break;
                    case "alt":
                        result |= MOD_ALT;
                        break;
                    case "shift":
                        result |= MOD_SHIFT;
                        break;
                    case "win":
                    case "windows":
                        result |= MOD_WIN;
                        break;
                }
            }
            
            return result;
        }

        /// <summary>
        /// Parse key from config to virtual key code
        /// </summary>
        private uint ParseKey(string key)
        {
            // Function keys
            if (key.StartsWith("F") && int.TryParse(key.Substring(1), out int fNum))
            {
                return (uint)(Keys.F1 + fNum - 1);
            }

            // Special keys
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
                    // Single character keys
                    if (key.Length == 1)
                    {
                        char c = char.ToUpper(key[0]);
                        return (uint)c;
                    }
                    break;
            }

            throw new ArgumentException($"Unknown key: {key}");
        }

        /// <summary>
        /// Format hotkey for display
        /// </summary>
        private string FormatHotkey(HotkeyConfig hotkey)
        {
            string result = string.Join(" + ", hotkey.Modifiers);
            if (result.Length > 0) result += " + ";
            result += hotkey.Key;
            return result;
        }

        /// <summary>
        /// Handle hotkey press
        /// </summary>
        private void OnHotkeyPressed(int id)
        {
            // ID is 1 based, array is 0 based
            int index = id - 1;
            if (index >= 0 && index < config.Hotkeys.Count)
            {
                var hotkey = config.Hotkeys[index];
                Console.WriteLine($"\n🔥 HOTKEY: {FormatHotkey(hotkey)} ({hotkey.ClipLengthSeconds}s)");
                HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(hotkey));
            }
        }

        public void Dispose()
        {
            UnregisterAll();
            messageWindow.DestroyHandle();
        }

        /// <summary>
        /// Hidden window to receive hotkey messages
        /// </summary>
        private class HotkeyMessageWindow : NativeWindow
        {
            private const int WM_HOTKEY = 0x0312;
            private readonly HotkeyManager manager;

            public HotkeyMessageWindow(HotkeyManager manager)
            {
                this.manager = manager;
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    manager.OnHotkeyPressed((int)m.WParam);
                }
                base.WndProc(ref m);
            }
        }
    }
}