using System;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class CaptureStarvationTests
    {
        private static readonly DateTime Start = new(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc);

        // One report two seconds after the last: 120 frames written, of which `repeats` were copies of the one before.
        private sealed class Feed
        {
            private long frames, repeats;
            private int seconds = 20;   // past the start-up warm-up
            public EncodeStats Next(int repeatsThisReport, double speed = 1.0)
            {
                frames += 120; repeats += repeatsThisReport; seconds += 2;
                return new EncodeStats(frames, 60, repeats, 0, speed, 0, seconds, Start.AddSeconds(seconds));
            }
        }

        private static bool Run(CaptureStarvation s, Feed feed, int reports, int repeatsEach, bool inFront = true)
        {
            bool fired = false;
            for (int i = 0; i < reports; i++) fired |= s.Observe(feed.Next(repeatsEach), Start, inFront);
            return fired;
        }

        [Fact]
        public void FiresAfterThirtySecondsOfAlmostOnlyRepeats()
        {
            var s = new CaptureStarvation();
            var feed = new Feed();
            s.Observe(feed.Next(0), Start, true);   // the first report only has something to compare against

            Assert.False(Run(s, feed, CaptureStarvation.Reports - 1, 118));
            Assert.True(s.Observe(feed.Next(118), Start, true));
        }

        [Fact]
        public void AnOrdinaryBadSlideshowIsLeftToTheGovernor()
        {
            // 80% repeats is the GTX 1650 case: a slow card, not a dead window.
            var s = new CaptureStarvation();
            Assert.False(Run(s, new Feed(), 60, 96));
        }

        [Fact]
        public void OneGoodReportStartsTheCountAgain()
        {
            var s = new CaptureStarvation();
            var feed = new Feed();
            Run(s, feed, CaptureStarvation.Reports - 1, 119);
            s.Observe(feed.Next(10), Start, true);

            Assert.False(Run(s, feed, CaptureStarvation.Reports - 1, 119));
        }

        [Fact]
        public void DoesNotCountWhileTheGameIsNotInFront()
        {
            // A minimised or backgrounded game legitimately holds one picture.
            var s = new CaptureStarvation();
            Assert.False(Run(s, new Feed(), 60, 120, inFront: false));
        }

        [Fact]
        public void IgnoresTheWarmUp()
        {
            var s = new CaptureStarvation();
            bool fired = false;
            long frames = 0, repeats = 0;
            for (int sec = 2; sec <= 10; sec += 2)
            {
                frames += 120; repeats += 120;
                fired |= s.Observe(new EncodeStats(frames, 60, repeats, 0, 1.0, 0, sec, Start.AddSeconds(sec)), Start, true);
            }
            Assert.False(fired);
        }

        [Fact]
        public void StartsCountingAgainAfterFiring()
        {
            var s = new CaptureStarvation();
            var feed = new Feed();
            s.Observe(feed.Next(0), Start, true);

            Assert.True(Run(s, feed, CaptureStarvation.Reports, 120));
            Assert.False(Run(s, feed, CaptureStarvation.Reports - 1, 120));
        }

        [Fact]
        public void FirstStarvationRestartsTheCapture()
            => Assert.Equal(StarvationResponse.RestartCapture, CaptureStarvation.Respond(null, Start));

        [Fact]
        public void AnotherSoonAfterMeansUseScreenCapture()
            => Assert.Equal(StarvationResponse.UseScreenCapture, CaptureStarvation.Respond(Start, Start.AddMinutes(4)));

        [Fact]
        public void ALongGapIsTreatedAsANewProblem()
            => Assert.Equal(StarvationResponse.RestartCapture, CaptureStarvation.Respond(Start, Start.AddMinutes(45)));
    }
}
