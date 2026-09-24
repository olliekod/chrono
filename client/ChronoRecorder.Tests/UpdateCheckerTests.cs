using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class UpdateCheckerTests
    {
        private sealed class FakeGitHub : HttpMessageHandler
        {
            public string Tag = "v1.1.6";
            public int Requests;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests++;
                if (request.RequestUri!.Host == "api.github.com")
                {
                    string body = JsonConvert.SerializeObject(new { tag_name = Tag, html_url = "https://github.com/olliekod/chrono/releases", assets = Array.Empty<object>() });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private static UpdateChecker Checker(FakeGitHub fake, RecorderConfig? config = null, string currentVersion = "1.1.4")
            => new(config ?? new RecorderConfig(), new GitHubUpdater(new HttpClient(fake)), () => currentVersion);

        [Fact]
        public async Task ANewerRelease_IsFound()
        {
            var fake = new FakeGitHub { Tag = "v1.1.6" };
            var checker = Checker(fake);

            var release = await checker.CheckAsync();

            Assert.NotNull(release);
            Assert.Equal("1.1.6", release!.Version);
            Assert.Equal(release, checker.Latest);
        }

        [Fact]
        public async Task TheSameOrAnOlderRelease_IsNotAnUpdate()
        {
            var fake = new FakeGitHub { Tag = "v1.1.4" };
            var checker = Checker(fake, currentVersion: "1.1.4");

            Assert.Null(await checker.CheckAsync());
            Assert.Null(checker.Latest);
        }

        [Fact]
        public async Task TurnedOff_NeverAsksGitHubAtAll()
        {
            var fake = new FakeGitHub { Tag = "v1.1.6" };
            var checker = Checker(fake, new RecorderConfig { CheckForUpdates = false });

            Assert.Null(await checker.CheckAsync());
            Assert.Equal(0, fake.Requests);
        }

        [Fact]
        public async Task UpdateFound_FiresOnceForANewVersion_NotOnEveryRepeatedCheck()
        {
            var fake = new FakeGitHub { Tag = "v1.1.6" };
            var checker = Checker(fake);
            int fired = 0;
            checker.UpdateFound += _ => fired++;

            await checker.CheckAsync();
            await checker.CheckAsync();
            await checker.CheckAsync();

            Assert.Equal(1, fired);
        }

        [Fact]
        public async Task UpdateFound_FiresAgain_IfAnEvenNewerReleaseComesOut()
        {
            var fake = new FakeGitHub { Tag = "v1.1.6" };
            var checker = Checker(fake);
            var seen = new System.Collections.Generic.List<string>();
            checker.UpdateFound += r => seen.Add(r.Version);

            await checker.CheckAsync();
            fake.Tag = "v1.1.7";
            await checker.CheckAsync();

            Assert.Equal(new[] { "1.1.6", "1.1.7" }, seen);
        }

        [Fact]
        public async Task IfAReleaseIsWithdrawnOrGitHubIsUnreachable_LatestGoesBackToNull()
        {
            var fake = new FakeGitHub { Tag = "v1.1.6" };
            var checker = Checker(fake);
            await checker.CheckAsync();
            Assert.NotNull(checker.Latest);

            fake.Tag = "1.1.4";   // back to the current version - nothing to announce
            await checker.CheckAsync();

            Assert.Null(checker.Latest);
        }
    }
}
