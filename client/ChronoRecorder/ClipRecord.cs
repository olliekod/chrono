using System;

namespace ChronoRecorder
{
    /// <summary>One clip in the library. Saved in library.json; the video itself stays in the clips folder.</summary>
    public sealed class ClipRecord
    {
        /// <summary>Stable id (never reused), used by the UI and for the thumbnail file.</summary>
        public string Id { get; set; } = "";

        /// <summary>File name inside the clips folder. Kept relative so moving the folder doesn't break the library.</summary>
        public string FileName { get; set; } = "";

        /// <summary>What the person calls it. Shown everywhere and sent with the upload.</summary>
        public string Title { get; set; } = "";

        /// <summary>The game it was recorded from, if known.</summary>
        public string? Game { get; set; }

        public DateTime CreatedUtc { get; set; }
        public double? DurationSeconds { get; set; }
        public long SizeBytes { get; set; }

        /// <summary>Picture size like "2560x1440".</summary>
        public string? Resolution { get; set; }

        /// <summary>The share link once uploaded, else null.</summary>
        public string? Link { get; set; }

        /// <summary>The server's id for the uploaded copy, needed to rename it there.</summary>
        public string? RemoteId { get; set; }

        public DateTime? UploadedUtc { get; set; }

        /// <summary>
        /// The secret the server gave this app when the clip was uploaded. Renaming or removing the uploaded copy needs it,
        /// which is what keeps a friend who shares the upload key from touching your clips. Null for clips uploaded before the
        /// server issued them: those can't be removed from Chrono. Never sent to the page.
        /// </summary>
        public string? OwnerToken { get; set; }

        public bool IsUploaded => !string.IsNullOrEmpty(Link);
    }
}
