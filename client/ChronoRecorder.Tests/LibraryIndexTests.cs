using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class LibraryIndexTests
    {
        private static int counter;
        private static string NewId() => "id" + (++counter);

        private static DiskFile File(string name, long size = 1000, int minutesAgo = 0)
            => new(name, size, new DateTime(2026, 9, 20, 20, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo));

        private static ClipRecord Record(string id, string name, string title, int minutesAgo = 0, string? link = null)
            => new() { Id = id, FileName = name, Title = title, CreatedUtc = new DateTime(2026, 9, 20, 20, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo), Link = link };

        // --------------------------------------------------------------- merging

        [Fact]
        public void AFileNotInTheLibrary_IsAddedWithATitleFromItsName()
        {
            var merged = LibraryIndex.Merge(new ClipRecord[0], new[] { File("Quick Clip_2026-09-20_19-40-21.mp4") }, NewId);

            var clip = Assert.Single(merged);
            Assert.Equal("Quick Clip", clip.Title);
            Assert.Equal("Quick Clip_2026-09-20_19-40-21.mp4", clip.FileName);
            Assert.False(string.IsNullOrEmpty(clip.Id));
        }

        [Fact]
        public void ASavedRecord_KeepsItsTitleAndLink_WhileItsFileExists()
        {
            var saved = new[] { Record("a", "one.mp4", "My best clip", link: "https://x.test/watch/abc") };

            var merged = LibraryIndex.Merge(saved, new[] { File("one.mp4", size: 5555) }, NewId);

            var clip = Assert.Single(merged);
            Assert.Equal("My best clip", clip.Title);
            Assert.Equal("https://x.test/watch/abc", clip.Link);
            Assert.Equal(5555, clip.SizeBytes);   // the size is refreshed from the disk
        }

        [Fact]
        public void ARecordWhoseFileIsGone_IsDropped()
        {
            var saved = new[] { Record("a", "gone.mp4", "Gone"), Record("b", "here.mp4", "Here") };

            var merged = LibraryIndex.Merge(saved, new[] { File("here.mp4") }, NewId);

            Assert.Equal(new[] { "here.mp4" }, merged.Select(c => c.FileName));
        }

        [Fact]
        public void FileNamesAreMatchedIgnoringCase()
        {
            var saved = new[] { Record("a", "Clip.MP4", "Named") };

            var merged = LibraryIndex.Merge(saved, new[] { File("clip.mp4") }, NewId);

            Assert.Equal("Named", Assert.Single(merged).Title);
        }

        [Fact]
        public void TheNewestClipComesFirst()
        {
            var saved = new[] { Record("a", "old.mp4", "Old", minutesAgo: 90), Record("b", "new.mp4", "New", minutesAgo: 1) };

            var merged = LibraryIndex.Merge(saved, new[] { File("old.mp4"), File("new.mp4"), File("mid.mp4", minutesAgo: 30) }, NewId);

            Assert.Equal(new[] { "new.mp4", "mid.mp4", "old.mp4" }, merged.Select(c => c.FileName));
        }

        [Fact]
        public void DuplicateEntriesForOneFile_CollapseToOne()
        {
            var saved = new[] { Record("a", "one.mp4", "First"), Record("b", "one.mp4", "Second") };

            Assert.Single(LibraryIndex.Merge(saved, new[] { File("one.mp4") }, NewId));
        }

        // ------------------------------------------------------------- names and titles

        [Theory]
        [InlineData("Quick Clip_2026-09-20_19-40-21.mp4", "Quick Clip")]
        [InlineData("Risk of rain 2_2026-09-20_19-40-21.mp4", "Risk of rain 2")]
        [InlineData("Quick Clip_2026-09-20_19-40-21_2.mp4", "Quick Clip")]      // a name made unique with a counter
        [InlineData("holiday video.mp4", "holiday video")]
        [InlineData("_2026-09-20_19-40-21.mp4", "_2026-09-20_19-40-21")]        // nothing before the stamp: keep the stem
        public void TitlesComeFromFileNames(string file, string expected)
        {
            Assert.Equal(expected, LibraryIndex.TitleFromFileName(file));
        }

        [Fact]
        public void TheTimeInAHotkeyFileName_IsReadAsLocalTime()
        {
            var utc = LibraryIndex.CreatedFromFileName("Quick Clip_2026-09-20_19-40-21.mp4");

            Assert.NotNull(utc);
            var local = utc!.Value.ToLocalTime();
            Assert.Equal(new DateTime(2026, 9, 20, 19, 40, 21), local);
            Assert.Null(LibraryIndex.CreatedFromFileName("holiday video.mp4"));
        }

        [Fact]
        public void ADefaultTitleNamesTheGameAndTime()
        {
            var when = new DateTime(2026, 9, 20, 21, 14, 0);

            Assert.Equal("Risk of rain 2 - Sep 20, 9:14 PM", LibraryIndex.DefaultTitle("Risk of rain 2", "Quick Clip", when));
            Assert.Equal("Quick Clip - Sep 20, 9:14 PM", LibraryIndex.DefaultTitle(null, "Quick Clip", when));
            Assert.Equal("Clip - Sep 20, 9:14 PM", LibraryIndex.DefaultTitle(" ", "", when));
        }

        [Theory]
        [InlineData("  Triple   kill \t on the boss ", "Triple kill on the boss")]
        [InlineData("line one\nline two", "line one line two")]
        [InlineData("", "fallback")]
        [InlineData("   ", "fallback")]
        [InlineData(null, "fallback")]
        public void TitlesAreTidied(string? input, string expected)
        {
            Assert.Equal(expected, LibraryIndex.CleanTitle(input, "fallback"));
        }

        [Fact]
        public void ALongTitle_IsCutToTheLimit_WithoutATrailingSpace()
        {
            string cut = LibraryIndex.CleanTitle(new string('a', 99) + " " + new string('b', 50), "x");

            Assert.True(cut.Length <= LibraryIndex.MaxTitleLength);
            Assert.False(cut.EndsWith(" "));
        }

        // --------------------------------------------------------- sections and search

        [Fact]
        public void ClipsAreSplitIntoNotUploadedAndUploaded_NewestFirst()
        {
            var clips = new[]
            {
                Record("a", "a.mp4", "A", minutesAgo: 10),
                Record("b", "b.mp4", "B", minutesAgo: 5, link: "https://x.test/watch/b"),
                Record("c", "c.mp4", "C", minutesAgo: 1),
                Record("d", "d.mp4", "D", minutesAgo: 20, link: "https://x.test/watch/d"),
            };

            var (local, uploaded) = LibraryIndex.Split(clips);

            Assert.Equal(new[] { "C", "A" }, local.Select(c => c.Title));
            Assert.Equal(new[] { "B", "D" }, uploaded.Select(c => c.Title));
        }

        [Fact]
        public void SearchMatchesTitleOrGame_IgnoringCase()
        {
            var clip = new ClipRecord { Title = "Triple kill", Game = "Risk of rain 2" };

            Assert.True(LibraryIndex.Matches(clip, "TRIPLE"));
            Assert.True(LibraryIndex.Matches(clip, "risk"));
            Assert.True(LibraryIndex.Matches(clip, ""));
            Assert.True(LibraryIndex.Matches(clip, null));
            Assert.False(LibraryIndex.Matches(clip, "valorant"));
            Assert.False(LibraryIndex.Matches(new ClipRecord { Title = "x" }, "risk"));
        }

        [Theory]
        [InlineData("clip.mp4", true)]
        [InlineData("CLIP.MP4", true)]
        [InlineData("clip.mov", false)]
        [InlineData("concat_abc.mp4", false)]
        [InlineData("~work.mp4", false)]
        [InlineData("clip.part.mp4", false)]
        [InlineData(".hidden.mp4", false)]
        public void OnlyRealClipsAreListed(string name, bool expected)
        {
            Assert.Equal(expected, LibraryIndex.IsClipFile(name));
        }
    }
}
