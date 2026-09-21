using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ChronoRecorder
{
    /// <summary>
    /// Checks and tidies settings before they are applied. The window already stops most mistakes, but the settings
    /// file is what runs the recorder (and holds the upload key), so nothing unchecked goes into it.
    /// </summary>
    public static class SettingsRules
    {
        public const int MinClipSeconds = 5;
        public const int MaxClipSeconds = 600;
        private static readonly string[] Encoders = { "auto", "h264_nvenc", "h264_amf", "h264_qsv", "libx264" };

        /// <summary>Tidies the values that can be tidied (trimming, name length) and returns a message for the first thing wrong, or null.</summary>
        public static string? ValidateAndTidy(RecorderConfig c)
        {
            c.Username = (c.Username ?? "").Trim();
            if (c.Username.Length > 32) c.Username = c.Username.Substring(0, 32);
            c.ApiUrl = (c.ApiUrl ?? "").Trim();
            c.UploadKey = (c.UploadKey ?? "").Trim();

            if (c.ApiUrl.Length > 0 && UploadRules.NormalizeServerUrl(c.ApiUrl).Length == 0)
                return "That server address isn't valid. Use an https:// address like https://your-server.workers.dev.";

            if (c.MicrophoneVolumePercent < 0 || c.MicrophoneVolumePercent > 500) return "The microphone volume must be between 0% and 500%.";
            c.SpeakerDeviceId = (c.SpeakerDeviceId ?? "").Trim();
            c.MicrophoneDeviceId = (c.MicrophoneDeviceId ?? "").Trim();

            if (c.Fps < 24 || c.Fps > 240) return "The frame rate must be between 24 and 240.";
            if (c.Bitrate != 0 && (c.Bitrate < 1000 || c.Bitrate > 80000)) return "The bitrate must be between 1000 and 80000 kbps, or empty for the recommended one.";
            if (!Regex.IsMatch(c.Resolution ?? "", @"^(native|\d{3,5}x\d{3,5})$", RegexOptions.IgnoreCase)) return "Choose a clip size from the list.";
            if (!Encoders.Contains(c.Encoder ?? "", StringComparer.OrdinalIgnoreCase)) return "Choose an encoder from the list.";

            return ValidateHotkeys(c.Hotkeys);
        }

        public static string? ValidateHotkeys(List<HotkeyConfig>? hotkeys)
        {
            if (hotkeys == null) return null;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hotkey in hotkeys)
            {
                hotkey.Name = (hotkey.Name ?? "").Trim();
                if (hotkey.Name.Length == 0) return "Every hotkey needs a name.";
                if (hotkey.Name.Length > 40) hotkey.Name = hotkey.Name.Substring(0, 40);

                if (hotkey.ClipLengthSeconds < MinClipSeconds || hotkey.ClipLengthSeconds > MaxClipSeconds)
                    return $"A clip must be between {MinClipSeconds} and {MaxClipSeconds} seconds ({hotkey.Name}).";

                if (string.IsNullOrWhiteSpace(hotkey.Key)) return $"\"{hotkey.Name}\" needs keys.";

                hotkey.Modifiers ??= new List<string>();
                string? keyProblem = HotkeyParser.Problem(hotkey.Modifiers, hotkey.Key);
                if (keyProblem != null) return $"{hotkey.Name}: {keyProblem}";
                uint mods = HotkeyParser.ParseModifiers(hotkey.Modifiers);
                if (!seen.Add($"{mods}+{hotkey.Key.ToUpperInvariant()}")) return $"Two hotkeys use the same keys ({hotkey.Name}).";
            }
            return null;
        }
    }
}
