using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace ChronoRecorder
{
    /// <summary>What the bridge needs from the window around it (so it can be tested without one).</summary>
    public interface IUiHost
    {
        /// <summary>Send a message (an event) to the page.</summary>
        void Post(string json);
        void CopyToClipboard(string text);
        void ShowInFolder(string path);
        void OpenFolder(string path);
    }

    /// <summary>
    /// The link between the page (HTML and JavaScript) and the rest of Chrono. The page sends requests
    /// <c>{requestId, action, ...}</c> and gets <c>{requestId, ok, data}</c> or <c>{requestId, ok:false, error}</c> back; Chrono can also push
    /// events <c>{event, data}</c> (library changed, status changed, upload progress).
    /// </summary>
    public sealed class UiBridge
    {
        public const string ThumbsBaseUrl = "https://thumbs.chrono/";
        public const string ClipsBaseUrl = "https://clips.chrono/";

        private static readonly JsonSerializerSettings CamelCase = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        private readonly RecorderConfig config;
        private readonly IRecorder recorder;
        private readonly ClipLibrary library;
        private readonly ClipMedia media;
        private readonly Uploader uploader;
        private readonly IUiHost host;
        private readonly Action onConfigSaved;
        private readonly Action<RecorderConfig> saveConfig;

        private readonly Dictionary<string, Func<JObject, Task<object?>>> handlers;
        private readonly HashSet<string> uploading = new();

        public UiBridge(RecorderConfig config, IRecorder recorder, ClipLibrary library, ClipMedia media, Uploader uploader, IUiHost host, Action onConfigSaved,
            Action<RecorderConfig>? saveConfig = null)
        {
            this.config = config;
            this.recorder = recorder;
            this.library = library;
            this.media = media;
            this.uploader = uploader;
            this.host = host;
            this.onConfigSaved = onConfigSaved;
            this.saveConfig = saveConfig ?? ConfigManager.Save;   // tests pass their own so the real settings file is never touched

            handlers = new Dictionary<string, Func<JObject, Task<object?>>>
            {
                ["getStatus"] = _ => Task.FromResult<object?>(Status()),
                ["getLibrary"] = GetLibrary,
                ["getClip"] = GetClip,
                ["getPoster"] = GetPoster,
                ["getFilmstrip"] = GetFilmstrip,
                ["renameClip"] = RenameClip,
                ["deleteClip"] = DeleteClip,
                ["uploadClip"] = UploadClip,
                ["copyLink"] = CopyLink,
                ["trimClip"] = TrimClip,
                ["showInFolder"] = ShowInFolder,
                ["openClipsFolder"] = _ => { host.OpenFolder(config.OutputFolder); return Task.FromResult<object?>(new { }); },
                ["setRecorderEnabled"] = SetRecorderEnabled,
                ["setMode"] = SetMode,
                ["getRunningApps"] = _ => Task.FromResult<object?>(new { apps = recorder.RunningApplications() }),
                ["getSettings"] = GetSettings,
                ["getRecommendedBitrate"] = GetRecommendedBitrate,
                ["saveSettings"] = SaveSettings,
            };
        }

        // ---------------------------------------------------------------- dispatch

        /// <summary>Handle one request from the page and return the reply to send back (null if it wasn't a request).</summary>
        public async Task<string?> HandleAsync(string requestJson)
        {
            JObject request;
            try { request = JObject.Parse(requestJson); }
            catch (JsonException) { return null; }

            var id = request["requestId"];
            string action = request.Value<string>("action") ?? "";
            if (id == null) return null;

            try
            {
                if (!handlers.TryGetValue(action, out var handler)) throw new InvalidOperationException($"Unknown request \"{action}\".");
                object? data = await handler(request);
                return Reply(id, ok: true, data, null);
            }
            catch (UploadException ex)
            {
                return Reply(id, ok: false, null, ex.Message);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Request {action} failed: {ex.Message}");
                return Reply(id, ok: false, null, ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Request {action} crashed: {ex}");
                return Reply(id, ok: false, null, "Something went wrong. The log has the details.");
            }
        }

        private static string Reply(JToken id, bool ok, object? data, string? error)
        {
            var reply = new JObject { ["requestId"] = id, ["ok"] = ok };
            if (ok) reply["data"] = data == null ? new JObject() : JToken.FromObject(data, JsonSerializer.Create(CamelCase));
            else reply["error"] = error;
            return reply.ToString(Formatting.None);
        }

        public void PushEvent(string name, object? data = null)
        {
            var message = new JObject { ["event"] = name };
            if (data != null) message["data"] = JToken.FromObject(data, JsonSerializer.Create(CamelCase));
            host.Post(message.ToString(Formatting.None));
        }

        public void PushStatus() => PushEvent("status", Status());

        public StatusDto Status() => StatusPresenter.Build(config, recorder);

        // ------------------------------------------------------------------ library

        private ClipDto Dto(ClipRecord clip) => ClipDto.From(clip, ClipsBaseUrl);

        private ClipRecord Need(JObject request)
        {
            string id = request.Value<string>("id") ?? "";
            return library.Find(id) ?? throw new InvalidOperationException("That clip isn't in the library any more.");
        }

        private Task<object?> GetLibrary(JObject _)
        {
            library.Refresh();
            var clips = library.Snapshot().Select(Dto).ToList();
            Task.Run(() => library.FillMissingDetails());   // older clips get their length and size in the background
            return Task.FromResult<object?>(new { clips, clipsFolder = config.OutputFolder, canUpload = UploadRules.SettingsFrom(config) != null });
        }

        private Task<object?> GetClip(JObject request) => Task.FromResult<object?>(new { clip = Dto(Need(request)) });

        private async Task<object?> GetPoster(JObject request)
        {
            var clip = Need(request);
            string? file = await Task.Run(() => media.Poster(clip));
            return new { url = file == null ? null : ThumbsBaseUrl + Uri.EscapeDataString(file) };
        }

        private async Task<object?> GetFilmstrip(JObject request)
        {
            var clip = Need(request);
            var strip = await Task.Run(() => media.Filmstrip(clip));
            return strip == null ? null : new { url = ThumbsBaseUrl + Uri.EscapeDataString(strip.FileName), frames = strip.Frames };
        }

        private async Task<object?> RenameClip(JObject request)
        {
            var clip = Need(request);
            string title = request.Value<string>("title") ?? "";
            library.Rename(clip.Id, title);
            var updated = library.Find(clip.Id)!;

            // An uploaded clip's page and Discord embed use the title on the server, so rename it there too.
            string? remoteError = null;
            var settings = UploadRules.SettingsFrom(config);
            if (updated.IsUploaded && !string.IsNullOrEmpty(updated.RemoteId) && settings != null)
            {
                try { await uploader.RenameAsync(settings, updated.RemoteId!, updated.Title); }
                catch (UploadException ex) { remoteError = ex.Message; }
            }
            return new { clip = Dto(updated), remoteError };
        }

        private async Task<object?> DeleteClip(JObject request)
        {
            var clip = Need(request);
            // The page has just let go of the video; give Windows a moment to close the file.
            for (int attempt = 1; ; attempt++)
            {
                try { library.Delete(clip.Id); break; }
                catch (System.IO.IOException) when (attempt < 5) { await Task.Delay(250); }
            }
            media.RemovePictures(clip.Id);
            return new { };
        }

        private async Task<object?> UploadClip(JObject request)
        {
            var clip = Need(request);
            var settings = UploadRules.SettingsFrom(config)
                ?? throw new UploadException("Uploading isn't set up. Add your server address and upload key in Settings.");

            lock (uploading) { if (!uploading.Add(clip.Id)) throw new UploadException("That clip is already uploading."); }
            try
            {
                string path = library.PathOf(clip);
                double? duration = clip.DurationSeconds ?? await Task.Run(() => MediaProbe.DurationSeconds(path));
                string? size = clip.Resolution ?? await Task.Run(() => MediaProbe.VideoSizeText(path));
                var info = new ClipInfo(duration, size, config.Fps, BitrateSizing.Resolve(config.Bitrate, size, config.Fps), clip.Title);

                var progress = new Progress<double>(f => PushEvent("uploadProgress", new { id = clip.Id, fraction = f }));
                var result = await uploader.UploadAsync(path, settings, info, progress);

                library.MarkUploaded(clip.Id, result.Link, result.ClipId);
                host.CopyToClipboard(result.Link);
                return new { clip = Dto(library.Find(clip.Id)!) };
            }
            finally { lock (uploading) uploading.Remove(clip.Id); }
        }

        private Task<object?> CopyLink(JObject request)
        {
            var clip = Need(request);
            if (!clip.IsUploaded) throw new InvalidOperationException("This clip hasn't been uploaded yet.");
            host.CopyToClipboard(clip.Link!);
            return Task.FromResult<object?>(new { });
        }

        private async Task<object?> TrimClip(JObject request)
        {
            var clip = Need(request);
            double start = request.Value<double?>("start") ?? 0;
            double end = request.Value<double?>("end") ?? 0;
            bool asCopy = request.Value<bool?>("saveAsCopy") ?? false;

            if (end - start < ClipMediaCommands.MinimumTrimSeconds)
                throw new InvalidOperationException($"Keep at least {ClipMediaCommands.MinimumTrimSeconds} seconds of the clip.");

            var result = await Task.Run(() => media.Trim(clip.Id, start, end, asCopy));
            return new { clip = Dto(result) };
        }

        private Task<object?> ShowInFolder(JObject request)
        {
            host.ShowInFolder(library.PathOf(Need(request)));
            return Task.FromResult<object?>(new { });
        }

        // ---------------------------------------------------------------- recording

        private Task<object?> SetRecorderEnabled(JObject request)
        {
            recorder.SetRecorderEnabled(request.Value<bool?>("enabled") ?? true);
            return Task.FromResult<object?>(Status());
        }

        private Task<object?> SetMode(JObject request)
        {
            string name = request.Value<string>("mode") ?? "";
            if (!Enum.TryParse<RecorderConfig.RecordingMode>(name, ignoreCase: true, out var mode))
                throw new InvalidOperationException($"Unknown mode \"{name}\".");

            recorder.SetRecordingMode(mode);
            string? app = request.Value<string>("app");
            if (app != null) recorder.SetTrackedApplication(app);
            return Task.FromResult<object?>(Status());
        }

        // ----------------------------------------------------------------- settings

        private Task<object?> GetSettings(JObject _)
        {
            return Task.FromResult<object?>(new
            {
                config = JObject.FromObject(config),   // PascalCase, exactly what is sent back on save
                recommended = Recommended(config.Fps)
            });
        }

        private object Recommended(int fps)
        {
            var size = recorder.RecordingSize();
            return new { kbps = BitrateSizing.Resolve(0, size, fps), size = CaptureSizing.Describe(size), fps };
        }

        private Task<object?> GetRecommendedBitrate(JObject request)
            => Task.FromResult<object?>(Recommended(request.Value<int?>("fps") ?? config.Fps));

        private Task<object?> SaveSettings(JObject request)
        {
            var incoming = request["config"]?.ToObject<RecorderConfig>() ?? throw new InvalidOperationException("No settings were sent.");
            incoming.Hotkeys ??= config.Hotkeys;

            string? problem = SettingsRules.ValidateAndTidy(incoming);
            if (problem != null) throw new InvalidOperationException(problem);

            // Edit the live config in place: the recorder and hotkeys hold this same instance.
            config.CopyFrom(incoming);
            saveConfig(config);
            onConfigSaved();
            PushStatus();
            return Task.FromResult<object?>(new { config = JObject.FromObject(config) });
        }
    }
}
