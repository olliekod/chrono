using System.Linq;
using Newtonsoft.Json;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class RecorderConfigTests
    {
        private static HotkeyConfig Hk(string name, int seconds)
            => new HotkeyConfig { Name = name, Key = "F8", ClipLengthSeconds = seconds };

        [Fact]
        public void RequiredBuffer_IsTheLongestHotkeyPlusSlack()
        {
            var config = new RecorderConfig { Hotkeys = new List<HotkeyConfig> { Hk("a", 30), Hk("b", 120) } };

            Assert.Equal(120 + 2 * RecorderConfig.SegmentSeconds, config.RequiredBufferSeconds);
        }

        [Fact]
        public void RequiredBuffer_FollowsEditsToTheClipLengths()
        {
            var config = new RecorderConfig { Hotkeys = new List<HotkeyConfig> { Hk("quick", 30), Hk("long", 120) } };
            int before = config.RequiredBufferSeconds;

            config.Hotkeys[1].ClipLengthSeconds = 300;

            Assert.Equal(before + 180, config.RequiredBufferSeconds);
            config.Hotkeys[1].ClipLengthSeconds = 10;
            Assert.Equal(30 + 2 * RecorderConfig.SegmentSeconds, config.RequiredBufferSeconds);   // the quick clip is now the longest
        }

        [Fact]
        public void RequiredBuffer_WithoutHotkeys_IsASmallFloor()
        {
            var config = new RecorderConfig { Hotkeys = null! };

            Assert.Equal(3 * RecorderConfig.SegmentSeconds, config.RequiredBufferSeconds);
        }

        [Fact]
        public void ConfigFilesFromBeforeTheSettingWasRemoved_StillLoad()
        {
            string old = "{\"BufferDurationSeconds\": 600, \"Fps\": 90}";

            var config = JsonConvert.DeserializeObject<RecorderConfig>(old)!;

            Assert.Equal(90, config.Fps);
        }

        [Fact]
        public void Deserialising_DoesNotAppendToDefaultHotkeys()
        {
            // Regression: saved hotkeys used to be appended to a default list, duplicating them.
            var config = new RecorderConfig();
            config.SetDefaultHotkeys();
            string json = JsonConvert.SerializeObject(config);

            var loaded = JsonConvert.DeserializeObject<RecorderConfig>(json)!;

            Assert.Equal(config.Hotkeys.Count, loaded.Hotkeys.Count);
        }

        [Fact]
        public void NewConfigs_HaveNoServerAndNoKey()
        {
            var config = new RecorderConfig();
            Assert.Equal("", config.ApiUrl);
            Assert.Equal("", config.UploadKey);
        }

        [Theory]
        [InlineData("https://chrono-clips.fly.dev")]
        [InlineData("https://CHRONO-CLIPS.FLY.DEV/")]
        public void MigrateLegacyValues_ClearsTheOldFlyAddress(string old)
        {
            var config = new RecorderConfig { ApiUrl = old };
            config.MigrateLegacyValues();
            Assert.Equal("", config.ApiUrl);
        }

        [Fact]
        public void NewConfigs_RecordAtNativeResolution()
        {
            Assert.Equal("native", new RecorderConfig().Resolution);
        }

        [Fact]
        public void MigrateLegacyValues_MovesTheOldDefaultResolutionToNative()
        {
            var config = new RecorderConfig { Resolution = "1920x1080" };
            config.MigrateLegacyValues();
            Assert.Equal("native", config.Resolution);
        }

        [Theory]
        [InlineData("1280x720")]
        [InlineData("2560x1440")]
        [InlineData("native")]
        public void MigrateLegacyValues_LeavesOtherResolutionChoicesAlone(string chosen)
        {
            var config = new RecorderConfig { Resolution = chosen };
            config.MigrateLegacyValues();
            Assert.Equal(chosen, config.Resolution);
        }

        [Fact]
        public void MigrateLegacyValues_LeavesARealServerAlone()
        {
            var config = new RecorderConfig { ApiUrl = "https://clips.example.workers.dev" };
            config.MigrateLegacyValues();
            Assert.Equal("https://clips.example.workers.dev", config.ApiUrl);
        }

        [Fact]
        public void CopyFrom_KeepsTheSameInstance_AndCopiesValues()
        {
            var live = new RecorderConfig { Fps = 30 };
            live.SetDefaultHotkeys();
            var incoming = new RecorderConfig
            {
                Fps = 90,
                Bitrate = 12000,
                Hotkeys = new List<HotkeyConfig> { Hk("only", 45) }
            };

            live.CopyFrom(incoming);

            Assert.Equal(90, live.Fps);
            Assert.Equal(12000, live.Bitrate);
            Assert.Equal(new[] { "only" }, live.Hotkeys.Select(h => h.Name));
        }

        [Fact]
        public void CopyFrom_DoesNotShareHotkeyObjectsWithTheSource()
        {
            var live = new RecorderConfig();
            var incoming = new RecorderConfig { Hotkeys = new List<HotkeyConfig> { Hk("x", 10) } };

            live.CopyFrom(incoming);
            incoming.Hotkeys[0].ClipLengthSeconds = 999;

            Assert.Equal(10, live.Hotkeys[0].ClipLengthSeconds);
        }
    }
}
