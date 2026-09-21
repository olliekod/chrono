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
        /// <param name="probe">Measures a file's real length (ffmpeg -i). Only used for a run's last file.</param>
        public SegmentTracker(string folder, double nominalSeconds, Func<string, double?> probe)
        {
            this.folder = folder;
            this.nominalSeconds = nominalSeconds;
            this.probe = probe;
        }

        private static readonly object runIdGate = new object();
        private static DateTime lastRunStamp = DateTime.MinValue;

        /// <summary>
        /// An id for a recording run, which also orders its files. Two runs must never share one: the segment muxer
        /// numbers each run's files from zero, so a shared id would have the new run overwrite the old run's files and
        /// let a clip mix footage from both. A restart in the same second is normal (a game switch, or the GDI
        /// fallback starting straight after Desktop Duplication fails), so a repeated stamp is pushed a second on.
        /// </summary>
        public static string NewRunId()
        {
            lock (runIdGate)
            {
                var stamp = DateTime.Now;
                // Seconds are the resolution of the id, so compare at that resolution.
                stamp = new DateTime(stamp.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, stamp.Second, stamp.Kind);
                if (stamp <= lastRunStamp) stamp = lastRunStamp.AddSeconds(1);
                lastRunStamp = stamp;
                return stamp.ToString("yyyyMMddHHmmss");
            }
        }

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
            string? liveRun = null;

            if (recording && files.Count > 0)
            {
                var newest = files[^1];
                if (currentRun == null || newest.Run == currentRun)
                {
                    livePath = newest.Path;
                    liveRun = newest.Run;
                    files.RemoveAt(files.Count - 1);
                }
            }

            // The run being recorded right now. Its files, the live one aside, were all closed on a boundary.
            string? activeRun = recording ? currentRun ?? liveRun : null;

            var finished = new List<SegmentSpan>(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                // Every file but a run's last was closed on a boundary, so it's the nominal length. A run's last file
                // was cut short when that recording stopped, so it has to be measured, which costs an FFmpeg run.
                // The current run's last finished file needs none of that: the live file follows it, so it is whole.
                bool lastOfRun = i == files.Count - 1 || files[i + 1].Run != files[i].Run;
                bool runEnded = files[i].Run != activeRun;
                finished.Add(new SegmentSpan(files[i].Path, lastOfRun && runEnded ? Measure(files[i].Path) : nominalSeconds));
            }

            return new SegmentScan(finished, livePath);
        }

        private double Measure(string path)
        {
            // Scan runs on the hotkey, the UI and the prune timer at once, so the cache needs guarding. The probe
            // itself stays outside the lock: it spawns FFmpeg, and holding a lock across that would stall a save.
            bool known;
            double? seconds;
            lock (measured) known = measured.TryGetValue(path, out seconds);

            if (!known)
            {
                seconds = probe(path);
                lock (measured) measured[path] = seconds;
            }

            return seconds is > 0 ? seconds.Value : nominalSeconds;
        }

        /// <summary>The newest file that belongs to one run, or null if it has none.</summary>
        public string? NewestFileOf(string run) => ListFiles().Where(e => e.Run == run).Select(e => e.Path).LastOrDefault();

        /// <summary>Forget a file's measured length, because the file has been changed.</summary>
        public void Forget(string path)
        {
            lock (measured) measured.Remove(path);
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
                    lock (measured) measured.Remove(segment.Path);
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
