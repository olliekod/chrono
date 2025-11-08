using System;
using System.IO;
using Newtonsoft.Json;

namespace ChronoRecorder
{
    public class HotkeyConfig
    {
        public string Name { get; set; }
        public string Key { get; set; }
        public List<string> Modifiers { get; set; } = new List<string>();
        public int ClipLengthSeconds { get; set; }
    }

    public class RecorderConfig
    {
        public string Username { get; set; } = Environment.UserName;
        public string ApiUrl { get; set; } = "https://chrono-clips.fly.dev";
        public int Bitrate { get; set; } = 8000;
        public int Fps { get; set; } = 60;
        public string Resolution { get; set; } = "1920x1080";
        public string Encoder { get; set; } = "auto";

        public enum RecordingMode
        {
            Display,
            Application
        }
        
        public RecordingMode Mode { get; set; } = RecordingMode.Application;
        public bool RecorderEnabled { get; set; } = false;
        public string SelectedApplication { get; set; } = "";
        public int MinimumFocusTimeSeconds { get; set; } = 2;

        // CRITICAL FIX: Initialize as null, set defaults in a separate method
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<HotkeyConfig> Hotkeys { get; set; } = null;

        public int BufferDurationSeconds { get; set; } = 120;
        public string TempFolder { get; set; } = Path.Combine(Path.GetTempPath(), "Chrono");
        public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos), "Chrono");
        public bool AutoUpload { get; set; } = true;
        public bool CopyLinkToClipboard { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;
        public bool SaveLocalCopy { get; set; } = false;

        // Method to set default hotkeys
        public void SetDefaultHotkeys()
        {
            if (Hotkeys == null || Hotkeys.Count == 0)
            {
                Hotkeys = new List<HotkeyConfig>
                {
                    new HotkeyConfig
                    {
                        Name = "Quick Clip",
                        Key = "F8",
                        Modifiers = new List<string>{ "Control" },
                        ClipLengthSeconds = 30
                    },
                    new HotkeyConfig
                    {
                        Name = "Long Clip",
                        Key = "Home",
                        Modifiers = new List<string> { "Control" },
                        ClipLengthSeconds = 120
                    }
                };
            }
        }
    }
    
    public class ConfigManager
    {
        private static readonly string ConfigFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chrono");
        private static readonly string ConfigPath = Path.Combine(ConfigFolder, "config.json");

        public static RecorderConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    
                    Console.WriteLine("=== LOADING CONFIG ===");
                    Console.WriteLine($"JSON length: {json.Length} characters");
                    
                    var config = JsonConvert.DeserializeObject<RecorderConfig>(json);
                    
                    Console.WriteLine($"Loaded config with {config.Hotkeys.Count} hotkeys:");
                    foreach (var hotkey in config.Hotkeys)
                    {
                        Console.WriteLine($"  - {hotkey.Name}: {string.Join("+", hotkey.Modifiers)}+{hotkey.Key} ({hotkey.ClipLengthSeconds}s)");
                    }
                    Console.WriteLine("======================\n");
                    
                    Console.WriteLine($"✓ Config loaded from {ConfigPath}");
                    return config;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error loading config: {ex.Message}");
            }

            Console.WriteLine("Creating default configuration...");
            Console.WriteLine("Auto-detecting GPU encoder...");
            string detectedEncoder = GpuDetector.GetBestEncoder();

            var defaultConfig = new RecorderConfig
            {
                Encoder = detectedEncoder
            };
            
            // Set default hotkeys for new config
            defaultConfig.SetDefaultHotkeys();

            Save(defaultConfig);
            return defaultConfig;
        }

        public static void Save(RecorderConfig config)
        {
            try
            {
                Console.WriteLine("=== SAVING CONFIG ===");
                
                // Ensure hotkeys list exists
                if (config.Hotkeys == null)
                {
                    Console.WriteLine("⚠ Hotkeys was null, initializing...");
                    config.SetDefaultHotkeys();
                }
                
                Console.WriteLine($"Config has {config.Hotkeys.Count} hotkeys:");
                foreach (var hotkey in config.Hotkeys)
                {
                    Console.WriteLine($"  - {hotkey.Name}: {string.Join("+", hotkey.Modifiers)}+{hotkey.Key} ({hotkey.ClipLengthSeconds}s)");
                }
                
                Directory.CreateDirectory(ConfigFolder);
                
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore,
                    DefaultValueHandling = DefaultValueHandling.Include
                };
                
                var json = JsonConvert.SerializeObject(config, settings);
                
                File.WriteAllText(ConfigPath, json);
                
                Console.WriteLine($"✓ Config saved to {ConfigPath}");
                Console.WriteLine("======================\n");
                
                // Verify
                var verifyJson = File.ReadAllText(ConfigPath);
                var verifyConfig = JsonConvert.DeserializeObject<RecorderConfig>(verifyJson);
                Console.WriteLine($"VERIFICATION: File now contains {verifyConfig.Hotkeys.Count} hotkeys");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error saving config: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        public static string GetConfigPath() => ConfigPath;
    }
}