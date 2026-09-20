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
        /// <summary>Address of the clip server (the Worker). Empty until set up in Settings.</summary>
        public string ApiUrl { get; set; } = "";

        /// <summary>Shared key that lets this app upload. Stored as plain text in the user's config file.</summary>
        public string UploadKey { get; set; } = "";
        public int Bitrate { get; set; } = 8000;
        public int Fps { get; set; } = 60;
        /// <summary>"native" records the monitor as it is (lightest: frames never leave the GPU). A fixed size like
        /// "1920x1080" scales down on the CPU, which costs roughly a core.</summary>
        public string Resolution { get; set; } = "native";
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

        /// <summary>Length of each rolling buffer segment written by the recorder.</summary>
        public const int SegmentSeconds = 10;

        /// <summary>Minimum buffer length. The buffer is always at least long enough for the longest hotkey.</summary>
        public int BufferDurationSeconds { get; set; } = 120;

        /// <summary>
        /// Buffer length actually kept: the user's setting, but never less than the longest hotkey
        /// plus two segments of slack (one in progress, one for the whole-segment rounding).
        /// </summary>
        [JsonIgnore]
        public int RequiredBufferSeconds
        {
            get
            {
                int longest = (Hotkeys != null && Hotkeys.Count > 0) ? Hotkeys.Max(h => h.ClipLengthSeconds) : 0;
                int needed = longest > 0 ? longest + 2 * SegmentSeconds : 0;
                return Math.Max(BufferDurationSeconds, needed);
            }
        }
        public string TempFolder { get; set; } = Path.Combine(Path.GetTempPath(), "Chrono");
        public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos), "Chrono");
        public bool AutoUpload { get; set; } = true;
        public bool CopyLinkToClipboard { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;
        public bool SaveLocalCopy { get; set; } = false;

        /// <summary>
        /// Overwrite this config's values in place. The recorder, hotkey manager and UI all hold this same
        /// instance, so saving settings must mutate it rather than swap in a new object.
        /// </summary>
        public void CopyFrom(RecorderConfig other)
        {
            // Round-trip through JSON so new properties are copied without touching this method.
            // Replace (not Reuse) for the hotkey list so entries are fresh objects, never shared with `other`.
            var replace = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
            JsonConvert.PopulateObject(JsonConvert.SerializeObject(other), this, replace);
        }

        /// <summary>
        /// Config files written by older versions may hold values that no longer make sense.
        /// </summary>
        public void MigrateLegacyValues()
        {
            // The old default pointed at a server nobody here owns; never send clips or keys there.
            if (ApiUrl != null && ApiUrl.Contains("chrono-clips.fly.dev", StringComparison.OrdinalIgnoreCase))
                ApiUrl = "";

            // 1080p used to be the default for everyone, so it says nothing about what anyone chose. Scaling
            // it now costs about a CPU core while gaming; native costs almost nothing. Anything else is a choice.
            if (Resolution == "1920x1080")
                Resolution = "native";
        }

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
                    config.MigrateLegacyValues();
                    
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