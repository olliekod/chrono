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
        public void RequiredBuffer_CoversLongestHotkeyPlusSlack()
        {
            var config = new RecorderConfig
            {
                BufferDurationSeconds = 60,
                Hotkeys = new List<HotkeyConfig> { Hk("a", 30), Hk("b", 120) }
            };

            Assert.Equal(120 + 2 * RecorderConfig.SegmentSeconds, config.RequiredBufferSeconds);
        }

        [Fact]
        public void RequiredBuffer_HonoursALargerUserSetting()
        {
            var config = new RecorderConfig
            {
                BufferDurationSeconds = 300,
                Hotkeys = new List<HotkeyConfig> { Hk("a", 30) }
            };

            Assert.Equal(300, config.RequiredBufferSeconds);
        }

        [Fact]
        public void RequiredBuffer_WithoutHotkeys_FallsBackToTheSetting()
        {
            var config = new RecorderConfig { BufferDurationSeconds = 120, Hotkeys = null! };

            Assert.Equal(120, config.RequiredBufferSeconds);
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
