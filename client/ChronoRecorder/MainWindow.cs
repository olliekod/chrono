using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChronoRecorder
{
    /// <summary>
    /// Chrono's window: a WebView2 showing the page in UI/. It is created when someone opens Chrono from the tray and
    /// disposed when closed (see <see cref="TrayApp"/>), so it costs nothing while you play.
    /// Three private web addresses are mapped onto folders, so the page can load its own files, the clip pictures and
    /// the videos with normal https requests (and the video player gets seeking for free):
    ///   app.chrono → the UI folder, thumbs.chrono → picture cache, clips.chrono → the clips folder.
    /// </summary>
    public sealed class MainWindow : Form, IUiHost
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const string AppHost = "app.chrono";
        private const string StartUrl = "https://" + AppHost + "/index.html";

        private readonly RecorderConfig config;
        private readonly ClipLibrary library;
        private readonly ClipMedia media;
        private readonly UiBridge bridge;
        private readonly WebView2 webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(49, 51, 56) };
        private readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly System.Windows.Forms.Timer libraryTimer = new System.Windows.Forms.Timer { Interval = 150 };
        private string lastStatus = "";
        private bool ready;

        public MainWindow(RecorderConfig config, IRecorder recorder, ClipLibrary library, ClipMedia media, Uploader uploader, Action onConfigSaved)
        {
            this.config = config;
            this.library = library;
            this.media = media;
            bridge = new UiBridge(config, recorder, library, media, uploader, this, onConfigSaved);

            Text = "Chrono";
            Icon = TrayIcons.For(TrayState.Idle);
            BackColor = Color.FromArgb(49, 51, 56);   // matches the page, so opening doesn't flash white
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1240, 780);
            MinimumSize = new Size(980, 640);
            Controls.Add(webView);

            statusTimer.Tick += (s, e) => PushStatusIfChanged();
            libraryTimer.Tick += (s, e) => { libraryTimer.Stop(); if (ready) bridge.PushEvent("libraryChanged"); };
            library.Changed += OnLibraryChanged;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // A dark title bar, so the frame matches the app (attribute 20; 19 on early Windows 10 builds).
            int on = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { await InitializeBrowserAsync(); }
            catch (Exception ex)
            {
                Console.WriteLine($"The window couldn't start: {ex}");
                MessageBox.Show($"Chrono's window couldn't start.\n\n{ex.Message}\n\nMake sure the Microsoft Edge WebView2 runtime is installed.",
                    "Chrono", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private async System.Threading.Tasks.Task InitializeBrowserAsync()
        {
            await webView.EnsureCoreWebView2Async(await WebView2Runtime.GetEnvironmentAsync());
            var core = webView.CoreWebView2;

            Directory.CreateDirectory(config.OutputFolder);
            string ui = Path.Combine(AppContext.BaseDirectory, "UI");
            core.SetVirtualHostNameToFolderMapping(AppHost, ui, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("thumbs.chrono", media.ThumbsFolder, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("clips.chrono", library.ClipsFolder, CoreWebView2HostResourceAccessKind.Allow);

            bool debug = Environment.GetEnvironmentVariable("CHRONO_DEBUG") == "1";
            core.Settings.AreDevToolsEnabled = debug;
            core.Settings.AreDefaultContextMenusEnabled = debug;
            core.Settings.AreBrowserAcceleratorKeysEnabled = debug;   // no reload, print or zoom shortcuts in the app
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;

            // The page changes with app updates; never show yesterday's copy from the browser cache.
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");

            // The window only ever shows Chrono's own page. Anything else opens in the normal browser.
            core.NavigationStarting += (s, e) =>
            {
                if (!e.Uri.StartsWith("https://" + AppHost + "/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                OpenInBrowser(e.Uri);
            };

            core.WebMessageReceived += async (s, e) =>
            {
                string? reply = await bridge.HandleAsync(e.WebMessageAsJson);
                if (reply != null && !IsDisposed && webView.CoreWebView2 != null) webView.CoreWebView2.PostWebMessageAsJson(reply);
            };

            core.NavigationCompleted += (s, e) =>
            {
                ready = true;
                statusTimer.Start();
            };

            core.Navigate(StartUrl);
        }

        // ------------------------------------------------------------ pushing to the page

        private void OnLibraryChanged()
        {
            // Changes come in bursts (a clip is added, then its details are read); tell the page once.
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() => { libraryTimer.Stop(); libraryTimer.Start(); }));
        }

        private void PushStatusIfChanged()
        {
            if (!ready || IsDisposed) return;
            string now = Newtonsoft.Json.JsonConvert.SerializeObject(bridge.Status());
            if (now == lastStatus) return;
            lastStatus = now;
            bridge.PushStatus();
        }

        /// <summary>A short message in the corner of the window (from something that happened outside it, like a hotkey).</summary>
        public void ShowToast(string kind, string title, string? text = null)
        {
            if (!ready || IsDisposed) return;
            bridge.PushEvent("toast", new { kind, title, text });
        }

        // -------------------------------------------------------------------- IUiHost

        public void Post(string json)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => Post(json))); return; }
            webView.CoreWebView2?.PostWebMessageAsJson(json);
        }

        public void CopyToClipboard(string text)
        {
            if (InvokeRequired) { Invoke(new Action(() => CopyToClipboard(text))); return; }
            try { Clipboard.SetDataObject(text, true, 5, 100); }   // another app can hold the clipboard for a moment
            catch (Exception ex) { Console.WriteLine($"Couldn't copy to the clipboard: {ex.Message}"); }
        }

        public void ShowInFolder(string path)
        {
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
            catch (Exception ex) { Console.WriteLine($"Couldn't open the folder: {ex.Message}"); }
        }

        public void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); Process.Start("explorer.exe", $"\"{path}\""); }
            catch (Exception ex) { Console.WriteLine($"Couldn't open the folder: {ex.Message}"); }
        }

        private static void OpenInBrowser(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || (parsed.Scheme != "https" && parsed.Scheme != "http")) return;
            try { Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true }); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                library.Changed -= OnLibraryChanged;   // the library outlives the window
                statusTimer.Dispose();
                libraryTimer.Dispose();
                webView.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
