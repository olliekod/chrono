using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace ChronoRecorder
{
    /// <summary>
    /// Chrono lives in the tray. It owns the tray icon, and creates the window only when someone asks for it and
    /// frees it (with the whole WebView2 browser behind it, tens of MB and several processes) when it is closed, so an
    /// idle Chrono costs almost nothing. There is no overlay: what is being recorded is shown on the tray icon (colour
    /// and tooltip), in its menu, and in the window.
    /// </summary>
    public sealed class TrayApp : ApplicationContext
    {
        private readonly RecorderConfig config;
        private readonly Recorder recorder;
        private readonly HotkeyManager hotkeys;
        private readonly ClipLibrary library;
        private readonly ClipMedia media;
        private readonly Uploader uploader;
        private readonly NotifyIcon icon;
        private readonly Control marshal;   // an invisible control, so other threads can hand work to the UI thread
        private readonly System.Windows.Forms.Timer statusTimer;
        private readonly ToolStripMenuItem statusItem = new ToolStripMenuItem { Enabled = false };
        private readonly ToolStripMenuItem pauseItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };

        private MainWindow? window;
        private TrayState shownState = (TrayState)(-1);
        private string shownText = "";

        public NotifyIcon Icon => icon;

        public TrayApp(RecorderConfig config, Recorder recorder, HotkeyManager hotkeys, ClipLibrary library, ClipMedia media, Uploader uploader)
        {
            this.config = config;
            this.recorder = recorder;
            this.hotkeys = hotkeys;
            this.library = library;
            this.media = media;
            this.uploader = uploader;

            marshal = new Control();
            marshal.CreateControl();
            _ = marshal.Handle;   // force the handle now, on the UI thread

            icon = new NotifyIcon { Icon = TrayIcons.For(TrayState.Idle), Text = TrayStatus.Tooltip("starting"), Visible = true };
            icon.ContextMenuStrip = BuildMenu();
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            icon.BalloonTipClicked += (s, e) => ShowWindow();   // "Clip saved" opens the library

            statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            statusTimer.Tick += (s, e) => RefreshStatus();
            statusTimer.Start();
            RefreshStatus();
        }

        // ------------------------------------------------------------------ the window

        /// <summary>Safe to call from any thread.</summary>
        public void RequestShow() => Post(ShowWindow);

        public void Post(Action action)
        {
            if (marshal.IsDisposed) return;
            if (marshal.InvokeRequired) marshal.BeginInvoke(action);
            else action();
        }

        public void ShowWindow()
        {
            if (window != null && !window.IsDisposed)
            {
                if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
                window.Show();
                window.Activate();
                window.BringToFront();
                return;
            }

            Console.WriteLine("Opening the window");
            var opened = new MainWindow(config, recorder, library, media, uploader, OnConfigSaved);
            opened.FormClosed += (s, e) =>
            {
                Console.WriteLine("Window closed; freeing its browser");
                window = null;
            };
            window = opened;
            opened.Show();
            opened.Activate();
        }

        /// <summary>A message inside the window, if it is open (a clip was saved by a hotkey, for example).</summary>
        public void ShowToast(string kind, string title, string? text = null) => Post(() => window?.ShowToast(kind, title, text));

        private void OnConfigSaved()
        {
            hotkeys.ReloadHotkeys();
            ApplyStartWithWindows();
            RefreshStatus();
        }

        // -------------------------------------------------------------------- the tray

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var open = new ToolStripMenuItem("Open Chrono") { Font = new System.Drawing.Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, System.Drawing.FontStyle.Bold) };
            open.Click += (s, e) => ShowWindow();

            pauseItem.Click += (s, e) =>
            {
                recorder.SetRecorderEnabled(!config.RecorderEnabled);
                RefreshStatus();
            };

            var folder = new ToolStripMenuItem("Open clips folder");
            folder.Click += (s, e) =>
            {
                try { Directory.CreateDirectory(config.OutputFolder); Process.Start("explorer.exe", config.OutputFolder); }
                catch (Exception ex) { Console.WriteLine($"Couldn't open the clips folder: {ex.Message}"); }
            };

            startupItem.Click += (s, e) =>
            {
                config.StartWithWindows = !config.StartWithWindows;
                ConfigManager.Save(config);
                ApplyStartWithWindows();
            };

            var exit = new ToolStripMenuItem("Exit Chrono");
            exit.Click += (s, e) => ExitThread();

            menu.Items.AddRange(new ToolStripItem[]
            {
                statusItem, new ToolStripSeparator(), open, pauseItem, folder, new ToolStripSeparator(), startupItem, new ToolStripSeparator(), exit
            });
            menu.Opening += (s, e) =>
            {
                startupItem.Checked = config.StartWithWindows;
                RefreshStatus();
            };
            return menu;
        }

        /// <summary>Make the Windows startup entry match the setting. A development copy is never registered.</summary>
        public void ApplyStartWithWindows()
        {
            bool ok = AutoStart.Set(config.StartWithWindows, Environment.ProcessPath);
            startupItem.Checked = config.StartWithWindows;
            if (!ok && config.StartWithWindows) Console.WriteLine("Start with Windows skipped: this copy of Chrono isn't installed.");
        }

        /// <summary>Update the icon, tooltip and menu to say what is being recorded. Cheap; runs every second.</summary>
        private void RefreshStatus()
        {
            var (state, text) = TrayStatus.Describe(
                config.RecorderEnabled, recorder.IsRecordingActive, config.Mode, recorder.TargetName, config.SelectedApplication);

            pauseItem.Text = config.RecorderEnabled ? "Pause recording" : "Resume recording";

            if (state == shownState && text == shownText) return;
            shownState = state;
            shownText = text;

            icon.Icon = TrayIcons.For(state);
            icon.Text = TrayStatus.Tooltip(text);
            statusItem.Text = text;
        }

        protected override void ExitThreadCore()
        {
            statusTimer.Stop();
            window?.Close();
            // Hide first, or Windows leaves a ghost icon in the tray until the mouse passes over it.
            icon.Visible = false;
            icon.Dispose();
            marshal.Dispose();
            base.ExitThreadCore();
        }
    }
}
