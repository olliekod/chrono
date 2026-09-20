using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ChronoRecorder
{
    public sealed record SegmentScan(IReadOnlyList<SegmentSpan> Finished, string? LivePath);

    /// <summary>
    /// Keeps track of the segment files FFmpeg's segment muxer writes, by reading the folder.
    /// Files are named <c>segment_{run}_{index}.ts</c>: <c>run</c> is a timestamp taken when recording
    /// started, so files from before a stop/start sort before the new ones.
    /// </summary>
    public sealed class SegmentTracker
    {
        private static readonly Regex NameFormat = new(@"^segment_(\d{14})_(\d{5})\.ts$", RegexOptions.Compiled);

        private readonly string folder;
        private readonly double nominalSeconds;
        private readonly Func<string, double?> probe;
        private readonly Dictionary<string, double?> measured = new();

        /// <param name="nominalSeconds">Length of a normal segment. FFmpeg splits on this exactly.</param>
        /// <param name="probe">Measures a file's real length (ffprobe). Only used for a run's last file.</param>
        public SegmentTracker(string folder, double nominalSeconds, Func<string, double?> probe)
        {
            this.folder = folder;
            this.nominalSeconds = nominalSeconds;
            this.probe = probe;
        }

        public static string NewRunId() => DateTime.Now.ToString("yyyyMMddHHmmss");

        /// <summary>The output pattern to hand to FFmpeg's segment muxer.</summary>
        public static string NamePattern(string folder, string run) => Path.Combine(folder, $"segment_{run}_%05d.ts");

        private sealed record Entry(string Path, string Run, int Index);

        private List<Entry> ListFiles()
        {
            if (!Directory.Exists(folder)) return new List<Entry>();

            return Directory.EnumerateFiles(folder, "segment_*.ts")
                .Select(path => (path, match: NameFormat.Match(Path.GetFileName(path))))
                .Where(x => x.match.Success)
                .Select(x => new Entry(x.path, x.match.Groups[1].Value, int.Parse(x.match.Groups[2].Value)))
                .OrderBy(e => e.Run, StringComparer.Ordinal)
                .ThenBy(e => e.Index)
                .ToList();
        }

        /// <summary>
        /// Splits the files into finished segments (oldest first) and the one FFmpeg is writing now.
        /// While recording, the newest file is live, but only if it belongs to the current run. Just after a
        /// restart the newest file on disk may be the previous run's last one, which is finished.
        /// </summary>
        public SegmentScan Scan(bool recording, string? currentRun = null)
        {
            var files = ListFiles();
            string? livePath = null;

            if (recording && files.Count > 0)
            {
                var newest = files[^1];
                if (currentRun == null || newest.Run == currentRun)
                {
                    livePath = newest.Path;
                    files.RemoveAt(files.Count - 1);
                }
            }

            var finished = new List<SegmentSpan>(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                // Every file but a run's last was closed on a boundary, so it's the nominal length.
                // A run's last file was cut short whenever recording stopped, so it has to be measured.
                bool lastOfRun = i == files.Count - 1 || files[i + 1].Run != files[i].Run;
                finished.Add(new SegmentSpan(files[i].Path, lastOfRun ? Measure(files[i].Path) : nominalSeconds));
            }

            return new SegmentScan(finished, livePath);
        }

        private double Measure(string path)
        {
            if (!measured.TryGetValue(path, out double? seconds))
            {
                seconds = probe(path);
                measured[path] = seconds;
            }
            return seconds is > 0 ? seconds.Value : nominalSeconds;
        }

        /// <summary>Deletes the oldest finished segments until no more than <paramref name="keepSeconds"/> remain.</summary>
        public int Prune(double keepSeconds, bool recording, string? currentRun = null)
        {
            var finished = Scan(recording, currentRun).Finished;
            double total = finished.Sum(s => s.DurationSeconds);
            int deleted = 0;

            foreach (var segment in finished)
            {
                if (total <= keepSeconds) break;

                try
                {
                    File.Delete(segment.Path);
                    measured.Remove(segment.Path);
                    total -= segment.DurationSeconds;
                    deleted++;
                }
                catch (IOException)
                {
                    break;   // still in use; try again on the next prune
                }
            }

            return deleted;
        }
    }
}
