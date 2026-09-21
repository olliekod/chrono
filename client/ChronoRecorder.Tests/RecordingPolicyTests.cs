using Xunit;

namespace ChronoRecorder.Tests
{
    public class RecordingPolicyTests
    {
        private const RecorderConfig.RecordingMode App = RecorderConfig.RecordingMode.Application;
        private const RecorderConfig.RecordingMode Display = RecorderConfig.RecordingMode.Display;

        [Fact]
        public void WhenTheRecorderIsOff_NothingRecords()
        {
            Assert.False(RecordingPolicy.ShouldRecord(enabled: false, App, "Risk of rain 2", selectedAppRunning: true));
            Assert.False(RecordingPolicy.ShouldRecord(enabled: false, Display, "", selectedAppRunning: false));
        }

        [Fact]
        public void ApplicationMode_RecordsWhileTheGameIsRunning_WhetherOrNotItHasFocus()
        {
            // "Has focus" is deliberately not an input: tabbing out to Discord must not pause the buffer.
            Assert.True(RecordingPolicy.ShouldRecord(enabled: true, App, "Risk of rain 2", selectedAppRunning: true));
        }

        [Fact]
        public void ApplicationMode_StopsWhenTheGameCloses()
        {
            Assert.False(RecordingPolicy.ShouldRecord(enabled: true, App, "Risk of rain 2", selectedAppRunning: false));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ApplicationMode_WithNoGameChosen_NeverRecords(string? selected)
        {
            Assert.False(RecordingPolicy.ShouldRecord(enabled: true, App, selected, selectedAppRunning: true));
        }

        [Fact]
        public void DisplayMode_RecordsRegardlessOfAnyGame()
        {
            Assert.True(RecordingPolicy.ShouldRecord(enabled: true, Display, "", selectedAppRunning: false));
        }
    }

    public class AutoModePolicyTests
    {
        [Fact]
        public void Auto_RecordsExactlyWhileAGameIsRunning()
        {
            Assert.True(RecordingPolicy.ShouldRecord(true, RecorderConfig.RecordingMode.Auto, "", selectedAppRunning: true));
            Assert.False(RecordingPolicy.ShouldRecord(true, RecorderConfig.RecordingMode.Auto, "", selectedAppRunning: false));
        }

        [Fact]
        public void Auto_NeedsNoChosenApplication()
        {
            Assert.True(RecordingPolicy.ShouldRecord(true, RecorderConfig.RecordingMode.Auto, null, selectedAppRunning: true));
        }

        [Fact]
        public void Auto_NeverRecordsWhileTheRecorderIsOff()
        {
            Assert.False(RecordingPolicy.ShouldRecord(false, RecorderConfig.RecordingMode.Auto, "", selectedAppRunning: true));
        }

        [Fact]
        public void NewInstallsStartInAutoMode_WithTheRecorderOn()
        {
            var config = new RecorderConfig();
            Assert.Equal(RecorderConfig.RecordingMode.Auto, config.Mode);
            Assert.True(config.RecorderEnabled);
        }

        [Fact]
        public void TheNumbersSavedByOlderVersionsKeepTheirMeaning()
        {
            Assert.Equal(0, (int)RecorderConfig.RecordingMode.Display);
            Assert.Equal(1, (int)RecorderConfig.RecordingMode.Application);
            Assert.Equal(2, (int)RecorderConfig.RecordingMode.Auto);
        }
    }

    public class ApplicationNameTests
    {
        [Theory]
        [InlineData("Risk of Rain 2", "Risk of rain 2")]
        [InlineData("risk of rain 2", "Risk of rain 2")]
        [InlineData("RISK OF RAIN 2", "Risk of rain 2")]
        [InlineData("Risk of Rain 2.exe", "Risk of rain 2")]
        [InlineData("javaw", "Minecraft")]
        public void AProcessMatchesTheNameTheAppListShowed(string processName, string selected)
        {
            Assert.True(WindowDetector.NameMatches(processName, selected));
        }

        [Theory]
        [InlineData("Discord", "Risk of rain 2")]
        [InlineData("Risk of Rain 2 Launcher", "Risk of rain 2")]
        [InlineData("", "Risk of rain 2")]
        public void OtherProcessesDoNot(string processName, string selected)
        {
            Assert.False(WindowDetector.NameMatches(processName, selected));
        }

        [Fact]
        public void NothingMatchesAnEmptySelection()
        {
            Assert.False(WindowDetector.NameMatches("Discord", ""));
            Assert.False(WindowDetector.NameMatches("", ""));
        }
    }
}
