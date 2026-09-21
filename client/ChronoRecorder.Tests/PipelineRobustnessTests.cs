using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    /// <summary>
    /// The rules that keep a clip whole: run ids that can't collide, segments measured only when they might be
    /// short, and an encoder that is told whether it is racing a game or saving a file.
    /// </summary>
    public class PipelineRobustnessTests
    {
        // ------------------------------------------------------------------ run ids

        [Fact]
        public void EveryRunIdIsNew_EvenWhenRunsStartInTheSameSecond()
        {
            // A game switch stops and starts in one breath, and the GDI fallback restarts the moment Desktop
            // Duplication fails. Two runs sharing an id would have the second overwrite the first's files, since
            // the segment muxer numbers every run from zero, and a clip could then run across both.
            var ids = Enumerable.Range(0, 50).Select(_ => SegmentTracker.NewRunId()).ToList();

            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal).ToList(), ids);   // still sort oldest first
            Assert.All(ids, id => Assert.Matches("^[0-9]{14}$", id));
        }

        // ---------------------------------------------------------------- measuring

        private sealed class Folder : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chrono-pipeline-{Guid.NewGuid():N}");
            public List<string> Probed { get; } = new();

            public Folder() => Directory.CreateDirectory(Path);
            public void Dispose() { try { Directory.Delete(Path, true); } catch { } }

            public void Make(string run, int index)
                => File.WriteAllBytes(System.IO.Path.Combine(Path, $"segment_{run}_{index:D5}.ts"), new byte[] { 1 });

            public SegmentTracker Tracker() => new(Path, 10, path =>
            {
                Probed.Add(System.IO.Path.GetFileName(path));
                return null;
            });
        }

        [Fact]
        public void TheCurrentRunsFinishedSegments_AreNotMeasured()
        {
            // Measuring starts an FFmpeg process. Only a run's last file can be short, and while a run is recording
            // its last file is the one being written, so everything behind it is a whole segment. Without this the
            // recorder measured a fresh 10-second file every 10 seconds, for the whole recording.
            using var f = new Folder();
            f.Make("20260920120000", 0);
            f.Make("20260920120000", 1);
            f.Make("20260920120000", 2);

            var scan = f.Tracker().Scan(recording: true, currentRun: "20260920120000");

            Assert.Empty(f.Probed);
            Assert.All(scan.Finished, s => Assert.Equal(10, s.DurationSeconds));
        }

        [Fact]
        public void AnEarlierRunsLastSegment_IsStillMeasured_WhileRecording()
        {
            using var f = new Folder();
            f.Make("20260920120000", 0);   // the run before this one stopped here, probably mid-segment
            f.Make("20260920130000", 0);
            f.Make("20260920130000", 1);

            f.Tracker().Scan(recording: true, currentRun: "20260920130000");

            Assert.Equal(new[] { "segment_20260920120000_00000.ts" }, f.Probed);
        }

        // ------------------------------------------------------------ encoder speed

        [Fact]
        public void Recording_UsesTheFastestSoftwarePreset()
        {
            Assert.Contains("-preset ultrafast", EncoderProfile.BuildArgs("libx264", 8000, 60, EncodeSpeed.Live));
        }

        [Fact]
        public void SavingAClip_UsesABetterSoftwarePreset_BecauseNothingIsWaitingOnIt()
        {
            // ultrafast turns off most of what x264 does to save bits, so the same bitrate looks worse. A clip that
            // is already recorded can afford a few more seconds.
            string args = EncoderProfile.BuildArgs("libx264", 8000, 60, EncodeSpeed.Save);

            Assert.Contains("-preset veryfast", args);
            Assert.DoesNotContain("ultrafast", args);
        }

        [Fact]
        public void TheSavePresetNeverChangesTheBitrateRules()
        {
            string live = EncoderProfile.BuildArgs("libx264", 12000, 60, EncodeSpeed.Live);
            string save = EncoderProfile.BuildArgs("libx264", 12000, 60, EncodeSpeed.Save);

            foreach (var expected in new[] { "-b:v 12000k", "-maxrate 18000k", "-bufsize 24000k", "-g 120" })
            {
                Assert.Contains(expected, live);
                Assert.Contains(expected, save);
            }
        }

        [Fact]
        public void ExportsAndTrims_AskForTheSaveQuality()
        {
            var export = new ExportRequest("list.txt", "out.mp4", 0, 20, ExportMode.Cpu,
                new Size(1920, 1080), "libx264", 12000, 60);
            Assert.Contains("-preset veryfast", ExportCommand.Build(export));

            var trim = new ClipMediaCommands.TrimRequest("in.mp4", "out.mp4", 1, 11, "libx264", 12000, 60, GpuDecode: false);
            Assert.Contains("-preset veryfast", ClipMediaCommands.Trim(trim));
        }

        [Fact]
        public void CapturingStillUsesTheLivePreset()
        {
            var request = new CaptureRequest("libx264", 60, 12000,
                new CaptureSource(CaptureMethod.Gdi, 0, new Rectangle(0, 0, 1920, 1080)),
                10, @"C:\temp\segment_%05d.ts");

            Assert.Contains("-preset ultrafast", CaptureCommand.Build(request));
        }
    }
}
