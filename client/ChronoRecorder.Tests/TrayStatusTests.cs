using Xunit;

namespace ChronoRecorder.Tests
{
    public class TrayStatusTests
    {
        private const RecorderConfig.RecordingMode Auto = RecorderConfig.RecordingMode.Auto;

        [Fact]
        public void WhileRecording_ItNamesTheGame()
        {
            var (state, text) = TrayStatus.Describe(true, true, Auto, "Risk of rain 2", "");
            Assert.Equal(TrayState.Recording, state);
            Assert.Equal("Recording Risk of rain 2", text);
        }

        [Fact]
        public void WithNoGame_AutoModeSaysItIsWaiting()
        {
            var (state, text) = TrayStatus.Describe(true, false, Auto, "no game running", "");
            Assert.Equal(TrayState.Idle, state);
            Assert.Equal("Waiting for a game", text);
        }

        [Fact]
        public void APausedRecorder_SaysPaused_EvenIfARecordingIsStillFinishing()
        {
            var (state, text) = TrayStatus.Describe(false, true, Auto, "Game", "");
            Assert.Equal(TrayState.Paused, state);
            Assert.Equal("Paused", text);
        }

        [Fact]
        public void ApplicationMode_NamesTheChosenGameOrAsksForOne()
        {
            Assert.Equal("Waiting for Risk of rain 2",
                TrayStatus.Describe(true, false, RecorderConfig.RecordingMode.Application, "x", "Risk of rain 2").Text);
            Assert.Equal("Choose a game to record",
                TrayStatus.Describe(true, false, RecorderConfig.RecordingMode.Application, "x", "").Text);
        }

        [Fact]
        public void DisplayMode_SaysItIsRecordingTheScreen()
        {
            Assert.Equal("Recording the screen",
                TrayStatus.Describe(true, true, RecorderConfig.RecordingMode.Display, "display", "").Text);
        }

        [Fact]
        public void TheTooltip_IsCutToWhatWindowsAccepts()
        {
            string tip = TrayStatus.Tooltip(new string('x', 500));
            Assert.Equal(TrayStatus.MaxTooltipLength, tip.Length);
            Assert.StartsWith("Chrono: ", tip);
            Assert.Equal("Chrono: Paused", TrayStatus.Tooltip("Paused"));
        }
    }
}
