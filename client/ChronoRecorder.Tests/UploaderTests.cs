using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class UploaderTests : IDisposable
    {
        private const string Server = "https://clips.test";
        private const int PartSize = 1000;

        private readonly List<string> tempFiles = new();
        private readonly FakeServer server = new();

        public void Dispose()
        {
            foreach (var f in tempFiles) { try { File.Delete(f); } catch { } }
        }

        private string MakeFile(int size)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chrono-test-{Guid.NewGuid():N}.mp4");
            var bytes = new byte[size];
            for (int i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
            File.WriteAllBytes(path, bytes);
            tempFiles.Add(path);
            return path;
        }

        private Uploader MakeUploader() => new Uploader(new HttpClient(server)) { RetryDelay = _ => TimeSpan.Zero };

        private static UploadSettings Settings => new(Server, "secret-key", "olly");
        private static ClipInfo Info => new(12.5, "1920x1080", 60, 8000);

        [Fact]
        public async Task UploadsAClipInParts_AndReturnsTheShareLink()
        {
            string file = MakeFile(2500);
            var progress = new SyncProgress();

            var result = await MakeUploader().UploadAsync(file, Settings, Info, progress);

            Assert.Equal("abc123def456", result.ClipId);
            Assert.Equal($"{Server}/watch/abc123def456", result.Link);

            var create = server.Requests[0];
            Assert.Equal("POST", create.Method);
            Assert.Equal($"{Server}/api/clips", create.Url);
            var body = JObject.Parse(create.Body);
            Assert.Equal("olly", (string?)body["username"]);
            Assert.Equal(Path.GetFileName(file), (string?)body["filename"]);
            Assert.Equal(2500, (long?)body["size"]);
            Assert.Equal(12.5, (double?)body["duration"]);
            Assert.Equal("1920x1080", (string?)body["resolution"]);
            Assert.Equal(60, (int?)body["fps"]);
            Assert.Equal(8000, (int?)body["bitrate"]);

            var puts = server.Requests.Where(r => r.Method == "PUT").ToList();
            Assert.Equal(new[] { 1000, 1000, 500 }, puts.Select(p => p.BodyBytes.Length));
            Assert.Equal(
                new[] { $"{Server}/api/clips/abc123def456/parts/1", $"{Server}/api/clips/abc123def456/parts/2", $"{Server}/api/clips/abc123def456/parts/3" },
                puts.Select(p => p.Url));

            // The parts, put back together, are exactly the file.
            Assert.Equal(File.ReadAllBytes(file), puts.SelectMany(p => p.BodyBytes).ToArray());

            var complete = server.Requests.Last();
            Assert.Equal($"{Server}/api/clips/abc123def456/complete", complete.Url);
            var parts = JObject.Parse(complete.Body)["parts"]!.ToObject<List<JObject>>()!;
            Assert.Equal(new[] { 1, 2, 3 }, parts.Select(p => (int)p["partNumber"]!));
            Assert.Equal(new[] { "etag-1", "etag-2", "etag-3" }, parts.Select(p => (string)p["etag"]!));

            Assert.All(server.Requests, r => Assert.Equal("Bearer secret-key", r.Authorization));

            Assert.Equal(new[] { 0.4, 0.8, 1.0 }, progress.Values.Select(v => Math.Round(v, 3)));
        }

        [Fact]
        public async Task UploadsAClipSmallerThanOnePart()
        {
            await MakeUploader().UploadAsync(MakeFile(10), Settings, Info);

            Assert.Single(server.Requests, r => r.Method == "PUT");
        }

        [Fact]
        public async Task RetriesAPartThatFailsTemporarily()
        {
            server.FailPart(2, HttpStatusCode.ServiceUnavailable, times: 2);

            var result = await MakeUploader().UploadAsync(MakeFile(2500), Settings, Info);

            Assert.Equal("abc123def456", result.ClipId);
            // Two failures then a success. The other parts are only sent once.
            Assert.Equal(3, server.Requests.Count(r => r.Url.EndsWith("/parts/2")));
            Assert.Equal(1, server.Requests.Count(r => r.Url.EndsWith("/parts/1")));
            Assert.Equal(1, server.Requests.Count(r => r.Url.EndsWith("/parts/3")));
        }

        [Fact]
        public async Task GivesUpAfterThreeAttempts()
        {
            server.FailPart(1, HttpStatusCode.BadGateway, times: 99);

            var ex = await Assert.ThrowsAsync<UploadException>(() => MakeUploader().UploadAsync(MakeFile(2500), Settings, Info));

            Assert.Equal(3, server.Requests.Count(r => r.Url.EndsWith("/parts/1")));
            Assert.Contains("part 1", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task RetriesNetworkFailures()
        {
            server.DropConnectionOnPart(1, times: 1);

            var result = await MakeUploader().UploadAsync(MakeFile(500), Settings, Info);

            Assert.Equal("abc123def456", result.ClipId);
            Assert.Equal(2, server.Requests.Count(r => r.Url.EndsWith("/parts/1")));
        }

        [Fact]
        public async Task ABadKeyFailsImmediatelyWithAHelpfulMessage()
        {
            server.RejectKey = true;

            var ex = await Assert.ThrowsAsync<UploadException>(() => MakeUploader().UploadAsync(MakeFile(500), Settings, Info));

            Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Settings", ex.Message);
            Assert.Single(server.Requests); // no retries for a 401
        }

        [Fact]
        public async Task PassesServerErrorMessagesThrough_WithoutRetrying()
        {
            server.CreateError = (HttpStatusCode.RequestEntityTooLarge, "Clip is larger than the limit");

            var ex = await Assert.ThrowsAsync<UploadException>(() => MakeUploader().UploadAsync(MakeFile(500), Settings, Info));

            Assert.Contains("Clip is larger than the limit", ex.Message);
            Assert.Single(server.Requests);
        }

        [Fact]
        public async Task MissingFileIsReportedClearly()
        {
            var ex = await Assert.ThrowsAsync<UploadException>(() =>
                MakeUploader().UploadAsync(Path.Combine(Path.GetTempPath(), "does-not-exist.mp4"), Settings, Info));

            Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(server.Requests);
        }

        [Fact]
        public async Task CanBeCancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                MakeUploader().UploadAsync(MakeFile(500), Settings, Info, null, cts.Token));
        }

        [Fact]
        public async Task RejectsAnUnusableServerAnswer()
        {
            server.MalformedCreate = true;

            await Assert.ThrowsAsync<UploadException>(() => MakeUploader().UploadAsync(MakeFile(500), Settings, Info));
        }

        // -------------------------------------------------------------------------------------------

        /// <summary>Progress&lt;T&gt; posts to the thread pool, which can reorder reports; this records them in order.</summary>
        private sealed class SyncProgress : IProgress<double>
        {
            public readonly List<double> Values = new();
            public void Report(double value) => Values.Add(value);
        }

        private sealed class RecordedRequest
        {
            public string Method = "";
            public string Url = "";
            public string? Authorization;
            public byte[] BodyBytes = Array.Empty<byte>();
            public string Body => Encoding.UTF8.GetString(BodyBytes);
        }

        /// <summary>A stand-in for the Worker that speaks the same protocol.</summary>
        private sealed class FakeServer : HttpMessageHandler
        {
            public readonly List<RecordedRequest> Requests = new();
            public bool RejectKey;
            public bool MalformedCreate;
            public (HttpStatusCode Status, string Message)? CreateError;
            private readonly Dictionary<int, (HttpStatusCode Status, int Remaining)> failures = new();
            private readonly Dictionary<int, int> drops = new();

            public void FailPart(int part, HttpStatusCode status, int times) => failures[part] = (status, times);
            public void DropConnectionOnPart(int part, int times) => drops[part] = times;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var recorded = new RecordedRequest
                {
                    Method = request.Method.Method,
                    Url = request.RequestUri!.ToString(),
                    Authorization = request.Headers.Authorization?.ToString(),
                    BodyBytes = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct),
                };
                Requests.Add(recorded);

                if (RejectKey) return Json(HttpStatusCode.Unauthorized, new { error = "Missing or invalid upload key" });

                string path = request.RequestUri.AbsolutePath;

                if (path == "/api/clips")
                {
                    if (CreateError is { } err) return Json(err.Status, new { error = err.Message });
                    if (MalformedCreate) return Json(HttpStatusCode.Created, new { unexpected = true });
                    return Json(HttpStatusCode.Created, new { id = "abc123def456", partSize = PartSize, link = $"{Server}/watch/abc123def456" });
                }

                var part = System.Text.RegularExpressions.Regex.Match(path, @"/parts/(\d+)$");
                if (part.Success)
                {
                    int n = int.Parse(part.Groups[1].Value);

                    if (drops.TryGetValue(n, out int dropsLeft) && dropsLeft > 0)
                    {
                        drops[n] = dropsLeft - 1;
                        throw new HttpRequestException("connection reset");
                    }

                    if (failures.TryGetValue(n, out var f) && f.Remaining > 0)
                    {
                        failures[n] = (f.Status, f.Remaining - 1);
                        return Json(f.Status, new { error = "temporary" });
                    }

                    return Json(HttpStatusCode.OK, new { partNumber = n, etag = $"etag-{n}" });
                }

                if (path.EndsWith("/complete"))
                {
                    return Json(HttpStatusCode.OK, new { id = "abc123def456", link = $"{Server}/watch/abc123def456", size = 0 });
                }

                return Json(HttpStatusCode.NotFound, new { error = "not found" });
            }

            private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
            {
                Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
            };
        }
    }
}
