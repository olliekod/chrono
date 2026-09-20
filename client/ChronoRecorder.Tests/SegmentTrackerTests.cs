using System.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class SegmentTrackerTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), $"chrono-tracker-{Guid.NewGuid():N}");
        private readonly Dictionary<string, double?> probed = new();

        public SegmentTrackerTests() => Directory.CreateDirectory(folder);
        public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

        private string Make(string run, int index)
        {
            string path = Path.Combine(folder, $"segment_{run}_{index:D5}.ts");
            File.WriteAllBytes(path, new byte[] { 1 });
            return path;
        }

        private SegmentTracker Tracker() => new(folder, 10, path =>
        {
            probed.TryAdd(Path.GetFileName(path), null);
            return probed[Path.GetFileName(path)];
        });

        [Fact]
        public void NamePattern_ProducesNamesTheTrackerReads()
        {
            string pattern = SegmentTracker.NamePattern(folder, "20260920120000");
            Assert.Contains("%05d", pattern);

            // What ffmpeg would write for segment 3.
            string written = pattern.Replace("%05d", "00003");
            File.WriteAllBytes(written, new byte[] { 1 });
            File.WriteAllBytes(pattern.Replace("%05d", "00004"), new byte[] { 1 });

            var scan = Tracker().Scan(recording: true);

            Assert.Equal(new[] { written }, scan.Finished.Select(s => s.Path));
        }

        [Fact]
        public void ANewestFileFromAnEarlierRun_IsNotLive()
        {
            // The recorder just restarted: ffmpeg hasn't created its first segment yet, so the newest file
            // on disk is the earlier run's last one and is finished (probably cut short), not being written.
            var old = Make("20260920120000", 0);
            probed["segment_20260920120000_00000.ts"] = 3.5;

            var scan = Tracker().Scan(recording: true, currentRun: "20260920130000");

            Assert.Null(scan.LivePath);
            Assert.Equal(new[] { old }, scan.Finished.Select(s => s.Path));
            Assert.Equal(3.5, scan.Finished[0].DurationSeconds, 3);
        }

        [Fact]
        public void WhileRecording_TheNewestFileIsLive_AndTheRestAreFinished()
        {
            var a = Make("20260920120000", 0);
            var b = Make("20260920120000", 1);
            var c = Make("20260920120000", 2);

            var scan = Tracker().Scan(recording: true);

            Assert.Equal(new[] { a, b }, scan.Finished.Select(s => s.Path));
            Assert.All(scan.Finished, s => Assert.Equal(10, s.DurationSeconds));
            Assert.Equal(c, scan.LivePath);
        }

        [Fact]
        public void WhenNotRecording_EverythingIsFinished_AndTheLastFileIsMeasured()
        {
            Make("20260920120000", 0);
            var last = Make("20260920120000", 1);
            probed["segment_20260920120000_00001.ts"] = 4.2;

            var scan = Tracker().Scan(recording: false);

            Assert.Null(scan.LivePath);
            Assert.Equal(2, scan.Finished.Count);
            Assert.Equal(10, scan.Finished[0].DurationSeconds);
            Assert.Equal(last, scan.Finished[1].Path);
            Assert.Equal(4.2, scan.Finished[1].DurationSeconds, 3);
        }

        [Fact]
        public void AFileThatCantBeMeasured_FallsBackToTheNominalLength()
        {
            Make("20260920120000", 0);

            var scan = Tracker().Scan(recording: false);

            Assert.Equal(10, scan.Finished[0].DurationSeconds);
        }

        [Fact]
        public void FilesAreOrderedByRunThenByIndex()
        {
            var late = Make("20260920130000", 0);
            var early1 = Make("20260920120000", 1);
            var early0 = Make("20260920120000", 0);
            probed["segment_20260920120000_00001.ts"] = 6;   // last file of the earlier run, cut short

            var scan = Tracker().Scan(recording: true);

            Assert.Equal(new[] { early0, early1 }, scan.Finished.Select(s => s.Path));
            Assert.Equal(6, scan.Finished[1].DurationSeconds);   // last of its run: measured, not assumed to be 10
            Assert.Equal(late, scan.LivePath);
        }

        [Fact]
        public void OrderingIsNumeric_NotAlphabetical()
        {
            var first = Make("20260920120000", 9);
            var second = Make("20260920120000", 10);
            Make("20260920120000", 11);

            var scan = Tracker().Scan(recording: true);

            Assert.Equal(new[] { first, second }, scan.Finished.Select(s => s.Path));
        }

        [Fact]
        public void IgnoresFilesThatAreNotSegments()
        {
            Make("20260920120000", 0);
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "hi");
            File.WriteAllText(Path.Combine(folder, "segment_oops.ts"), "hi");
            File.WriteAllText(Path.Combine(folder, "concat_abc.txt"), "hi");

            var scan = Tracker().Scan(recording: false);

            Assert.Single(scan.Finished);
        }

        [Fact]
        public void AnEmptyFolder_HasNothing()
        {
            var scan = Tracker().Scan(recording: true);

            Assert.Empty(scan.Finished);
            Assert.Null(scan.LivePath);
        }

        [Fact]
        public void Prune_DeletesTheOldestFinishedSegmentsBeyondTheBuffer()
        {
            var files = Enumerable.Range(0, 8).Select(i => Make("20260920120000", i)).ToList();   // 7 finished + 1 live

            int deleted = Tracker().Prune(keepSeconds: 40, recording: true);

            Assert.Equal(3, deleted);   // 70s finished -> keep 40s = 4 segments
            Assert.Equal(new[] { false, false, false, true, true, true, true, true }, files.Select(File.Exists));
        }

        [Fact]
        public void Prune_NeverDeletesTheLiveSegment()
        {
            var files = new[] { Make("20260920120000", 0), Make("20260920120000", 1) };

            Tracker().Prune(keepSeconds: 0, recording: true);

            Assert.False(File.Exists(files[0]));
            Assert.True(File.Exists(files[1]));
        }

        [Fact]
        public void Prune_KeepsEverythingThatFitsInTheBuffer()
        {
            for (int i = 0; i < 5; i++) Make("20260920120000", i);

            Assert.Equal(0, Tracker().Prune(keepSeconds: 120, recording: true));
        }

        [Fact]
        public void Prune_LeavesOtherFilesAlone()
        {
            Make("20260920120000", 0);
            string stranger = Path.Combine(folder, "keep-me.txt");
            File.WriteAllText(stranger, "hi");

            Tracker().Prune(keepSeconds: 0, recording: false);

            Assert.True(File.Exists(stranger));
        }
    }
}
