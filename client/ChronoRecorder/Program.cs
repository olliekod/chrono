using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChronoRecorder
{
    class Program
    {
        private static RecorderConfig config = null!;
        private static Recorder? recorder;
        private static HotkeyManager? hotkeyManager;
        private static Notifier? notifier;
        private static Form? uiForm;
        private static readonly Uploader uploader = new Uploader(Uploader.CreateHttpClient());

        [STAThread]
        static void Main(string[] args)
        {
            // Launch UI
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Console.WriteLine("=== Chrono Clip Recorder ===");
            Console.WriteLine($"Started at: {DateTime.Now}\n");

            // Load configuration
            config = ConfigManager.Load();
            Console.WriteLine($"✓ Configuration loaded\n");

            notifier = new Notifier(config);

            // Create recorder
            recorder = new Recorder(config);
            recorder.StartMonitoring();

            // Create hotkey manager
            hotkeyManager = new HotkeyManager(config);
            hotkeyManager.HotkeyPressed += OnHotkeyPressed;
            hotkeyManager.RegisterHotkeys();
            Console.WriteLine();

            var mainWindow = new WebViewHost(config);
            uiForm = mainWindow;
            mainWindow.SetRecorder(recorder);

            // FFmpeg failures arrive on a worker thread; the tray icon belongs to the UI thread.
            recorder.RecordingFailed += message => OnUiThread(() => notifier?.Error("Recording problem", message));
            recorder.Warning += message => OnUiThread(() => notifier?.Error("Chrono", message));
            // Settings edit the shared config in place; re-register so new hotkeys work without a restart.
            mainWindow.ConfigSaved += () => hotkeyManager.ReloadHotkeys();

            Application.Run(mainWindow);

            // Cleanup
            hotkeyManager.Dispose();
            recorder.StopMonitoring();
            recorder.StopRecording();
            notifier.Dispose();
        }

        private static void OnUiThread(Action action)
        {
            if (uiForm != null && uiForm.IsHandleCreated && uiForm.InvokeRequired) uiForm.BeginInvoke(action);
            else action();
        }

        // async void because this is an event handler. It runs on the UI thread, so every await resumes there,
        // which is what lets it touch the clipboard and tray icon. Nothing may escape the try/catch.
        private static async void OnHotkeyPressed(object? sender, HotkeyManager.HotkeyPressedEventArgs e)
        {
            try
            {
                if (recorder == null || !recorder.IsRecordingActive)
                {
                    notifier?.Error("Chrono", "Not recording. Start the recorder first.");
                    return;
                }

                Console.WriteLine($"Saving last {e.Hotkey.ClipLengthSeconds} seconds...");

                // Joining segments spawns FFmpeg; keep the UI responsive while it runs.
                string clipPath = await Task.Run(() => recorder.SaveClip(e.Hotkey.ClipLengthSeconds, e.Hotkey.Name));
                Console.WriteLine($"✓ Clip saved: {clipPath}");

                // TODO: Open trim UI here

                await ShareAsync(clipPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error saving clip: {ex}");
                notifier?.Error("Couldn't save clip", ex.Message);
            }
        }

        /// <summary>Upload a saved clip and put its share link on the clipboard.</summary>
        private static async Task ShareAsync(string clipPath)
        {
            string name = Path.GetFileName(clipPath);

            if (!config.AutoUpload)
            {
                notifier?.Info("Clip saved", name);
                return;
            }

            var settings = UploadRules.SettingsFrom(config);
            if (settings == null)
            {
                notifier?.Info("Clip saved", $"{name}\nAdd your server address and upload key in Settings to get share links.");
                return;
            }

            try
            {
                notifier?.Info("Uploading clip...", name);

                // The clip may have been shrunk as it was saved, so read its real size rather than assuming.
                double? duration = await Task.Run(() => MediaProbe.DurationSeconds(clipPath));
                string? videoSize = await Task.Run(() => MediaProbe.VideoSizeText(clipPath)) ?? recorder?.CaptureResolution;
                var info = new ClipInfo(duration, videoSize, config.Fps, BitrateSizing.Resolve(config.Bitrate, videoSize, config.Fps));

                var result = await uploader.UploadAsync(clipPath, settings, info);
                Console.WriteLine($"✓ Uploaded: {result.Link}");

                if (config.CopyLinkToClipboard)
                {
                    // Another app can hold the clipboard for a moment; retry a few times.
                    Clipboard.SetDataObject(result.Link, true, 5, 100);
                    notifier?.Info("Link copied", result.Link);
                }
                else
                {
                    notifier?.Info("Clip uploaded", result.Link);
                }
            }
            catch (UploadException ex)
            {
                Console.WriteLine($"✗ Upload failed: {ex.Message}");
                notifier?.Error("Upload failed", $"{ex.Message}\nYour clip is saved: {name}");
            }
        }
    }
}
