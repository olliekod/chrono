using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;

namespace ChronoRecorder
{
    /// <summary>A strip of frames for the trim timeline: the picture's file (in the thumbnails folder) and how many tiles it has.</summary>
    public sealed record Filmstrip(string FileName, int Frames);

    /// <summary>
    /// What the library does with the video files: card pictures, the trim timeline's strip of frames, and trimming.
    /// Pictures live in %LOCALAPPDATA%\Chrono\thumbs and are named after the clip's id and when its file last changed,
    /// so a trimmed clip gets fresh pictures and the old ones are removed.
    /// </summary>
    public sealed class ClipMedia
    {
        private const int PosterWidth = 480;
        private const int StripTileHeight = 90;

        private readonly RecorderConfig config;
        private readonly ClipLibrary library;
        private readonly Func<string> resolveEncoder;
        private readonly SemaphoreSlim slots = new SemaphoreSlim(2);   // a couple of FFmpeg runs at a time, not a stampede

        public string ThumbsFolder { get; }

        public ClipMedia(RecorderConfig config, ClipLibrary library, Func<string> resolveEncoder, string? thumbsFolder = null)
        {
            this.config = config;
            this.library = library;
            this.resolveEncoder = resolveEncoder;
            ThumbsFolder = thumbsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chrono", "thumbs");
            Directory.CreateDirectory(ThumbsFolder);
        }

        // ------------------------------------------------------------------ pictures

        /// <summary>The card picture's file name (inside <see cref="ThumbsFolder"/>), made if needed; null if it can't be made.</summary>
        public string? Poster(ClipRecord clip)
        {
            string video = library.PathOf(clip);
            if (!File.Exists(video)) return null;

            string name = $"{clip.Id}_{Version(video)}.jpg";
            string path = Path.Combine(ThumbsFolder, name);
            if (File.Exists(path)) return name;

            slots.Wait();
            try
            {
                if (File.Exists(path)) return name;
                RemoveStale(clip.Id, keepPrefix: $"{clip.Id}_{Version(video)}");

                double? duration = clip.DurationSeconds ?? MediaProbe.DurationSeconds(video);
                var (ok, error) = FfmpegRunner.Run(ClipMediaCommands.Poster(video, path, ClipMediaCommands.PosterTime(duration), PosterWidth), 30_000);
                if (!ok) Console.WriteLine($"Couldn't make a picture for {clip.FileName}: {error}");
                return ok && File.Exists(path) ? name : null;
            }
            finally { slots.Release(); }
        }

        /// <summary>The strip of frames for the trim timeline, made if needed.</summary>
        public Filmstrip? Filmstrip(ClipRecord clip)
        {
            string video = library.PathOf(clip);
            if (!File.Exists(video)) return null;

            double duration = clip.DurationSeconds ?? MediaProbe.DurationSeconds(video) ?? 0;
            if (duration <= 0) return null;

            int frames = ClipMediaCommands.FilmstripFrames(duration);
            string name = $"{clip.Id}_{Version(video)}_strip.jpg";
            string path = Path.Combine(ThumbsFolder, name);
            if (File.Exists(path)) return new Filmstrip(name, frames);

            slots.Wait();
            try
            {
                if (File.Exists(path)) return new Filmstrip(name, frames);

                var size = ParseSize(clip.Resolution ?? MediaProbe.VideoSizeText(video));
                var tile = ClipMediaCommands.TileSize(size, StripTileHeight);
                var (ok, error) = FfmpegRunner.Run(ClipMediaCommands.Filmstrip(video, path, duration, tile), 60_000);
                if (!ok) Console.WriteLine($"Couldn't make the film strip for {clip.FileName}: {error}");
                return ok && File.Exists(path) ? new Filmstrip(name, frames) : null;
            }
            finally { slots.Release(); }
        }

        // ------------------------------------------------------------------- trimming

