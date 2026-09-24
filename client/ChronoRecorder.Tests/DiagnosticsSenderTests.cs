using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class DiagnosticsSenderTests
    {
        private const string Server = "https://clips.test";
        private static readonly UploadSettings Settings = new(Server, "key", "Oliver");

        private sealed class FakeServer : HttpMessageHandler
        {
            public HttpRequestMessage? LastRequest;
            public string? LastBody;
            public HttpStatusCode Status = HttpStatusCode.Created;
            public bool Throw;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (Throw) throw new HttpRequestException("no connection");
                LastRequest = request;
                LastBody = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
                return new HttpResponseMessage(Status) { Content = new StringContent("{\"id\":\"abc123def456\"}", Encoding.UTF8, "application/json") };
            }
        }

        [Fact]
        public async Task SendAsync_PostsTheReportWithTheKeyAndUsername()
        {
            var server = new FakeServer();
            var sender = new DiagnosticsSender(new HttpClient(server));

            await sender.SendAsync(Settings, "Chrono 1.1.6 diagnostics\n...", "Long Clip_2026-09-23_20-51-05.mp4");

            Assert.Equal($"{Server}/api/diagnostics", server.LastRequest!.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, server.LastRequest.Method);
            Assert.Equal("Bearer key", server.LastRequest.Headers.Authorization!.ToString());

            var body = JObject.Parse(server.LastBody!);
            Assert.Equal("Oliver", (string?)body["username"]);
            Assert.Equal("Long Clip_2026-09-23_20-51-05.mp4", (string?)body["clipFilename"]);
            Assert.Contains("1.1.6", (string?)body["report"]);
        }

        [Fact]
        public async Task SendAsync_WorksWithNoClipFilename()
        {
            var server = new FakeServer();
            var sender = new DiagnosticsSender(new HttpClient(server));

            await sender.SendAsync(Settings, "report text", null);

            var body = JObject.Parse(server.LastBody!);
            Assert.True(body["clipFilename"] == null || body["clipFilename"]!.Type == JTokenType.Null);
        }

        [Fact]
        public async Task SendAsync_TrimsATrailingSlashFromTheServerAddress()
        {
            var server = new FakeServer();
            var sender = new DiagnosticsSender(new HttpClient(server));

            await sender.SendAsync(Settings with { ServerUrl = Server + "/" }, "report", null);

            Assert.Equal($"{Server}/api/diagnostics", server.LastRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task SendAsync_ThrowsAFitMessage_WhenTheServerRejectsIt()
        {
            var server = new FakeServer { Status = HttpStatusCode.Unauthorized };
            var sender = new DiagnosticsSender(new HttpClient(server));

            var ex = await Assert.ThrowsAsync<UploadException>(() => sender.SendAsync(Settings, "report", null));

            Assert.Contains("401", ex.Message);
        }

        [Fact]
        public async Task SendAsync_ThrowsAFitMessage_WhenThereIsNoConnection()
        {
            var server = new FakeServer { Throw = true };
            var sender = new DiagnosticsSender(new HttpClient(server));

            var ex = await Assert.ThrowsAsync<UploadException>(() => sender.SendAsync(Settings, "report", null));

            Assert.Contains("Couldn't send the diagnostics report", ex.Message);
        }

        [Fact]
        public void CreateHttpClient_SendsChronosUserAgent()
        {
            Assert.Equal("Chrono/1.0", DiagnosticsSender.CreateHttpClient().DefaultRequestHeaders.UserAgent.ToString());
        }
    }
}
