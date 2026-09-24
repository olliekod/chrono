using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace ChronoRecorder
{
    /// <summary>
    /// Every clip you have saved. The list (titles, links, games) lives in library.json beside the settings; the
    /// videos stay in the clips folder. Whatever is really in that folder is what the library shows, so clips
    /// deleted or added by hand are picked up on the next refresh (<see cref="LibraryIndex.Merge"/>).
    /// Safe to use from any thread.
    /// </summary>
    public sealed class ClipLibrary
    {
        private sealed class IndexFile
        {
            public int Version { get; set; } = 1;
            public List<ClipRecord> Clips { get; set; } = new();
        }

        /// <summary>A file this new is still being written by a save; leave it out until it settles.</summary>
        private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

        private readonly RecorderConfig config;
        private readonly string indexPath;
        private readonly object gate = new object();
        private List<ClipRecord> clips = new();
        private bool loaded;

        /// <summary>Raised (on any thread) after the list of clips or a clip's details changed.</summary>
        public event Action? Changed;

        public static string DefaultIndexPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chrono", "library.json");

        public ClipLibrary(RecorderConfig config, string? indexPath = null)
        {
            this.config = config;
            this.indexPath = indexPath ?? DefaultIndexPath;
        }

        public string ClipsFolder => config.OutputFolder;

        public string PathOf(ClipRecord clip) => Path.Combine(config.OutputFolder, clip.FileName);

        /// <summary>A copy of the clips, newest first. Editing the copies changes nothing.</summary>
        public List<ClipRecord> Snapshot()
        {
            lock (gate)
            {
                EnsureLoaded();
                return clips.Select(Clone).OrderByDescending(c => c.CreatedUtc).ToList();
            }
        }

        public ClipRecord? Find(string id)
        {
            lock (gate)
            {
                EnsureLoaded();
                var found = clips.FirstOrDefault(c => c.Id == id);
                return found == null ? null : Clone(found);
            }
        }

        /// <summary>Compare the saved list with what is in the folder; keep, add and drop clips to match.</summary>
        public void Refresh()
        {
            bool changed;
            lock (gate)
            {
                EnsureLoaded();

                var onDisk = new List<DiskFile>();
                if (Directory.Exists(config.OutputFolder))
                {
                    foreach (var path in Directory.EnumerateFiles(config.OutputFolder, "*.mp4"))
                    {
                        string name = Path.GetFileName(path);
                        if (!LibraryIndex.IsClipFile(name)) continue;

                        var info = new FileInfo(path);
                        bool settling = DateTime.UtcNow - info.LastWriteTimeUtc < SettleTime;
                        onDisk.Add(new DiskFile(name, info.Length, info.CreationTimeUtc, settling));
                    }
                }

                var before = Signature(clips);
                clips = LibraryIndex.Merge(clips, onDisk, NewId);
                changed = before != Signature(clips);
                if (changed) Save();
            }
            if (changed) Changed?.Invoke();
        }

        /// <summary>Add a clip that was just saved (by a hotkey), with the game and title it should have.</summary>
        public ClipRecord AddSaved(string fullPath, string? game, string title)
        {
            ClipRecord added;
            lock (gate)
            {
                EnsureLoaded();
                string name = Path.GetFileName(fullPath);
                var existing = clips.FirstOrDefault(c => c.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) clips.Remove(existing);

                var info = new FileInfo(fullPath);
                added = new ClipRecord
                {
                    Id = existing?.Id ?? NewId(),
                    FileName = name,
                    Title = LibraryIndex.CleanTitle(title, LibraryIndex.TitleFromFileName(name)),
                    Game = string.IsNullOrWhiteSpace(game) ? null : game,
                    CreatedUtc = DateTime.UtcNow,
                    SizeBytes = info.Exists ? info.Length : 0
                };
                clips.Add(added);
                Save();
                added = Clone(added);
            }
            Changed?.Invoke();
            return added;
        }

        public bool Rename(string id, string title)
        {
            return Edit(id, c => c.Title = LibraryIndex.CleanTitle(title, LibraryIndex.TitleFromFileName(c.FileName)));
        }

        public bool MarkUploaded(string id, string link, string remoteId, string? ownerToken = null)
        {
            return Edit(id, c => { c.Link = link; c.RemoteId = remoteId; c.UploadedUtc = DateTime.UtcNow; c.OwnerToken = ownerToken; });
        }

        /// <summary>The uploaded copy is gone from the server: the clip is just a file on this PC again.</summary>
        public bool ClearUpload(string id)
        {
            return Edit(id, c => { c.Link = null; c.RemoteId = null; c.UploadedUtc = null; c.OwnerToken = null; });
        }

        /// <summary>Record what is now true of the file after it was changed (a trim).</summary>
        public bool UpdateDetails(string id, double? duration, string? resolution)
        {
            return Edit(id, c =>
            {
                c.DurationSeconds = duration ?? c.DurationSeconds;
                c.Resolution = resolution ?? c.Resolution;
                var info = new FileInfo(PathOf(c));
                if (info.Exists) c.SizeBytes = info.Length;
            });
        }

        /// <summary>
        /// Fill in length and picture size for clips that don't have them yet (older clips, hand-added files).
        /// One at a time: this is called at startup and again after every saved clip, and two passes together would
        /// start FFmpeg twice for the same files. A call made while one is running is served by that one.
        /// </summary>
        public void FillMissingDetails(CancellationToken ct = default)
        {
            lock (fillGate)
            {
                fillWanted = true;
                if (filling) return;
                filling = true;
            }

            try
            {
                while (true)
                {
                    lock (fillGate)
                    {
                        if (!fillWanted) return;
                        fillWanted = false;
                    }
                    FillMissingDetailsOnce(ct);
                }
            }
            finally
            {
                lock (fillGate) filling = false;
            }
        }

        private readonly object fillGate = new object();
        private bool filling;
        private bool fillWanted;

        private void FillMissingDetailsOnce(CancellationToken ct)
        {
            List<ClipRecord> missing;
            lock (gate) { EnsureLoaded(); missing = clips.Where(c => c.DurationSeconds == null || c.Resolution == null).Select(Clone).ToList(); }

            foreach (var clip in missing)
            {
                if (ct.IsCancellationRequested) return;
                string path = PathOf(clip);
                if (!File.Exists(path)) continue;

                var (duration, size) = MediaProbe.Details(path);
                if (duration == null && size == null) continue;
                UpdateDetails(clip.Id, duration, size);
            }
        }

        /// <summary>What moving to a new clips folder did: how many files made the move, and the names of any that didn't.</summary>
        public sealed record FolderChangeResult(string Folder, int Moved, IReadOnlyList<string> Failed);

        /// <summary>
        /// Points the library at a new folder, moving every clip file there first (new clips are saved there from now
        /// on regardless; <see cref="Recorder.SaveClip"/> just reads <c>OutputFolder</c> fresh each time). Records in
        /// library.json are keyed by file name only, so nothing about them needs to change - the same names just live
        /// in a new place. A file that can't be moved (open in another program right now) is left in the old folder
        /// and reported, rather than guessed at or lost with no record of it: it drops out of the library until it's moved there
        /// by hand, since <see cref="Refresh"/> only ever looks at the current folder.
        /// </summary>
        public FolderChangeResult ChangeFolder(string newFolder)
        {
            string destination = Path.GetFullPath(newFolder);
            Directory.CreateDirectory(destination);

            var failed = new List<string>();
            int moved = 0;

            if (Directory.Exists(config.OutputFolder) && !PathsEqual(config.OutputFolder, destination))
            {
                foreach (var path in Directory.EnumerateFiles(config.OutputFolder, "*.mp4").ToList())
                {
                    string name = Path.GetFileName(path);
                    string dest = Path.Combine(destination, name);
                    if (File.Exists(dest)) { failed.Add(name); continue; }   // a name clash: leave both where they are rather than guess
                    if (TryMove(path, dest)) moved++; else failed.Add(name);
                }
            }

            config.OutputFolder = destination;
            Refresh();
            return new FolderChangeResult(destination, moved, failed);
        }

        private static bool PathsEqual(string a, string b)
            => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        /// <summary>Retries briefly: a clip open in the editor or another program can take Windows a moment to let go of.</summary>
        private static bool TryMove(string from, string to)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(from, to); return true; }
                catch (IOException) when (attempt < 6) { Thread.Sleep(250); }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }

        /// <summary>Delete a clip's video (to the Recycle Bin, so a slip can be undone) and remove it from the library.</summary>
        public bool Delete(string id)
        {
            ClipRecord? clip;
            lock (gate) { EnsureLoaded(); clip = clips.FirstOrDefault(c => c.Id == id); }
            if (clip == null) return false;

            string path = PathOf(clip);
            if (File.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }

            lock (gate)
            {
                clips.RemoveAll(c => c.Id == id);
                Save();
            }
            Changed?.Invoke();
            return true;
        }

        // ------------------------------------------------------------------ internals

        private bool Edit(string id, Action<ClipRecord> change)
        {
            lock (gate)
            {
                EnsureLoaded();
                var clip = clips.FirstOrDefault(c => c.Id == id);
                if (clip == null) return false;
                change(clip);
                Save();
            }
            Changed?.Invoke();
            return true;
        }

        private void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(indexPath)) return;
                var file = JsonConvert.DeserializeObject<IndexFile>(File.ReadAllText(indexPath, Encoding.UTF8));
                clips = file?.Clips ?? new List<ClipRecord>();
            }
            catch (Exception ex)
            {
                // A damaged index must not lose clips: they are rebuilt from the folder, only titles are lost.
                Console.WriteLine($"Couldn't read the library ({ex.Message}); rebuilding it from the clips folder");
                try { File.Move(indexPath, indexPath + ".damaged", overwrite: true); } catch { }
                clips = new List<ClipRecord>();
            }
        }

        /// <summary>Written to a temporary file first, so a crash mid-write can't leave a half-written library.</summary>
        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
                string temp = indexPath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(new IndexFile { Clips = clips }, Formatting.Indented), new UTF8Encoding(false));
                File.Move(temp, indexPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Couldn't save the library: {ex.Message}");
            }
        }

        private static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        private static ClipRecord Clone(ClipRecord c) => new()
        {
            Id = c.Id, FileName = c.FileName, Title = c.Title, Game = c.Game, CreatedUtc = c.CreatedUtc,
            DurationSeconds = c.DurationSeconds, SizeBytes = c.SizeBytes, Resolution = c.Resolution,
            Link = c.Link, RemoteId = c.RemoteId, UploadedUtc = c.UploadedUtc, OwnerToken = c.OwnerToken
        };

        private static string Signature(IEnumerable<ClipRecord> list)
            => string.Join("|", list.Select(c => $"{c.Id}:{c.FileName}:{c.SizeBytes}:{c.Title}"));
    }
}
