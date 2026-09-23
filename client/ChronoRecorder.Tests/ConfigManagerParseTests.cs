using Newtonsoft.Json;
using Xunit;

namespace ChronoRecorder.Tests
{
    /// <summary>
    /// Covers ConfigManager.Parse, the part of loading a config file that doesn't touch disk. Load() itself reads from
    /// %APPDATA% and isn't under test; a bad file there used to be caught and replaced with fresh defaults, with no sign
    /// of what happened (see Parse's own remarks).
    /// </summary>
    public class ConfigManagerParseTests
    {
        [Fact]
        public void AFileMissingHotkeys_GetsTheDefaultOnes_InsteadOfCrashingTheLoad()
        {
            // What a much older file, or one edited by hand, might look like: everything but "Hotkeys".
            string json = "{\"Username\":\"Oliver\",\"ApiUrl\":\"https://clips.example.workers.dev\",\"UploadKey\":\"secret\"}";

            var config = ConfigManager.Parse(json);

            Assert.Equal("Oliver", config.Username);
            Assert.Equal("https://clips.example.workers.dev", config.ApiUrl);
            Assert.Equal("secret", config.UploadKey);
            Assert.Equal(2, config.Hotkeys.Count);   // the defaults, not an empty or null list
        }

        [Fact]
        public void AnEmptyHotkeysList_IsLeftEmpty()
        {
            // Unlike a missing list, an empty one is a choice someone could have made (deleted both hotkeys in Settings
            // and closed without adding one back); SetDefaultHotkeys only fills in null or empty, so this still refills
            // it. That mirrors Save(), which does the same before writing, so a file on disk is never actually saved
            // with zero hotkeys; this only matters for a file edited or written by something else.
            string json = "{\"Hotkeys\":[]}";

            var config = ConfigManager.Parse(json);

            Assert.Equal(2, config.Hotkeys.Count);
        }

        [Fact]
        public void AFileWithHotkeys_KeepsThem_AndDoesNotAddMore()
        {
            string json = "{\"Hotkeys\":[{\"Name\":\"Clutch\",\"Key\":\"F9\",\"Modifiers\":[],\"ClipLengthSeconds\":45}]}";

            var config = ConfigManager.Parse(json);

            Assert.Equal(new[] { "Clutch" }, config.Hotkeys.ConvertAll(h => h.Name));
        }

        [Fact]
        public void AFileFromBeforeTheLibrary_IsMigratedAndGetsItsHotkeys()
        {
            string json = "{\"Mode\":1,\"RecorderEnabled\":false,\"SelectedApplication\":\"Risk of rain 2\"}";

            var config = ConfigManager.Parse(json);

            Assert.Equal(RecorderConfig.RecordingMode.Auto, config.Mode);
            Assert.True(config.RecorderEnabled);
            Assert.Equal(2, config.Hotkeys.Count);
        }

        [Fact]
        public void TheLiteralWordNull_IsRefused_RatherThanCrashingSomewhereLessObvious()
        {
            Assert.Throws<JsonException>(() => ConfigManager.Parse("null"));
        }
    }
}
