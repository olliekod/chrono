using System.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class ClipPlannerTests
    {
        private static SegmentSpan[] Segs(int count, double each = 10)
            => Enumerable.Range(1, count).Select(i => new SegmentSpan($"s{i}", each)).ToArray();

        [Fact]
        public void NoSegments_ReturnsNull()
        {
            Assert.Null(ClipPlanner.Plan(Array.Empty<SegmentSpan>(), 30));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void NonPositiveLength_Throws(double length)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ClipPlanner.Plan(Segs(3), length));
        }

        [Fact]
        public void ExactFit_UsesExactlyEnoughSegmentsAndNoSeek()
        {
            var plan = ClipPlanner.Plan(Segs(3), 30)!;

            Assert.Equal(new[] { "s1", "s2", "s3" }, plan.Segments.Select(s => s.Path));
            Assert.Equal(0, plan.SeekSeconds, 3);
            Assert.False(plan.IsShort);
        }

        [Fact]
        public void Overshoot_SeeksPastTheOldestFootage_KeepingTheNewest()
        {
            // 25s needs 3 whole segments (30s). The extra 5s must be skipped from the START,
            // not cut off the end (the old `-t` behaviour dropped the newest footage).
            var plan = ClipPlanner.Plan(Segs(3), 25)!;

            Assert.Equal(3, plan.Segments.Count);
            Assert.Equal(5, plan.SeekSeconds, 3);
            Assert.Equal(30, plan.AvailableSeconds, 3);
        }

        [Fact]
        public void LongBuffer_PicksOnlyTheNewestSegments_InChronologicalOrder()
        {
            var plan = ClipPlanner.Plan(Segs(12), 30)!;

            Assert.Equal(new[] { "s10", "s11", "s12" }, plan.Segments.Select(s => s.Path));
        }

        [Fact]
        public void NotEnoughFootage_UsesEverythingAndIsFlaggedShort()
        {
            var plan = ClipPlanner.Plan(Segs(2), 60)!;

            Assert.Equal(2, plan.Segments.Count);
            Assert.Equal(0, plan.SeekSeconds, 3);
            Assert.True(plan.IsShort);
            Assert.Equal(20, plan.AvailableSeconds, 3);
        }

        [Fact]
        public void InProgressSegment_CountsTowardTheClip()
        {
            var spans = new[]
            {
                new SegmentSpan("s1", 10),
                new SegmentSpan("s2", 10),
                new SegmentSpan("live", 4.5),
            };

            // 4.5 + 10 = 14.5 < 15, so it must pull in a second segment: 24.5 total, skip 9.5.
            var plan = ClipPlanner.Plan(spans, 15)!;

            Assert.Equal(new[] { "s1", "s2", "live" }, plan.Segments.Select(s => s.Path));
            Assert.Equal(9.5, plan.SeekSeconds, 3);
        }
    }
}
