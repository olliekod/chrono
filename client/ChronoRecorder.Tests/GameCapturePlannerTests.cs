using Xunit;

namespace ChronoRecorder.Tests
{
    public class GameCapturePlannerTests
    {
        [Fact]
        public void ByDefault_AGameIsCapturedAsItsOwnWindow()
        {
            Assert.Equal(GameCapturePlan.Window, GameCapturePlanner.Plan("window", windowCaptureFailed: false, haveWindow: true));
            Assert.Equal(GameCapturePlan.Window, GameCapturePlanner.Plan(null, false, true));
        }

        [Fact]
        public void TheMonitorIsOnlyUsedWhenAskedFor_OrWhenWindowCaptureFailed()
        {
            Assert.Equal(GameCapturePlan.MonitorWhenInFront, GameCapturePlanner.Plan("monitor", false, true));
            Assert.Equal(GameCapturePlan.MonitorWhenInFront, GameCapturePlanner.Plan("MONITOR", false, true));
            Assert.Equal(GameCapturePlan.MonitorWhenInFront, GameCapturePlanner.Plan("window", windowCaptureFailed: true, haveWindow: true));
        }

        [Theory]
        [InlineData("window", false)]
        [InlineData("monitor", false)]
        [InlineData("window", true)]
        public void WithNoWindowToCapture_NothingIsRecorded_WhateverTheSetting(string preference, bool failed)
        {
            // The rule that matters: when in doubt, record nothing rather than record the desktop.
            Assert.Equal(GameCapturePlan.Unavailable, GameCapturePlanner.Plan(preference, failed, haveWindow: false));
        }

        [Fact]
        public void WindowCaptureMayAlwaysCapture_BecauseNothingElseCanAppearInIt()
        {
            Assert.True(GameCapturePlanner.MayCapture(GameCapturePlan.Window, gameInFront: false));
            Assert.True(GameCapturePlanner.MayCapture(GameCapturePlan.Window, gameInFront: true));
        }

        [Fact]
        public void MonitorCaptureMustPause_WheneverTheGameIsNotInFront()
        {
            Assert.False(GameCapturePlanner.MayCapture(GameCapturePlan.MonitorWhenInFront, gameInFront: false));
            Assert.True(GameCapturePlanner.MayCapture(GameCapturePlan.MonitorWhenInFront, gameInFront: true));
        }

        [Fact]
        public void NothingMayBeCaptured_WhenThereIsNoWindow()
        {
            Assert.False(GameCapturePlanner.MayCapture(GameCapturePlan.Unavailable, gameInFront: true));
        }

        [Fact]
        public void AFreshInstall_CapturesGamesAsWindows()
        {
            Assert.Equal("window", new RecorderConfig().GameCapture);
        }
    }
}
