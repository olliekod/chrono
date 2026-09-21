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
        public string Username { get; set; } = "username";
        /// <summary>Address of the clip server (the Worker). Empty until set up in Settings.</summary>
        public string ApiUrl { get; set; } = "";

        /// <summary>Shared key that lets this app upload. Stored as plain text in the user's config file.</summary>
        public string UploadKey { get; set; } = "";
        /// <summary>Video bitrate in kbps. 0 means automatic: it follows the picture size and frame rate (see <see cref="BitrateSizing"/>).</summary>
        public int Bitrate { get; set; } = 0;
        public int Fps { get; set; } = 60;
        /// <summary>
        /// The size of saved clips: "native" keeps the monitor's own size, a size like "1920x1080" shrinks clips on the GPU
        /// after you save them. Recording itself is always native (it is nearly free), so this never affects the game.
        /// </summary>
        public string Resolution { get; set; } = "1920x1080";
        public string Encoder { get; set; } = "auto";

        /// <summary>
        /// How hard recording works the graphics card: "auto" (default: lighter for modest cards, and a faster encoder
        /// setting if the PC can't keep up; the frame rate stays as set), "autofps" (the same, and it may also drop to
        /// 30 FPS), "normal" or "light" (both fixed). See <see cref="LoadPlan"/>.
        /// </summary>
        public string EncoderLoad { get; set; } = "auto";

        /// <summary>The step "auto" found this PC needs (0-2), remembered so the next launch starts there instead of losing footage to find it again.</summary>
        public int LearnedLoadLevel { get; set; } = 0;

        public enum RecordingMode
        {
            /// <summary>Record the whole monitor whenever the recorder is on.</summary>
            Display,
            /// <summary>Record while the game chosen in <see cref="SelectedApplication"/> is running.</summary>
            Application,
            /// <summary>Find the game that is running and record it: nothing to choose (see <see cref="GameClassifier"/>).
            /// Appended, so the numbers saved by older versions keep their meaning.</summary>
            Auto
        }
        
        public RecordingMode Mode { get; set; } = RecordingMode.Auto;

        /// <summary>
        /// Which generation of settings this file is. Old files have no value (0) and are brought up to date once by
        /// <see cref="MigrateLegacyValues"/>.
        /// </summary>
        public int ConfigVersion { get; set; } = 0;

        /// <summary>The current generation. 1: the library and automatic recording arrived. 2: game capture became "auto".</summary>
        public const int CurrentConfigVersion = 2;
        public bool RecorderEnabled { get; set; } = true;
        public string SelectedApplication { get; set; } = "";
        public int MinimumFocusTimeSeconds { get; set; } = 2;

        // CRITICAL FIX: Initialize as null, set defaults in a separate method
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<HotkeyConfig> Hotkeys { get; set; } = null;

        /// <summary>Length of each rolling buffer segment written by the recorder.</summary>
        public const int SegmentSeconds = 10;

        /// <summary>
        /// How much footage the buffer keeps. Not a setting: it follows from the hotkeys. The longest clip plus two
        /// segments of slack (one being written, one for whole-segment rounding), so no hotkey can ever outrun it.
        /// Edit a clip length and the buffer follows. With no hotkeys there is still a small floor.
        /// </summary>
        [JsonIgnore]
        public int RequiredBufferSeconds
        {
            get
            {
                int longest = (Hotkeys != null && Hotkeys.Count > 0) ? Hotkeys.Max(h => h.ClipLengthSeconds) : 0;
                return Math.Max(longest, 0) + 2 * SegmentSeconds + (longest > 0 ? 0 : SegmentSeconds);
            }
        }
        public string TempFolder { get; set; } = Path.Combine(Path.GetTempPath(), "Chrono");
        public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos), "Chrono");
        /// <summary>Record what you hear: the game, voice chat, music (the default output device).</summary>
        public bool RecordAudio { get; set; } = true;

        /// <summary>Also record the default microphone. Off by default: it is a privacy decision.</summary>
        public bool RecordMicrophone { get; set; } = false;

        /// <summary>
        /// How a game is captured. "auto" (default): the game's own window on Windows 11, where nothing extra shows on screen,
        /// and the monitor (only while the game is in front) on Windows 10, where capturing a window makes Windows draw a
        /// yellow border around it. "window": always the game's own window, so nothing else can ever be in the clip, border
        /// or not. "monitor": always the monitor, only while the game is in front (for the rare game that records black as a
        /// window). Whole-screen mode always records the monitor.
        /// </summary>
        public string GameCapture { get; set; } = "auto";

        /// <summary>Which speakers/headphones to record the game from (its Windows device id). Empty = Windows' default.</summary>
        public string SpeakerDeviceId { get; set; } = "";

        /// <summary>Which microphone to record (its Windows device id). Empty = Windows' default.</summary>
        public string MicrophoneDeviceId { get; set; } = "";

        /// <summary>How loud the microphone is in clips, in percent: 100 = as the device delivers it, 300 = three times louder.</summary>
        public int MicrophoneVolumePercent { get; set; } = 100;

        /// <summary>A short chime when a hotkey saves a clip, so you know it worked without looking.</summary>
        public bool PlaySoundOnClip { get; set; } = true;

        /// <summary>Trim a constant audio/video offset, in milliseconds (positive delays the sound). Normally 0.</summary>
        public int AudioDelayMs { get; set; } = 0;

        /// <summary>Not used any more: clips are saved to the library and only uploaded when you choose. Kept so old config files load.</summary>
        public bool AutoUpload { get; set; } = false;

        /// <summary>Not used any more: a link is copied when you upload or press Copy link. Kept so old config files load.</summary>
        public bool CopyLinkToClipboard { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;

        /// <summary>Show the Diagnostics page in the sidebar. Off by default: it is for finding out why something isn't working, not for everyday use.</summary>
        public bool ShowDiagnostics { get; set; } = false;

        /// <summary>Start Chrono (into the tray) when Windows starts. Only a real install is ever registered.</summary>
        public bool StartWithWindows { get; set; } = true;

        /// <summary>False until the first run has opened the window once; after that Chrono starts in the tray.</summary>
        public bool FirstRunCompleted { get; set; } = false;
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

            // Before the library and automatic recording, the recorder had an on/off button and a game to pick. Someone
            // who last pressed Stop would otherwise find the new, automatic Chrono doing nothing. Done once.
            if (ConfigVersion < 1)
            {
                RecorderEnabled = true;
                Mode = RecordingMode.Auto;
            }

            // "window" used to be the default and was never a choice anyone had to make, so it is not a preference to keep:
            // on Windows 10 it puts a yellow border over the game. "auto" is the same on Windows 11 and safe on Windows 10.
            // Someone who really wants the window on Windows 10 can pick it again in Settings.
            if (ConfigVersion < 2 && string.Equals(GameCapture, "window", StringComparison.OrdinalIgnoreCase))
                GameCapture = "auto";

            ConfigVersion = CurrentConfigVersion;

            // 8000 used to be the default for everyone; it was too little for 1440p and was never a choice.
            if (Bitrate == BitrateSizing.LegacyDefaultKbps)
                Bitrate = 0;
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