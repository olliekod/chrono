using System;
using System.Windows.Forms;

namespace ChronoRecorder
{
    class Program
    {
        private static Recorder? recorder;
        private static HotkeyManager? hotkeyManager;

        [STAThread]
        static void Main(string[] args)
        {
            // Launch UI
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            Console.WriteLine("=== Chrono Clip Recorder ===");
            Console.WriteLine($"Started at: {DateTime.Now}\n");
            
            // Load configuration
            var config = ConfigManager.Load();
            Console.WriteLine($"✓ Configuration loaded\n");

            // Create recorder
            recorder = new Recorder(config);
            recorder.StartMonitoring();

            // Create hotkey manager
            hotkeyManager = new HotkeyManager(config);
            hotkeyManager.HotkeyPressed += OnHotkeyPressed;
            hotkeyManager.RegisterHotkeys();
            Console.WriteLine();
            
            var mainWindow = new WebViewHost(config);
            mainWindow.SetRecorder(recorder);
            
            Application.Run(mainWindow);
            
            // Cleanup
            hotkeyManager.Dispose();
            recorder.StopMonitoring();
            recorder.StopRecording();
        }

        private static void OnHotkeyPressed(object? sender, HotkeyManager.HotkeyPressedEventArgs e)
        {
            try
            {
                if (recorder == null || !recorder.IsRecordingActive)
                {
                    Console.WriteLine("⚠ Cannot save clip - recorder is not active");
                    MessageBox.Show("Recorder is not active. Start recording first!", 
                        "Chrono", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Console.WriteLine($"Saving last {e.Hotkey.ClipLengthSeconds} seconds...");
                
                string clipPath = recorder.SaveClip(e.Hotkey.ClipLengthSeconds, e.Hotkey.Name);
                
                Console.WriteLine($"✓ Clip saved: {clipPath}");
                MessageBox.Show($"Clip saved!\n\n{clipPath}", 
                    "Chrono", MessageBoxButtons.OK, MessageBoxIcon.Information);
                
                // TODO: Open trim UI here
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error saving clip: {ex.Message}");
                MessageBox.Show($"Failed to save clip:\n{ex.Message}", 
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}