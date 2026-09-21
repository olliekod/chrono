using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ChronoRecorder
{
    /// <summary>An mp4 found in the clips folder. <paramref name="Settling"/>: it was written a moment ago and may still be growing.</summary>
    public sealed record DiskFile(string FileName, long SizeBytes, DateTime CreatedUtc, bool Settling = false);

    /// <summary>
    /// The rules of the library, kept apart from files and windows so they can be tested: which clips exist (the saved
    /// list checked against what is really in the folder), what to call a clip nobody named, and how titles are cleaned.
    /// </summary>
    public static class LibraryIndex
    {
        public const int MaxTitleLength = 100;

        // "Quick Clip_2026-09-20_19-40-21.mp4": the name a hotkey gives, then the local time it was saved.
        private static readonly Regex Stamped = new(@"^(?<name>.*?)_(?<stamp>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(?:_\d+)?$", RegexOptions.Compiled);

        /// <summary>
        /// The clips that exist now, newest first: saved records whose file is still there (their title, link and the
        /// rest kept), plus a new record for every file that isn't in the saved list yet, such as clips from before the
        /// library existed or dropped into the folder by hand. Records whose file is gone are dropped.
        /// </summary>
        public static List<ClipRecord> Merge(IEnumerable<ClipRecord> saved, IEnumerable<DiskFile> onDisk, Func<string> newId)
        {
            var files = new Dictionary<string, DiskFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in onDisk) files[file.FileName] = file;

            var result = new List<ClipRecord>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in saved)
            {
                if (!files.TryGetValue(record.FileName, out var file)) continue;   // deleted outside Chrono
                if (!known.Add(record.FileName)) continue;                         // a duplicate entry
                record.SizeBytes = file.SizeBytes;
                result.Add(record);
            }

            foreach (var file in files.Values)
            {
                if (known.Contains(file.FileName)) continue;
                // A file that is still being written is not a clip yet. (A clip already in the library is never dropped for
                // being new: a trim replaces the file, and a fresh file must not make the clip look deleted.)
                if (file.Settling) continue;
                result.Add(new ClipRecord
                {
                    Id = newId(),
                    FileName = file.FileName,
                    Title = TitleFromFileName(file.FileName),
                    CreatedUtc = CreatedFromFileName(file.FileName) ?? file.CreatedUtc,
                    SizeBytes = file.SizeBytes
                });
            }

            return result.OrderByDescending(r => r.CreatedUtc).ThenBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"Quick Clip_2026-09-20_19-40-21.mp4" becomes "Quick Clip"; any other name loses only its extension.</summary>
        public static string TitleFromFileName(string fileName)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            var match = Stamped.Match(stem);
            string title = match.Success && match.Groups["name"].Value.Trim().Length > 0 ? match.Groups["name"].Value : stem;
            return CleanTitle(title, "Clip");
        }

        /// <summary>The time in a hotkey-style file name, as UTC, or null.</summary>
        public static DateTime? CreatedFromFileName(string fileName)
        {
            var match = Stamped.Match(System.IO.Path.GetFileNameWithoutExtension(fileName));
            if (!match.Success) return null;
            if (!DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                return null;
            return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        }

        /// <summary>
        /// What a new clip is called until its owner names it: "Risk of rain 2 - Sep 20, 9:14 PM". Without a known game
        /// the hotkey's name stands in ("Quick Clip - Sep 20, 9:14 PM").
        /// </summary>
        public static string DefaultTitle(string? game, string? hotkeyName, DateTime savedLocal)
        {
            string what = !string.IsNullOrWhiteSpace(game) ? game! : !string.IsNullOrWhiteSpace(hotkeyName) ? hotkeyName! : "Clip";
            string when = savedLocal.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);
            return CleanTitle($"{what} - {when}", "Clip");
        }

        /// <summary>A title fit to store and show: control characters gone, spaces collapsed, ends trimmed, at most 100 characters.</summary>
        public static string CleanTitle(string? title, string fallback)
        {
            var sb = new StringBuilder();
            foreach (char c in title ?? "") sb.Append(char.IsControl(c) ? ' ' : c);

            string cleaned = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            if (cleaned.Length > MaxTitleLength) cleaned = cleaned.Substring(0, MaxTitleLength).TrimEnd();
            return cleaned.Length == 0 ? fallback : cleaned;
        }

        /// <summary>Clips split for the library: still on this PC only, and already uploaded (each newest first).</summary>
        public static (List<ClipRecord> OnThisPc, List<ClipRecord> Uploaded) Split(IEnumerable<ClipRecord> clips)
        {
            var ordered = clips.OrderByDescending(c => c.CreatedUtc).ToList();
            return (ordered.Where(c => !c.IsUploaded).ToList(), ordered.Where(c => c.IsUploaded).ToList());
        }

        /// <summary>Case-insensitive match on title or game, for the library's search box. Empty matches everything.</summary>
        public static bool Matches(ClipRecord clip, string? query)
        {
            string q = (query ?? "").Trim();
            if (q.Length == 0) return true;
            return clip.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (clip.Game?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        /// <summary>Files that are clips: mp4s, not the temporary files Chrono makes while working.</summary>
        public static bool IsClipFile(string fileName)
        {
            if (!fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return false;
            return !fileName.StartsWith(".", StringComparison.Ordinal) && !fileName.StartsWith("~", StringComparison.Ordinal)
                && !fileName.StartsWith("concat_", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase);
        }
    }
}
