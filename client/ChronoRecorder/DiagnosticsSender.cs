using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ChronoRecorder
{
    /// <summary>
    /// Sends a diagnostics report to the configured server, for someone who turned on "Send a diagnostics report when
    /// you save a clip". A separate, much smaller upload from a clip's: plain text, no video, straight to D1, never R2.
    /// </summary>
    public class DiagnosticsSender
    {
        private readonly HttpClient http;

        public DiagnosticsSender(HttpClient http)
        {
            this.http = http;
        }

        public static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Chrono/1.0");
            return client;
        }

        /// <summary>
        /// Posts the report. Throws <see cref="UploadException"/> on failure, the same type uploading a clip throws, so
        /// a caller can show or log it the same way. Never retries: a report that didn't make it just waits for the
        /// next clip, which is due in minutes anyway.
        /// </summary>
        public async Task SendAsync(UploadSettings settings, string report, string? clipFilename, CancellationToken ct = default)
        {
            string baseUrl = settings.ServerUrl.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/diagnostics")
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { username = settings.Username, clipFilename, report }),
                    Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.UploadKey);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new UploadException($"Couldn't send the diagnostics report ({ex.Message}).", ex);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new UploadException("Couldn't send the diagnostics report (the connection timed out).");
            }

            if (!response.IsSuccessStatusCode)
                throw new UploadException($"Couldn't send the diagnostics report (the server answered {(int)response.StatusCode}).");
        }
    }
}
