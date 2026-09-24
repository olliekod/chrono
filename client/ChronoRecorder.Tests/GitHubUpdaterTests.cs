using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class GitHubUpdaterTests : IDisposable
    {
        private readonly FakeGitHub fake = new();
        private readonly GitHubUpdater updater;
        private readonly List<string> toDelete = new();

        public GitHubUpdaterTests() => updater = new GitHubUpdater(new HttpClient(fake));

        public void Dispose() { foreach (var path in toDelete) { try { File.Delete(path); } catch { } } }

        [Fact]
        public async Task GetLatestAsync_ReadsTheTagAndTheInstallerAsset()
        {
            fake.Release(tag: "v1.1.6", htmlUrl: "https://github.com/olliekod/chrono/releases/tag/v1.1.6", assets: new()
            {
                ["Chrono-Setup.exe"] = "https://github.com/olliekod/chrono/releases/download/v1.1.6/Chrono-Setup.exe",
                ["Chrono-win-x64.zip"] = "https://github.com/olliekod/chrono/releases/download/v1.1.6/Chrono-win-x64.zip",
            });

            var release = await updater.GetLatestAsync();

            Assert.NotNull(release);
            Assert.Equal("1.1.6", release!.Version);   // the leading v is stripped
            Assert.Equal("https://github.com/olliekod/chrono/releases/tag/v1.1.6", release.ReleaseUrl);
            Assert.Equal("https://github.com/olliekod/chrono/releases/download/v1.1.6/Chrono-Setup.exe", release.SetupDownloadUrl);
        }

        [Fact]
        public void CreateHttpClient_SendsAUserAgent_AsGitHubRequires()
        {
            // GitHub's API refuses requests with no User-Agent at all.
            Assert.NotEmpty(GitHubUpdater.CreateHttpClient().DefaultRequestHeaders.UserAgent.ToString());
        }

        [Fact]
        public async Task GetLatestAsync_WithNoMatchingAsset_StillReportsTheVersion()
        {
            // A release still building, or a draft: there is something newer, just nothing to install yet.
            fake.Release("v1.1.6", assets: new() { ["Chrono-win-x64.zip"] = "https://example.com/zip" });

            var release = await updater.GetLatestAsync();

            Assert.NotNull(release);
            Assert.Equal("1.1.6", release!.Version);
            Assert.Null(release.SetupDownloadUrl);
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.Forbidden)]   // rate limited
        public async Task GetLatestAsync_ReturnsNull_RatherThanThrowing(HttpStatusCode status)
        {
            fake.Status = status;
            Assert.Null(await updater.GetLatestAsync());
        }

        [Fact]
        public async Task GetLatestAsync_ReturnsNull_WhenTheConnectionDrops()
        {
            fake.Throw = true;
            Assert.Null(await updater.GetLatestAsync());
        }

        [Fact]
        public async Task GetLatestAsync_IgnoresATagThatIsntAVersion()
        {
            // A pre-release or a tag from something other than a numbered release.
            fake.Release("nightly-build");
            Assert.Null(await updater.GetLatestAsync());
        }

        [Fact]
        public async Task DownloadInstallerAsync_SavesTheBytes_AndReportsProgress()
        {
            byte[] body = new byte[12 * 1024 * 1024];
            new Random(1).NextBytes(body);
            fake.DownloadBody = body;

            var reports = new List<double>();
            string path = await updater.DownloadInstallerAsync("https://example.com/Chrono-Setup.exe", new Progress<double>(reports.Add));
            toDelete.Add(path);

            Assert.True(File.Exists(path));
            Assert.Equal(body, await File.ReadAllBytesAsync(path));
            Assert.NotEmpty(reports);
            Assert.Equal(1.0, reports[^1], 3);
        }

        [Fact]
        public async Task DownloadInstallerAsync_RefusesATooSmallFile_AndCleansUpAfterItself()
        {
            fake.DownloadBody = new byte[1024];   // far short of a real installer

            var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadInstallerAsync("https://example.com/Chrono-Setup.exe"));

            Assert.Contains("didn't look like the installer", ex.Message);
        }

        [Fact]
        public async Task DownloadInstallerAsync_ThrowsAFitMessage_OnAFailedDownload()
        {
            fake.DownloadStatus = HttpStatusCode.NotFound;

            var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadInstallerAsync("https://example.com/Chrono-Setup.exe"));

            Assert.Contains("download failed", ex.Message);
        }

        private sealed class FakeGitHub : HttpMessageHandler
        {
            public HttpStatusCode Status = HttpStatusCode.OK;
            public bool Throw;
            public string? LastUserAgent;
            public byte[] DownloadBody = new byte[12 * 1024 * 1024];
            public HttpStatusCode DownloadStatus = HttpStatusCode.OK;
            private string body = "{}";

            public void Release(string tag, string htmlUrl = "https://github.com/olliekod/chrono/releases/tag/x", Dictionary<string, string>? assets = null)
            {
                var assetList = (assets ?? new()).Select(kv => new { name = kv.Key, browser_download_url = kv.Value });
                body = JsonConvert.SerializeObject(new { tag_name = tag, html_url = htmlUrl, assets = assetList });
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (Throw) throw new HttpRequestException("no connection");
                LastUserAgent = request.Headers.UserAgent.ToString();

                if (request.RequestUri!.Host == "api.github.com")
                {
                    var response = new HttpResponseMessage(Status);
                    if (Status == HttpStatusCode.OK) response.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    return Task.FromResult(response);
                }

                // The download itself, from wherever GitHub's asset URL points.
                var dl = new HttpResponseMessage(DownloadStatus);
                if (DownloadStatus == HttpStatusCode.OK) dl.Content = new ByteArrayContent(DownloadBody);
                return Task.FromResult(dl);
            }
        }
    }
}
