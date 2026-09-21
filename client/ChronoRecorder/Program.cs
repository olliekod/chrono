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
        private static TrayApp? tray;
        private static ClipLibrary? library;

        [STAThread]
        static void Main(string[] args)
        {
            // The installed app has no console window; keep a log file so problems on someone's PC can be read.
            FileLog.Attach();

            // One Chrono at a time. Launching it again just brings the running one's window forward.
            using var instance = new SingleInstance();
            if (!instance.IsFirst)
            {
                instance.AskFirstToShow();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Console.WriteLine("=== Chrono Clip Recorder ===");
            Console.WriteLine($"Started at: {DateTime.Now}");
            Console.WriteLine(FfmpegLocator.IsBundled ? $"✓ Using the FFmpeg that came with Chrono: {FfmpegLocator.Path}" : "Using FFmpeg from the PATH");

            if (!WebView2Runtime.EnsureInstalled()) return;

            config = ConfigManager.Load();
            Console.WriteLine("✓ Configuration loaded");

            recorder = new Recorder(config);
            recorder.StartMonitoring();

            hotkeyManager = new HotkeyManager(config);
            hotkeyManager.HotkeyPressed += OnHotkeyPressed;
            hotkeyManager.RegisterHotkeys();
            Console.WriteLine();

            library = new ClipLibrary(config);
            var media = new ClipMedia(config, library, () => recorder.EncoderName);

            // Chrono lives in the tray; the window is opened on demand and freed when closed.
            tray = new TrayApp(config, recorder, hotkeyManager, library, media, new Uploader(Uploader.CreateHttpClient()));
            notifier = new Notifier(config, tray.Icon);
            instance.ListenForShowRequests(tray.RequestShow);

            // FFmpeg failures arrive on a worker thread; the tray icon belongs to the UI thread.
            recorder.RecordingFailed += message => OnUiThread(() => notifier?.Error("Recording problem", message));
            recorder.Warning += message => OnUiThread(() => notifier?.Error("Chrono", message));

            tray.ApplyStartWithWindows();

            // Clips saved before the library existed are picked up, and their lengths read, without holding anything up.
            Task.Run(() =>
            {
                try { library.Refresh(); library.FillMissingDetails(); }
                catch (Exception ex) { Console.WriteLine($"Couldn't read the clips folder: {ex.Message}"); }
            });

            bool startedInBackground = Array.Exists(args, a => a == AutoStart.BackgroundFlag);
            if (!config.FirstRunCompleted)
            {
                // The very first run opens the window so there is something to see and set up.
                config.FirstRunCompleted = true;
                ConfigManager.Save(config);
                tray.ShowWindow();
            }
            else if (!startedInBackground)
            {
                // Launched by hand: nothing appears, so say where Chrono went.
                notifier.Info("Chrono is running in the tray", "Click its icon to open Chrono. It records games automatically.");
            }

            Application.Run(tray);

            // Cleanup
            hotkeyManager.Dispose();
            recorder.StopMonitoring();
            recorder.StopRecording();
            Console.WriteLine("Chrono exited");
        }

        private static void OnUiThread(Action action)
        {
            if (tray != null) tray.Post(action);
            else action();
        }

        // async void because this is an event handler. It runs on the UI thread, so every await resumes there,
        // which is what lets it touch the tray icon. Nothing may escape the try/catch.
        private static async void OnHotkeyPressed(object? sender, HotkeyManager.HotkeyPressedEventArgs e)
        {
            try
            {
                if (recorder == null || library == null || !recorder.IsRecordingActive)
                {
                    notifier?.Error("Chrono", "Nothing is being recorded right now. Chrono records games automatically once one is running.");
                    return;
                }

                Console.WriteLine($"Saving last {e.Hotkey.ClipLengthSeconds} seconds...");
                string? game = recorder.CurrentGameName;

                // Joining segments spawns FFmpeg; keep the UI responsive while it runs.
                string clipPath = await Task.Run(() => recorder.SaveClip(e.Hotkey.ClipLengthSeconds, e.Hotkey.Name));
                Console.WriteLine($"✓ Clip saved: {clipPath}");

                // The clip goes into the library and stays there. Uploading is something you choose to do, from the library.
                var clip = library.AddSaved(clipPath, game, LibraryIndex.DefaultTitle(game, e.Hotkey.Name, DateTime.Now));
                _ = Task.Run(() => library.FillMissingDetails());

                notifier?.Info("Clip saved", $"{clip.Title}\nClick to open it in your library.");
                tray?.ShowToast("good", "Clip saved", clip.Title);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error saving clip: {ex}");
                notifier?.Error("Couldn't save clip", ex.Message);
            }
        }
    }
}
