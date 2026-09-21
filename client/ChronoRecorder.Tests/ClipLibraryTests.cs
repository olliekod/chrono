using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    public sealed class ClipLibraryTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "chrono_lib_" + Guid.NewGuid().ToString("N"));
        private readonly string clips;
        private readonly string index;
        private readonly RecorderConfig config;

        public ClipLibraryTests()
        {
            clips = Path.Combine(root, "clips");
            index = Path.Combine(root, "settings", "library.json");
            Directory.CreateDirectory(clips);
            config = new RecorderConfig { OutputFolder = clips };
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        private ClipLibrary NewLibrary() => new(config, index);

        /// <summary>A file that looks like a finished clip: old enough that a save can't still be writing it.</summary>
        private string Clip(string name, int bytes = 100, int secondsOld = 60)
        {
            string path = Path.Combine(clips, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-secondsOld));
            return path;
        }

        [Fact]
        public void RefreshFindsClipsAlreadyInTheFolder()
        {
            Clip("Quick Clip_2026-09-20_19-40-21.mp4", 500);
            var library = NewLibrary();

            library.Refresh();

            var clip = Assert.Single(library.Snapshot());
            Assert.Equal("Quick Clip", clip.Title);
            Assert.Equal(500, clip.SizeBytes);
        }

        [Fact]
        public void RefreshLeavesOutFilesStillBeingWrittenAndTemporaryFiles()
        {
            Clip("done.mp4");
            Clip("just-now.mp4", secondsOld: 0);   // a clip that is still being saved: not listed until it settles
            Clip("concat_abc.mp4");
            Clip("trimming.abc.part.mp4");
            File.WriteAllText(Path.Combine(clips, "notes.txt"), "x");
            var library = NewLibrary();

            library.Refresh();

            Assert.Equal(new[] { "done.mp4" }, library.Snapshot().Select(c => c.FileName));
        }

        [Fact]
        public void ARefreshRightAfterATrimReplacedTheFile_KeepsTheClipAndItsTitle()
        {
            string path = Clip("Quick Clip_2026-09-20_19-57-08.mp4", 500);
            var library = NewLibrary();
            library.Refresh();
            string id = library.Snapshot().Single().Id;
            library.Rename(id, "Triple kill");

            File.WriteAllBytes(path, new byte[200]);          // the trim wrote a smaller file, just now
            library.Refresh();

            var clip = Assert.Single(library.Snapshot());
            Assert.Equal(id, clip.Id);
            Assert.Equal("Triple kill", clip.Title);
            Assert.Equal(200, clip.SizeBytes);
        }

        [Fact]
        public void ARenameSurvivesRestartingTheApp()
        {
            Clip("a.mp4");
            var first = NewLibrary();
            first.Refresh();
            string id = first.Snapshot().Single().Id;

            Assert.True(first.Rename(id, "  Triple kill  on the boss "));

            var second = NewLibrary();   // as after a restart
            second.Refresh();
            var clip = Assert.Single(second.Snapshot());
            Assert.Equal(id, clip.Id);
            Assert.Equal("Triple kill on the boss", clip.Title);
        }

        [Fact]
        public void ABlankTitle_FallsBackToTheFileName()
        {
            Clip("Quick Clip_2026-09-20_19-40-21.mp4");
            var library = NewLibrary();
            library.Refresh();
            string id = library.Snapshot().Single().Id;

            library.Rename(id, "   ");

            Assert.Equal("Quick Clip", library.Find(id)!.Title);
        }

        [Fact]
        public void UploadedClips_KeepTheirLinkAcrossRefreshesAndRestarts()
        {
            Clip("a.mp4");
            var first = NewLibrary();
            first.Refresh();
            string id = first.Snapshot().Single().Id;

            Assert.True(first.MarkUploaded(id, "https://clips.test/watch/abc123", "abc123"));

            var second = NewLibrary();
            second.Refresh();
            var clip = second.Snapshot().Single();
            Assert.True(clip.IsUploaded);
            Assert.Equal("https://clips.test/watch/abc123", clip.Link);
            Assert.Equal("abc123", clip.RemoteId);
            Assert.NotNull(clip.UploadedUtc);
        }

        [Fact]
        public void AClipRemovedFromTheFolderByHand_LeavesTheLibrary()
        {
            string path = Clip("a.mp4");
            Clip("b.mp4");
            var library = NewLibrary();
            library.Refresh();

            File.Delete(path);
            library.Refresh();

            Assert.Equal(new[] { "b.mp4" }, library.Snapshot().Select(c => c.FileName));
        }

        [Fact]
        public void AddingASavedClip_ThenRefreshing_DoesNotDuplicateIt()
        {
            string path = Clip("Quick Clip_2026-09-20_21-14-00.mp4", 300);
            var library = NewLibrary();

            var added = library.AddSaved(path, "Risk of rain 2", "Risk of rain 2 - Sep 20, 9:14 PM");
            library.Refresh();

            var clip = Assert.Single(library.Snapshot());
            Assert.Equal(added.Id, clip.Id);
            Assert.Equal("Risk of rain 2 - Sep 20, 9:14 PM", clip.Title);
            Assert.Equal("Risk of rain 2", clip.Game);
        }

        [Fact]
        public void ADamagedIndex_DoesNotLoseClips_OnlyTheirTitles()
        {
            Clip("a.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(index)!);
            File.WriteAllText(index, "{ this is not json");

            var library = NewLibrary();
            library.Refresh();

            Assert.Equal(new[] { "a.mp4" }, library.Snapshot().Select(c => c.FileName));
            Assert.True(File.Exists(index + ".damaged"));
        }

        [Fact]
        public void ChangesRaiseTheChangedEvent()
        {
            Clip("a.mp4");
            var library = NewLibrary();
            int raised = 0;
            library.Changed += () => raised++;

            library.Refresh();                                   // finds a clip
            library.Rename(library.Snapshot().Single().Id, "New name");

            Assert.Equal(2, raised);
        }

        [Fact]
        public void SnapshotsAreCopies_SoEditingOneChangesNothing()
        {
            Clip("a.mp4");
            var library = NewLibrary();
            library.Refresh();

            library.Snapshot().Single().Title = "tampered";

            Assert.NotEqual("tampered", library.Snapshot().Single().Title);
        }

        [Fact]
        public void TheIndexIsWrittenAtomically_NoTemporaryFileLeftBehind()
        {
            Clip("a.mp4");
            var library = NewLibrary();
            library.Refresh();

            Assert.True(File.Exists(index));
            Assert.False(File.Exists(index + ".tmp"));
        }

        [Fact]
        public void PathsAreRelativeToTheClipsFolder_SoMovingTheFolderKeepsTheLibrary()
        {
            Clip("a.mp4");
            var library = NewLibrary();
            library.Refresh();

            var clip = library.Snapshot().Single();

            Assert.Equal("a.mp4", clip.FileName);
            Assert.Equal(Path.Combine(clips, "a.mp4"), library.PathOf(clip));
        }
    }
}