        /// <summary>
        /// Cut a clip to [start, end] seconds. <paramref name="saveAsCopy"/> makes a new clip and leaves the original;
        /// otherwise the original is replaced. An uploaded clip is always saved as a copy, because the uploaded version
        /// is what its link shows and can't be changed by editing the file here.
        /// </summary>
        public ClipRecord Trim(string id, double start, double end, bool saveAsCopy)
        {
            var clip = library.Find(id) ?? throw new InvalidOperationException("That clip isn't in the library any more.");
            string source = library.PathOf(clip);
            if (!File.Exists(source)) throw new FileNotFoundException("The clip's video file is missing.", source);

            if (clip.IsUploaded) saveAsCopy = true;

            double length = clip.DurationSeconds ?? MediaProbe.DurationSeconds(source) ?? end;
            start = Math.Max(0, start);
            end = Math.Min(end, length);

            string folder = Path.GetDirectoryName(source)!;
            string temp = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(source)}.{Guid.NewGuid():N}.part.mp4");
            string encoder = resolveEncoder();
            int bitrate = BitrateSizing.Resolve(config.Bitrate, clip.Resolution, config.Fps);

            slots.Wait();
            try
            {
                // NVIDIA's decoder is quicker; if it refuses this file, decode in software.
                bool gpu = encoder == "h264_nvenc";
                var (ok, error) = FfmpegRunner.Run(ClipMediaCommands.Trim(new ClipMediaCommands.TrimRequest(source, temp, start, end, encoder, bitrate, config.Fps, gpu)));
                if (!ok && gpu)
                {
                    Console.WriteLine($"GPU trim failed ({error}); trying again in software");
                    TryDelete(temp);
                    (ok, error) = FfmpegRunner.Run(ClipMediaCommands.Trim(new ClipMediaCommands.TrimRequest(source, temp, start, end, encoder, bitrate, config.Fps, false)));
                }
                if (!ok)
                {
                    TryDelete(temp);
                    throw new InvalidOperationException($"Couldn't trim the clip. {error}");
                }
            }
            finally { slots.Release(); }

            double? newLength = MediaProbe.DurationSeconds(temp);

            if (saveAsCopy)
            {
                string finalPath = UniquePath(folder, Path.GetFileNameWithoutExtension(source) + "_trim");
                File.Move(temp, finalPath);
                var added = library.AddSaved(finalPath, clip.Game, LibraryIndex.CleanTitle(clip.Title + " (trimmed)", "Clip (trimmed)"));
                library.UpdateDetails(added.Id, newLength, clip.Resolution);
                return library.Find(added.Id)!;
            }

            MoveOver(temp, source);
            RemovePictures(id);   // the old ones show the untrimmed clip
            library.UpdateDetails(id, newLength, clip.Resolution);
            return library.Find(id)!;
        }

        // ------------------------------------------------------------------- helpers

        /// <summary>Changes whenever the video file changes, so pictures of the old version are never reused.</summary>
        private static string Version(string videoPath)
            => new FileInfo(videoPath).LastWriteTimeUtc.Ticks.ToString("x");

        private void RemoveStale(string id, string keepPrefix)
        {
            foreach (var file in Directory.EnumerateFiles(ThumbsFolder, id + "_*.jpg").ToList())
                if (!Path.GetFileName(file).StartsWith(keepPrefix, StringComparison.Ordinal)) TryDelete(file);
        }

        /// <summary>Remove every picture of a clip (when it is deleted).</summary>
        public void RemovePictures(string id) => RemoveStale(id, keepPrefix: "\0");

        private static Size ParseSize(string? text)
        {
            var parts = (text ?? "").Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) ? new Size(w, h) : Size.Empty;
        }

        private static string UniquePath(string folder, string stem)
        {
            string path = Path.Combine(folder, stem + ".mp4");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}_{n}.mp4");
            return path;
        }

        /// <summary>Replace a file, retrying briefly: the page has only just let go of the video and Windows can be slow to close it.</summary>
        private static void MoveOver(string from, string to)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(from, to, overwrite: true); return; }
                catch (IOException) when (attempt < 6) { Thread.Sleep(250); }
                catch (IOException)
                {
                    TryDelete(from);
                    throw new IOException("The clip is still open in another program, so it couldn't be replaced. Close it and try again.");
                }
            }
        }

        private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    }
}
