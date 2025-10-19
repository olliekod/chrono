using System;
using System.IO;
using System.Windows.Forms;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChronoRecorder
{
    /// <summary>
    /// Hosts the WebView2 control and manages UI communication
    /// </summary>
    public class WebViewHost : Form
    {
        private WebView2 webView;
        private RecorderConfig config;
        private System.Windows.Forms.Timer? statusUpdateTimer;

        public WebViewHost(RecorderConfig config)
        {
            this.config = config;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // Window setup
            this.Text = "Chrono - Clip Recorder";
            this.Width = 600;
            this.Height = 700;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;

            // WebView2 setup
            webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            this.Controls.Add(webView);

            // Initialize WebView2
            InitializeAsync();

            
        }

        private async void InitializeAsync()
        {
            try
            {
                // Initialize WebView2 environment
                await webView.EnsureCoreWebView2Async(null);

                // Enable dev tools for debugging
                webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

                // Handle messages from JavaScript
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // Load the UI
                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UI", "index.html");

                if (File.Exists(htmlPath))
                {
                    webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                    Console.WriteLine($"✓ Loaded UI from {htmlPath}");
                }
                else
                {
                    // Fallback: Load from embedded HTML
                    Console.WriteLine($"⚠ UI file not found at {htmlPath}, using inline HTML");
                    LoadInlineHTML();
                }

                // Send initial status to UI
                await System.Threading.Tasks.Task.Delay(500); // Wait for page to load
                SendStatusUpdate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize WebView2: {ex.Message}\n\nMake sure Edge WebView2 Runtime is installed.",
                    "Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                var message = JObject.Parse(json);
                string action = message["action"]?.ToString();

                Console.WriteLine($"Received message: {action}");

                switch (action)
                {
                    case "getRunningApps":
                        SendRunningAppsList();
                        break;

                    case "getConfig":
                        SendConfig();
                        break;

                    case "setMode":
                        string mode = message["mode"]?.ToString();
                        SetRecordingMode(mode);
                        break;

                    case "startRecording":
                        string app = message["app"]?.ToString();
                        StartRecordingWithApp(app);
                        break;

                    case "stopRecording":
                        StopRecordingManually();
                        break;

                    case "openSettings":
                        OpenSettingsWindow();
                        break;

                    case "openClipsFolder":
                        OpenClipsFolder();
                        break;

                    default:
                        Console.WriteLine($"Unknown action: {action}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling message: {ex.Message}");
            }
        }

        private void StartStatusPolling()
        {
            // Poll recorder status every 500ms and update UI
            statusUpdateTimer = new System.Windows.Forms.Timer();
            statusUpdateTimer.Interval = 500; // 500ms
            statusUpdateTimer.Tick += (s, e) =>
            {
                if (recorder != null && config.RecorderEnabled)
                {
                    SendStatusUpdate(WindowDetector.GetActiveApplicationName(), recorder.IsRecordingActive);
                }
            };
            statusUpdateTimer.Start();
            Console.WriteLine("✓ Status polling started");
        }

        private void SendStatusUpdate()
        {
            var status = new
            {
                action = "updateStatus",
                status = "Active",
                buffer = "0:00 / 5:00",
                username = config.Username,
                quality = $"{config.Resolution.Split('x')[1]}p{config.Fps}"
            };

            string json = JsonConvert.SerializeObject(status);
            webView.CoreWebView2.PostWebMessageAsJson(json);
        }

        private void SendHotkeysUpdate()
        {
            var hotkeys = config.Hotkeys.Select(h => new
            {
                name = h.Name,
                key = FormatHotkey(h),
                length = h.ClipLengthSeconds
            }).ToArray();

            var message = new
            {
                action = "updateHotkeys",
                hotkeys = hotkeys
            };

            string json = JsonConvert.SerializeObject(message);
            webView.CoreWebView2.PostWebMessageAsJson(json);
        }

        private string FormatHotkey(HotkeyConfig hotkey)
        {
            string result = string.Join(" + ", hotkey.Modifiers);
            if (result.Length > 0) result += " + ";
            result += hotkey.Key;
            return result;
        }

        private void OpenSettingsWindow()
        {
            // Must run on UI thread
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(OpenSettingsWindow));
                return;
            }

            try
            {
                Console.WriteLine("Opening settings window...");
                
                // Create settings window
                var settingsForm = new Form
                {
                    Text = "Chrono Settings",
                    Width = 800,
                    Height = 700,
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = true
                };

                var settingsWebView = new WebView2
                {
                    Dock = DockStyle.Fill
                };

                settingsForm.Controls.Add(settingsWebView);
                
                Console.WriteLine("Settings form created");

                // Show form first
                settingsForm.Show();
                
                // Then initialize WebView2 asynchronously
                var _ = InitializeSettingsWebView(settingsWebView, settingsForm);
                
                Console.WriteLine("Settings window shown");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Settings error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                MessageBox.Show($"Could not open settings: {ex.Message}", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task InitializeSettingsWebView(WebView2 settingsWebView, Form settingsForm)
        {
            try
            {
                Console.WriteLine("Initializing settings WebView2...");
                
                await settingsWebView.EnsureCoreWebView2Async(null);
                
                Console.WriteLine("Settings WebView2 initialized");
                
                // Add navigation handlers
                settingsWebView.CoreWebView2.NavigationStarting += (s, e) =>
                {
                    Console.WriteLine($"Navigation starting: {e.Uri}");
                };
                
                settingsWebView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    Console.WriteLine($"Navigation completed. Success: {e.IsSuccess}");
                    if (!e.IsSuccess)
                    {
                        Console.WriteLine($"Navigation failed: {e.WebErrorStatus}");
                    }
                };
                
                // Handle messages from settings page
                settingsWebView.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    Console.WriteLine($"Settings message: {e.WebMessageAsJson}");
                    HandleSettingsMessage(e.WebMessageAsJson, settingsWebView, settingsForm);
                };

                // Load settings page
                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UI", "settings.html");
                Console.WriteLine($"Loading settings from: {htmlPath}");
                
                if (File.Exists(htmlPath))
                {
                    Console.WriteLine("✓ Settings file exists");
                    settingsWebView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    Console.WriteLine("✗ Settings file NOT FOUND!");
                    MessageBox.Show($"Settings file not found:\n{htmlPath}", "Error");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error initializing settings WebView2: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private void HandleSettingsMessage(string json, WebView2 settingsWebView, Form settingsForm)
        {
            try
            {
                var message = JObject.Parse(json);
                string action = message["action"]?.ToString();

                if (action == "loadConfig")
                {
                    // Send current config to settings page
                    var response = new { action = "configLoaded", config };
                    string responseJson = JsonConvert.SerializeObject(response);
                    settingsWebView.CoreWebView2.PostWebMessageAsJson(responseJson);
                }
                else if (action == "saveConfig")
                {
                    // Update config from settings
                    var newConfig = message["config"]?.ToObject<RecorderConfig>();
                    if (newConfig != null)
                    {
                        // Update config
                        config.Resolution = newConfig.Resolution;
                        config.Fps = newConfig.Fps;
                        config.Bitrate = newConfig.Bitrate;
                        config.Encoder = newConfig.Encoder;
                        config.BufferDurationSeconds = newConfig.BufferDurationSeconds;
                        config.Username = newConfig.Username;
                        config.ApiUrl = newConfig.ApiUrl;
                        config.Hotkeys = newConfig.Hotkeys;

                        // Save to disk
                        ConfigManager.Save(config);

                        // Notify settings page
                        var response = new { action = "configSaved" };
                        string responseJson = JsonConvert.SerializeObject(response);
                        settingsWebView.CoreWebView2.PostWebMessageAsJson(responseJson);

                        // Close settings window
                        settingsForm.Close();

                        MessageBox.Show("Settings saved! Restart Chrono for hotkey changes to take effect.", 
                            "Settings Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling settings message: {ex.Message}");
            }
        }

        private void OpenClipsFolder()
        {
            try
            {
                Directory.CreateDirectory(config.OutputFolder);
                System.Diagnostics.Process.Start("explorer.exe", config.OutputFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void TestCapture(int length)
        {
            MessageBox.Show($"Test capture triggered!\nWould save last {length} seconds.\n\nRecorder not yet implemented.",
                "Test Capture", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void LoadInlineHTML()
        {
            string html = @"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Chrono</title>
    <style>
        body { 
            margin: 0; 
            padding: 40px; 
            font-family: 'Segoe UI', sans-serif; 
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
        }
        h1 { text-align: center; }
        .message { 
            text-align: center; 
            background: rgba(255,255,255,0.1); 
            padding: 20px; 
            border-radius: 10px; 
        }
    </style>
</head>
<body>
    <h1>⏱️ Chrono</h1>
    <div class='message'>
        <p>WebView2 initialized successfully!</p>
        <p>UI files should be placed in the UI/ folder.</p>
    </div>
</body>
</html>";

            webView.CoreWebView2.NavigateToString(html);
        }
        private Recorder? recorder;

        public void SetRecorder(Recorder rec)
        {
            Console.WriteLine("WebViewHost: Setting recorder...");
            this.recorder = rec;
            
            // Listen to recording status changes
            rec.ApplicationChanged += (s, app) =>
            {
                Console.WriteLine($"WebViewHost: Received app change event: {app}");
                SendStatusUpdate(app, rec.IsRecordingActive);
            };
            
            // Start polling status
            StartStatusPolling();
            
            Console.WriteLine("WebViewHost: Recorder set successfully");
        }

        private void SendRunningAppsList()
        {
            // Must run on UI thread to access WebView
            if (webView.InvokeRequired)
            {
                webView.Invoke(new Action(SendRunningAppsList));
                return;
            }

            try
            {
                Console.WriteLine("Sending apps list to UI...");
                var apps = Recorder.GetRunningApplications();
                Console.WriteLine($"Apps to send: {string.Join(", ", apps)}");
                
                var message = new
                {
                    action = "runningApps",
                    apps = apps
                };
                string json = JsonConvert.SerializeObject(message);
                Console.WriteLine($"JSON: {json}");
                
                webView.CoreWebView2.PostWebMessageAsJson(json);
                Console.WriteLine("✓ Apps list sent to UI");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error sending apps list: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private void SendConfig()
        {
            // Must run on UI thread
            if (webView.InvokeRequired)
            {
                webView.Invoke(new Action(SendConfig));
                return;
            }

            try
            {
                var message = new
                {
                    action = "config",
                    mode = config.Mode.ToString(),
                    selectedApp = config.SelectedApplication,
                    enabled = config.RecorderEnabled,
                    hotkeys = config.Hotkeys.Select(h => new {
                        Name = h.Name,
                        Key = h.Key,
                        Modifiers = h.Modifiers,
                        ClipLengthSeconds = h.ClipLengthSeconds
                    }).ToArray()
                };
                string json = JsonConvert.SerializeObject(message);
                webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error sending config: {ex.Message}");
            }
        }

        private void SetRecordingMode(string modeStr)
        {
            if (Enum.TryParse<RecorderConfig.RecordingMode>(modeStr, out var mode))
            {
                recorder?.SetRecordingMode(mode);
            }
        }

        private void StartRecordingWithApp(string app)
        {
            Console.WriteLine($"\n>>> StartRecordingWithApp called with: '{app}'");
            Console.WriteLine($"    Current mode: {config.Mode}");
            
            if (recorder == null)
            {
                Console.WriteLine("    ERROR: Recorder is null!");
                return;
            }

            // For Display mode, start recording immediately
            if (config.Mode == RecorderConfig.RecordingMode.Display)
            {
                Console.WriteLine("    Mode is Display - starting recording immediately");
                config.SelectedApplication = "display";
                recorder.SetTrackedApplication("display");
                recorder.SetRecorderEnabled(true);
                
                // Force start recording immediately
                recorder.ForceStartRecording();
                
                // Update UI to show recording
                System.Threading.Thread.Sleep(200);
                SendStatusUpdate("display", true);
            }
            else
            {
                Console.WriteLine($"    Mode is {config.Mode} - setting to '{app}'");
                config.SelectedApplication = app;
                recorder.SetTrackedApplication(app);
                recorder.SetRecorderEnabled(true);
                
                System.Threading.Thread.Sleep(100);
                SendStatusUpdate(config.SelectedApplication, recorder.IsRecordingActive);
            }
            
            Console.WriteLine(">>> StartRecordingWithApp complete\n");
        }

        private void StopRecordingManually()
        {
            recorder?.SetRecorderEnabled(false);
            
            var message = new
            {
                action = "statusUpdate",
                type = "stopped",
                main = "RECORDER OFF",
                detail = "Recording stopped"
            };
            string json = JsonConvert.SerializeObject(message);
            webView.CoreWebView2.PostWebMessageAsJson(json);
        }

        private void SendStatusUpdate(string app, bool isRecording)
        {
            // Must run on UI thread to access WebView2
            if (webView.InvokeRequired)
            {
                webView.Invoke(new Action(() => SendStatusUpdate(app, isRecording)));
                return;
            }

            string type, main, detail;
            
            if (isRecording)
            {
                type = "recording";
                main = $"RECORDING: {app}";
                detail = "Buffer active";
            }
            else if (config.RecorderEnabled)
            {
                type = "idle";
                main = "WAITING";
                detail = $"Monitoring for {config.SelectedApplication}...";
            }
            else
            {
                type = "stopped";
                main = "RECORDER OFF";
                detail = "Not recording";
            }

            try
            {
                var message = new { action = "statusUpdate", type, main, detail };
                string json = JsonConvert.SerializeObject(message);
                webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error sending status update: {ex.Message}");
            }
        }
    }
}