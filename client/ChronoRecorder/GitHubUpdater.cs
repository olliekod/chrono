using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ChronoRecorder
{
    /// <summary>
    /// The newest release GitHub has. <paramref name="Version"/> has no leading "v". <paramref name="SetupDownloadUrl"/>
    /// is null if that release has no <c>Chrono-Setup.exe</c> asset (a draft, or one still building) - there is a newer
    /// version to know about, but nothing yet to install.
    /// </summary>
    public sealed record ReleaseInfo(string Version, string ReleaseUrl, string? SetupDownloadUrl);

    /// <summary>
    /// Asks GitHub's public API for the repository's newest release. No key needed: releases on a public repo are public.
    /// A failed check (no internet, GitHub unreachable, rate limited) is never an error to show anyone - there is always
    /// a next scheduled try - so this only ever returns null rather than throwing, except from <see cref="DownloadInstallerAsync"/>,
    /// which someone asked for directly and needs to know if it failed.
    /// </summary>
    public class GitHubUpdater
    {
        public const string AssetName = "Chrono-Setup.exe";
        private const string ApiUrl = "https://api.github.com/repos/olliekod/chrono/releases/latest";
        private const long MinInstallerBytes = 10 * 1024 * 1024;   // the real one is ~76 MB; anything far smaller is a bad download, not an installer

        private readonly HttpClient http;

        public GitHubUpdater(HttpClient http)
        {
            this.http = http;
        }

        public static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Chrono-Updater/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>The repository's newest published release, or null if the check failed or nothing is published yet.</summary>
        public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
        {
            try
            {
                using var response = await http.GetAsync(ApiUrl, ct);
                if (!response.IsSuccessStatusCode) return null;

                var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
                string? tag = (string?)json["tag_name"];
                if (string.IsNullOrWhiteSpace(tag) || UpdateCheck.Parse(tag) == null) return null;

                string? assetUrl = (json["assets"] as JArray)?
                    .FirstOrDefault(a => string.Equals((string?)a["name"], AssetName, StringComparison.OrdinalIgnoreCase))?
                    .Value<string>("browser_download_url");

                return new ReleaseInfo(tag!.TrimStart('v', 'V'), (string?)json["html_url"] ?? "", assetUrl);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Downloads the installer to a temp file and returns its path. Unlike <see cref="GetLatestAsync"/>, this is
        /// something a person asked for directly (pressed "Update"), so a failure is thrown with a message fit to show.
        /// </summary>
        public async Task<string> DownloadInstallerAsync(string url, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            string path = Path.Combine(Path.GetTempPath(), $"Chrono-Setup-{Guid.NewGuid():N}.exe");

            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw new UpdateException($"The download failed ({(int)response.StatusCode}). Try again, or download it from GitHub yourself.");

                long? total = response.Content.Headers.ContentLength;
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var dest = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        if (total is > 0) progress?.Report((double)done / total.Value);
                    }
                }

                if (new FileInfo(path).Length < MinInstallerBytes)
                    throw new UpdateException("The download didn't look like the installer. Try again, or download it from GitHub yourself.");

                return path;
            }
            catch (UpdateException)
            {
                TryDelete(path);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(path);
                throw new UpdateException($"Couldn't download the update ({ex.Message}). Check your connection and try again.", ex);
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best effort; the OS cleans %TEMP% eventually */ }
        }
    }

    /// <summary>An update-download failure with a message fit to show the user, the same shape as <see cref="UploadException"/>.</summary>
    public class UpdateException : Exception
    {
        public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
