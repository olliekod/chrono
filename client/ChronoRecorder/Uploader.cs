using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChronoRecorder
{
    public sealed record UploadResult(string ClipId, string Link);

    /// <summary>An upload failure with a message that is fit to show the user.</summary>
    public class UploadException : Exception
    {
        public UploadException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Uploads a saved clip to the Worker in parts (create, PUT each part, complete) and returns the share link.
    /// Parts are retried on their own, so a flaky connection doesn't restart a whole clip.
    /// </summary>
    public class Uploader
    {
        private const int MaxAttempts = 3;

        private readonly HttpClient http;

        /// <summary>Wait before retry number <c>attempt</c> (1-based). Replaceable so tests don't sleep.</summary>
        public Func<int, TimeSpan> RetryDelay { get; set; } = attempt => TimeSpan.FromMilliseconds(500 * Math.Pow(3, attempt - 1));

        public Uploader(HttpClient http)
        {
            this.http = http;
        }

        /// <summary>A part is up to 16 MiB, so give slow connections time before giving up on one.</summary>
        public static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Chrono/1.0");
            return client;
        }

        public async Task<UploadResult> UploadAsync(
            string filePath,
            UploadSettings settings,
            ClipInfo info,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            if (!File.Exists(filePath)) throw new UploadException($"Clip file not found: {filePath}");

            long size = new FileInfo(filePath).Length;
            if (size == 0) throw new UploadException("The clip file is empty.");

            string baseUrl = settings.ServerUrl.TrimEnd('/');

            // 1. Start the upload; the server decides the part size.
            var created = await SendAsync(HttpMethod.Post, $"{baseUrl}/api/clips", settings.UploadKey, "start the upload",
                () => JsonBody(new
                {
                    username = settings.Username,
                    filename = Path.GetFileName(filePath),
                    title = info.Title,
                    size,
                    duration = info.DurationSeconds,
                    resolution = info.Resolution,
                    fps = info.Fps,
                    bitrate = info.BitrateKbps,
                }), ct);

            string clipId = (string?)created["id"] ?? "";
            long partSize = (long?)created["partSize"] ?? 0;
            string link = (string?)created["link"] ?? "";
            if (clipId.Length == 0 || partSize < 1 || link.Length == 0)
                throw new UploadException("The server sent an unexpected reply. Check the server address in Settings.");

            // 2. Send the parts, one at a time.
            int totalParts = UploadRules.PartCount(size, partSize);
            var uploaded = new JArray();
            var buffer = new byte[partSize];

            await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

            for (int part = 1; part <= totalParts; part++)
            {
                int length = (int)Math.Min(partSize, size - (part - 1) * partSize);
                await file.ReadExactlyAsync(buffer.AsMemory(0, length), ct);

                var result = await SendAsync(HttpMethod.Put, $"{baseUrl}/api/clips/{clipId}/parts/{part}", settings.UploadKey,
                    $"upload part {part} of {totalParts}",
                    () =>
                    {
                        var content = new ByteArrayContent(buffer, 0, length);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        return content;
                    }, ct);

                uploaded.Add(new JObject { ["partNumber"] = (int?)result["partNumber"] ?? part, ["etag"] = (string?)result["etag"] });
                progress?.Report((double)Math.Min(size, part * partSize) / size);
            }

            // 3. Publish it.
            var done = await SendAsync(HttpMethod.Post, $"{baseUrl}/api/clips/{clipId}/complete", settings.UploadKey,
                "finish the upload", () => JsonBody(new { parts = uploaded }), ct);

            return new UploadResult(clipId, (string?)done["link"] ?? link);
        }

        /// <summary>Rename an uploaded clip on the server, so its page and Discord embed show the new title.</summary>
        public async Task RenameAsync(UploadSettings settings, string clipId, string title, CancellationToken ct = default)
        {
            string baseUrl = settings.ServerUrl.TrimEnd('/');
            await SendAsync(new HttpMethod("PATCH"), $"{baseUrl}/api/clips/{Uri.EscapeDataString(clipId)}", settings.UploadKey, "rename the clip",
                () => JsonBody(new { title }), ct);
        }

        private static StringContent JsonBody(object body)
            => new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        /// <summary>
        /// One API call with retries for network trouble and temporary server errors.
        /// Anything else (a rejected key, an invalid clip) fails at once with the server's own message.
        /// </summary>
        private async Task<JObject> SendAsync(
            HttpMethod method, string url, string key, string what, Func<HttpContent> makeContent, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                string problem;

                try
                {
                    using var request = new HttpRequestMessage(method, url) { Content = makeContent() };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

                    using var response = await http.SendAsync(request, ct);
                    string text = await response.Content.ReadAsStringAsync(ct);

                    if (response.IsSuccessStatusCode)
                    {
                        try { return JObject.Parse(text); }
                        catch (JsonException) { throw new UploadException("The server sent an unexpected reply. Check the server address in Settings."); }
                    }

                    if (!IsTemporary(response.StatusCode)) throw new UploadException(Describe(response.StatusCode, text, what));
                    problem = $"the server answered {(int)response.StatusCode}";
                }
                catch (HttpRequestException ex)
                {
                    problem = ex.Message;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    problem = "the connection timed out"; // HttpClient's own timeout, not the caller cancelling
                }

                if (attempt >= MaxAttempts)
                    throw new UploadException($"Couldn't {what} ({problem}). Check your connection and try again.");

                await Task.Delay(RetryDelay(attempt), ct);
            }
        }

        private static bool IsTemporary(HttpStatusCode status)
            => (int)status >= 500 || status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests;

        private static string Describe(HttpStatusCode status, string body, string what)
        {
            if (status == HttpStatusCode.Unauthorized)
                return "The server rejected the upload key. Check it in Settings.";

            string? serverMessage = null;
            try { serverMessage = (string?)JObject.Parse(body)["error"]; } catch { }

            return serverMessage != null
                ? $"Couldn't {what}: {serverMessage}"
                : $"Couldn't {what} (the server answered {(int)status}).";
        }
    }
}
