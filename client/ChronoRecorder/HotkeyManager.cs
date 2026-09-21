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

        private readonly NativeWindow messageWindow;
        private readonly RecorderConfig config;

        // id -> hotkey as registered. The config is edited in place when settings are saved, so a pressed
        // hotkey is looked up here instead of by index into config.Hotkeys.
        private readonly Dictionary<int, HotkeyConfig> registered = new Dictionary<int, HotkeyConfig>();

        /// <summary>Hotkeys from the last registration that Windows refused (another program already owns those keys).</summary>
        public IReadOnlyList<string> Failures => failures;
        private readonly List<string> failures = new List<string>();

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
            failures.Clear();
            var hotkeys = config.Hotkeys ?? new List<HotkeyConfig>();
            Console.WriteLine($"Registering {hotkeys.Count} hotkeys...");

            for (int i = 0; i < hotkeys.Count; i++)
            {
                RegisterHotkey(hotkeys[i], i + 1); // Pass ID explicitly
            }

            Console.WriteLine($"✓ Registered hotkeys\n");
        }

        /// <summary>
        /// Re-read the hotkeys from config (after the settings were saved).
        /// </summary>
        public IReadOnlyList<string> ReloadHotkeys()
        {
            UnregisterAll();
            RegisterHotkeys();
            return Failures;
        }

        /// <summary>
        /// Register a single hotkey
        /// </summary>
        private void RegisterHotkey(HotkeyConfig hotkey, int id)
        {
            try
            {
                uint modifiers = HotkeyParser.ParseModifiers(hotkey.Modifiers);
                uint vkCode = HotkeyParser.ParseKey(hotkey.Key);

                bool success = RegisterHotKey(messageWindow.Handle, id, modifiers | HotkeyParser.ModNoRepeat, vkCode);

                if (success)
                {
                    registered[id] = hotkey;
                    Console.WriteLine($"  ✓ {FormatHotkey(hotkey)} → {hotkey.ClipLengthSeconds}s");
                }
                else
                {
                    Console.WriteLine($"  ✗ Failed: {FormatHotkey(hotkey)} (key already in use by another app)");
                    failures.Add($"{hotkey.Name} ({FormatHotkey(hotkey)})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Error: {hotkey.Name} - {ex.Message}");
                failures.Add($"{hotkey.Name} ({FormatHotkey(hotkey)})");
            }
        }

        /// <summary>
        /// Unregister all hotkeys
        /// </summary>
        public void UnregisterAll()
        {
            // Unregister what was actually registered; config.Hotkeys may have changed since.
            foreach (int id in registered.Keys)
            {
                UnregisterHotKey(messageWindow.Handle, id);
            }
            registered.Clear();
            Console.WriteLine("✓ Unregistered all hotkeys");
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
            if (registered.TryGetValue(id, out var hotkey))
            {
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