using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Newtonsoft.Json;

namespace ChronoRecorder
{
    /// <summary>
    /// user config
    /// </summary>
    public class HotkeyConfig
    {
        public string Name { get; set; }
        public string Key { get; set; }
        public List<string> Modifiers { get; set; } = new List<string>();
        public int ClipLengthSeconds { get; set; }
    }

    /// <summary>
    /// user config
    /// </summary>
    public class RecorderConfig
    {
        // user identity for querying/thumbnail
        public string Username { get; set; } = Environment.UserName;

        // api
        public string ApiUrl { get; set; } = "https://chrono-clips.fly.dev";

        // recording settings
        public int Bitrate { get; set; } = 8000; // this is in kbps
        public int Fps { get; set; } = 60;
        public string Resolution { get; set; } = "1920x1080";
        public string Encoder { get; set; } = "auto";


        // Recording mode
        public enum RecordingMode
        {
            Display,        // Always record screen
            Application     // Only record specific app
        }
        
        public RecordingMode Mode { get; set; } = RecordingMode.Application;

        // Recorder state
        public bool RecorderEnabled { get; set; } = false; // Start with recorder OFF
        
        // Currently selected application to track (set via UI)
        public string SelectedApplication { get; set; } = "";

        // Minimum window focus time before starting recording (prevents accidental triggers)
        public int MinimumFocusTimeSeconds { get; set; } = 2;

        // custom hotkey configurations
        public List<HotkeyConfig> Hotkeys { get; set; } = new List<HotkeyConfig>
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

        // buffer settings
        public int BufferDurationSeconds { get; set; } = 120; // keeps last 2 minutes

        // filepaths for video output
        public string TempFolder { get; set; } = Path.Combine(Path.GetTempPath(), "Chrono");
        public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Chrono");

        // features
        public bool AutoUpload { get; set; } = true;
        public bool CopyLinkToClipboard { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;
        public bool SaveLocalCopy { get; set; } = false; // keep clips locally default false.
    }
    
    /// <summary>
    /// manages loading and saving the config
    /// </summary>
    public class ConfigManager
    {
        // pathfinding for config.json
        private static readonly string ConfigFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chrono");
        private static readonly string ConfigPath = Path.Combine(ConfigFolder, "config.json");

        /// <summary>
        /// load config from disk if exists or create default
        /// </summary>
        public static RecorderConfig Load()
        {
            try
            {
                // checks if the file exists
                if (File.Exists(ConfigPath))
                {
                    // if so, pulls data from json
                    var json = File.ReadAllText(ConfigPath);
                    var config = JsonConvert.DeserializeObject<RecorderConfig>(json);
                    Console.WriteLine($"✓ Config loaded from {ConfigPath}");
                    return config;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error loading config: {ex.Message}");
            }

            // returns default config and saves it with automatic encoder
            Console.WriteLine("Creating default configuration...");
            Console.WriteLine("Auto-detecting GPU encoder...");
            string detectedEncoder = GpuDetector.GetBestEncoder();

            var defaultConfig = new RecorderConfig
            {
                Encoder = detectedEncoder  // set detected encoder immediately
            };

            Save(defaultConfig);
            return defaultConfig;
        }

        /// <summary>
        /// save config to disk
        /// </summary>
        public static void Save(RecorderConfig config)
        {
            try
            {
                Directory.CreateDirectory(ConfigFolder);
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
                Console.WriteLine($"✓ Config saved to {ConfigPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error saving config: {ex.Message}");
            }
        }

        /// <summary>
        /// get the config file path for user reference next time!
        /// </summary>
        public static string GetConfigPath() => ConfigPath;
    }
}