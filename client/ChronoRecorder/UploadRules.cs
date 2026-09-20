using System;
using System.Text.RegularExpressions;

namespace ChronoRecorder
{
    public sealed record UploadSettings(string ServerUrl, string UploadKey, string Username);

    /// <summary>What the Worker needs to know about a clip. Everything is optional metadata.</summary>
    public sealed record ClipInfo(double? DurationSeconds, string? Resolution, int? Fps, int? BitrateKbps);

    /// <summary>Pure decisions about uploading: what counts as configured, and how to clean up user input.</summary>
    public static class UploadRules
    {
        // The address this project used before it had a backend of its own. Nobody here owns it.
        private const string LegacyHost = "chrono-clips.fly.dev";

        /// <summary>The server only accepts 1-32 letters, digits, "_" and "-".</summary>
        public static string SanitizeUsername(string? name)
        {
            string cleaned = Regex.Replace((name ?? "").Trim(), "[^A-Za-z0-9_-]", "_");
            if (cleaned.Length > 32) cleaned = cleaned.Substring(0, 32);
            return cleaned.Length == 0 ? "player" : cleaned;
        }

        /// <summary>
        /// A base address safe to send the upload key to, or "" if it isn't one. Plain http is only allowed for
        /// the local machine; anything else would send the key across the network in clear text.
        /// </summary>
        public static string NormalizeServerUrl(string? input)
        {
            string text = (input ?? "").Trim();
            if (text.Length == 0) return "";

            if (!text.Contains("://")) text = "https://" + text;

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return "";
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return "";
            if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback) return "";
            if (uri.Host.Equals(LegacyHost, StringComparison.OrdinalIgnoreCase)) return "";

            return text.TrimEnd('/');
        }

        /// <summary>Settings to upload with, or null while the server or key hasn't been set up.</summary>
        public static UploadSettings? SettingsFrom(RecorderConfig config)
        {
            string url = NormalizeServerUrl(config.ApiUrl);
            string key = (config.UploadKey ?? "").Trim();
            if (url.Length == 0 || key.Length == 0) return null;

            return new UploadSettings(url, key, SanitizeUsername(config.Username));
        }

        public static int PartCount(long size, long partSize) => (int)((size + partSize - 1) / partSize);
    }
}
