using Xunit;

namespace ChronoRecorder.Tests
{
    public class UploadRulesTests
    {
        [Theory]
        [InlineData("olly", "olly")]
        [InlineData("Oliver Meihls", "Oliver_Meihls")]
        [InlineData("a.b@c!", "a_b_c_")]
        [InlineData("../x", "___x")]
        [InlineData("", "username")]
        [InlineData(null, "username")]
        [InlineData("   ", "username")]
        public void SanitizeUsername_MakesNamesTheServerAccepts(string? input, string expected)
        {
            Assert.Equal(expected, UploadRules.SanitizeUsername(input));
        }

        [Fact]
        public void ANewConfig_DoesNotUseTheWindowsAccountName()
        {
            Assert.Equal("username", new RecorderConfig().Username);
        }

        [Fact]
        public void SanitizeUsername_TruncatesToTheServerLimit()
        {
            Assert.Equal(32, UploadRules.SanitizeUsername(new string('a', 100)).Length);
        }

        [Theory]
        [InlineData("https://clips.example.workers.dev", "https://clips.example.workers.dev")]
        [InlineData("https://clips.example.workers.dev/", "https://clips.example.workers.dev")]
        [InlineData("  https://clips.example.workers.dev//  ", "https://clips.example.workers.dev")]
        [InlineData("clips.example.workers.dev", "https://clips.example.workers.dev")]
        [InlineData("http://localhost:8799", "http://localhost:8799")]
        [InlineData("http://127.0.0.1:8799/", "http://127.0.0.1:8799")]
        public void NormalizeServerUrl_AcceptsUsableAddresses(string input, string expected)
        {
            Assert.Equal(expected, UploadRules.NormalizeServerUrl(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a url")]
        [InlineData("ftp://example.com")]
        [InlineData("http://clips.example.com")]          // would send the key in clear text
        [InlineData("https://chrono-clips.fly.dev")]      // old default that nobody here owns
        [InlineData("chrono-clips.fly.dev")]
        public void NormalizeServerUrl_RejectsUnusableOrUnsafeAddresses(string? input)
        {
            Assert.Equal("", UploadRules.NormalizeServerUrl(input));
        }

        [Fact]
        public void SettingsFrom_NeedsBothAServerAndAKey()
        {
            var config = new RecorderConfig { Username = "Oliver Meihls", ApiUrl = "https://x.example.dev", UploadKey = "k" };

            var settings = UploadRules.SettingsFrom(config);

            Assert.NotNull(settings);
            Assert.Equal("https://x.example.dev", settings!.ServerUrl);
            Assert.Equal("k", settings.UploadKey);
            Assert.Equal("Oliver_Meihls", settings.Username);

            config.UploadKey = "";
            Assert.Null(UploadRules.SettingsFrom(config));

            config.UploadKey = "k";
            config.ApiUrl = "";
            Assert.Null(UploadRules.SettingsFrom(config));
        }

        [Fact]
        public void SettingsFrom_TreatsTheOldFlyDefaultAsNotConfigured()
        {
            var config = new RecorderConfig { ApiUrl = "https://chrono-clips.fly.dev", UploadKey = "k" };
            Assert.Null(UploadRules.SettingsFrom(config));
        }

        [Theory]
        [InlineData(1, 100, 1)]
        [InlineData(100, 100, 1)]
        [InlineData(101, 100, 2)]
        [InlineData(250, 100, 3)]
        public void PartCount_RoundsUp(long size, long partSize, int expected)
        {
            Assert.Equal(expected, UploadRules.PartCount(size, partSize));
        }
    }
}
