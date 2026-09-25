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

        /// <summary>Close Chrono. Used after handing off to an update installer, which needs Chrono gone before it can
        /// replace its files.</summary>
        void RequestExit();

        /// <summary>Shows a native folder picker starting at <paramref name="initialFolder"/>. Null if cancelled.</summary>
        string? PickFolder(string title, string initialFolder);
    }

    /// <summary>
    /// The link between the page (HTML and JavaScript) and the rest of Chrono. The page sends requests
    /// <c>{requestId, action, ...}</c> and gets <c>{requestId, ok, data}</c> or <c>{requestId, ok:false, error}</c> back; Chrono can also push
    /// events <c>{event, data}</c> (library changed, status changed, upload progress).
    /// </summary>
    public sealed class UiBridge : IDisposable
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
        private readonly Func<IReadOnlyList<string>> onConfigSaved;
        private readonly Action<RecorderConfig> saveConfig;
        private readonly DiagnosticsCollector? diagnostics;
        private readonly UpdateChecker? updates;
        private readonly GitHubUpdater updateDownloader;
        private readonly Action<string> launchInstaller;
        private readonly VoiceClipListener? voice;

        /// <summary>
        /// How long InstallUpdate waits after launching the installer before asking the host to exit. Long enough for
        /// the "Starting the installer" toast to register before the tray icon vanishes; the installer's own wizard
        /// needs a further click before it does anything that cares whether Chrono is still running (see
        /// DEVELOPMENT.md). Tests set this to zero so they don't wait on the real clock.
        /// </summary>
        public TimeSpan ExitDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

        private readonly Dictionary<string, Func<JObject, Task<object?>>> handlers;
        private readonly HashSet<string> uploading = new();
        private MicMeter? meter;

        public UiBridge(RecorderConfig config, IRecorder recorder, ClipLibrary library, ClipMedia media, Uploader uploader, IUiHost host, Func<IReadOnlyList<string>> onConfigSaved,
            Action<RecorderConfig>? saveConfig = null, DiagnosticsCollector? diagnostics = null, UpdateChecker? updates = null, GitHubUpdater? updateDownloader = null,
            Action<string>? launchInstaller = null, VoiceClipListener? voice = null)
        {
            this.voice = voice;
            this.diagnostics = diagnostics;
            this.config = config;
            this.recorder = recorder;
            this.library = library;
            this.media = media;
            this.uploader = uploader;
            this.host = host;
            this.onConfigSaved = onConfigSaved;
            this.saveConfig = saveConfig ?? ConfigManager.Save;   // tests pass their own so the real settings file is never touched
            this.updates = updates;
            this.updateDownloader = updateDownloader ?? new GitHubUpdater(GitHubUpdater.CreateHttpClient());
            this.launchInstaller = launchInstaller ?? (path => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }));
            if (updates != null) updates.UpdateFound += OnUpdateFound;

            handlers = new Dictionary<string, Func<JObject, Task<object?>>>
            {
                ["getStatus"] = _ => Task.FromResult<object?>(Status()),
                ["getLibrary"] = GetLibrary,
                ["getClip"] = GetClip,
                ["getPoster"] = GetPoster,
                ["getFilmstrip"] = GetFilmstrip,
                ["renameClip"] = RenameClip,
                ["deleteClip"] = DeleteClip,
                ["removeUpload"] = RemoveUpload,
                ["uploadClip"] = UploadClip,
                ["copyLink"] = CopyLink,
                ["trimClip"] = TrimClip,
                ["showInFolder"] = ShowInFolder,
                ["openClipsFolder"] = _ => { host.OpenFolder(config.OutputFolder); return Task.FromResult<object?>(new { }); },
                ["pickClipsFolder"] = PickClipsFolder,
                ["setRecorderEnabled"] = SetRecorderEnabled,
                ["setMode"] = SetMode,
                ["getRunningApps"] = _ => Task.FromResult<object?>(new { apps = recorder.RunningApplications() }),
                ["getSettings"] = GetSettings,
                ["getRecommendedBitrate"] = GetRecommendedBitrate,
                ["saveSettings"] = SaveSettings,
                ["completeOnboarding"] = CompleteOnboarding,
                ["getAudioDevices"] = GetAudioDevices,
                ["startMicMeter"] = StartMicMeter,
                ["stopMicMeter"] = _ => { StopMeter(); return Task.FromResult<object?>(new { }); },
                ["getDiagnostics"] = GetDiagnostics,
                ["copyDiagnostics"] = CopyDiagnostics,
                ["openLogsFolder"] = _ => { host.OpenFolder(FileLog.DefaultFolder); return Task.FromResult<object?>(new { }); },
                ["checkForUpdates"] = CheckForUpdates,
                ["installUpdate"] = InstallUpdate,
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
            catch (Exception ex) when (ex is UploadException or UpdateException)
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

        public StatusDto Status() => StatusPresenter.Build(config, recorder, updates?.Latest?.Version, updates?.Latest?.ReleaseUrl);

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

        /// <summary>
        /// Shows a native folder picker and, if someone chose one, moves every clip there and points the library at
        /// it from now on. Cancelling changes nothing. Runs the move on a background thread, since it can copy real
        /// data across drives; the dialog itself already blocked the UI thread while it was open.
        /// </summary>
        private async Task<object?> PickClipsFolder(JObject request)
        {
            string? chosen = host.PickFolder("Choose a clips folder", config.OutputFolder);
            if (string.IsNullOrWhiteSpace(chosen)) return new { changed = false };

            var result = await Task.Run(() => library.ChangeFolder(chosen));
            saveConfig(config);
            PushEvent("libraryChanged");
            PushStatus();

            return new { changed = true, folder = result.Folder, moved = result.Moved, failed = result.Failed };
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
                try { await uploader.RenameAsync(settings, updated.RemoteId!, updated.Title, updated.OwnerToken); }
                catch (UploadException ex) { remoteError = ex.Message; }
            }
            return new { clip = Dto(updated), remoteError };
        }

        /// <summary>
        /// Take the clip's uploaded copy off the server and make it an ordinary local clip again. Only the app that uploaded
        /// a clip holds its owner token, so this can only ever remove your own uploads.
        /// </summary>
        private async Task RemoveFromServer(ClipRecord clip)
        {
            if (!clip.IsUploaded) throw new InvalidOperationException("That clip isn't uploaded.");
            if (string.IsNullOrEmpty(clip.OwnerToken) || string.IsNullOrEmpty(clip.RemoteId))
                throw new InvalidOperationException("This clip was uploaded by an older version of Chrono, so it can't be removed from here. Whoever runs the server can remove it.");

            var settings = UploadRules.SettingsFrom(config)
                ?? throw new UploadException("The server address and upload key aren't set, so Chrono can't reach the uploaded copy. Add them in Settings.");

            await uploader.RemoveAsync(settings, clip.RemoteId!, clip.OwnerToken!);
            library.ClearUpload(clip.Id);
        }

        private async Task<object?> RemoveUpload(JObject request)
        {
            var clip = Need(request);
            await RemoveFromServer(clip);
            return new { clip = Dto(library.Find(clip.Id)!) };
        }

        private async Task<object?> DeleteClip(JObject request)
        {
            var clip = Need(request);

            // "Also remove it from the cloud" goes first: if the server can't be reached, nothing has been deleted and the
            // person can try again, or delete only the copy on this PC.
            if (request.Value<bool?>("removeUpload") ?? false) await RemoveFromServer(clip);

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

                library.MarkUploaded(clip.Id, result.Link, result.ClipId, result.OwnerToken);
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

        private async Task<object?> GetAudioDevices(JObject _)
        {
            // Core Audio is COM; ask off the UI thread so a slow driver can't freeze the window.
            var (speakers, microphones) = await Task.Run(() => (AudioDevices.List(NAudio.CoreAudioApi.DataFlow.Render), AudioDevices.List(NAudio.CoreAudioApi.DataFlow.Capture)));
            return new { speakers, microphones };
        }

        private async Task<object?> StartMicMeter(JObject request)
        {
            StopMeter();
            string? id = request.Value<string>("deviceId");
            string? problem = null;
            var started = await Task.Run(() => MicMeter.TryStart(id, level => PushEvent("micLevel", new { level }), out problem));
            if (started == null) throw new InvalidOperationException(problem ?? "Couldn't listen to the microphone.");
            meter = started;
            return new { };
        }

        private void StopMeter()
        {
            var running = meter;
            meter = null;
            running?.Dispose();
        }

        // -------------------------------------------------------------- diagnostics

        // The numbers cost something to read (a WMI query for GPU usage, a look at FFmpeg's process), so this is asked for
        // only while the Diagnostics page is open, and off the window's thread.
        private async Task<object?> GetDiagnostics(JObject _)
        {
            var collector = diagnostics ?? throw new InvalidOperationException("Diagnostics aren't available.");
            return await Task.Run(() => collector.Collect());
        }

        private async Task<object?> CopyDiagnostics(JObject _)
        {
            var collector = diagnostics ?? throw new InvalidOperationException("Diagnostics aren't available.");
            string report = await Task.Run(collector.Report);
            host.CopyToClipboard(report);
            return new { };
        }

        private void OnUpdateFound(ReleaseInfo release) => PushStatus();

        public void Dispose()
        {
            StopMeter();
            if (updates != null) updates.UpdateFound -= OnUpdateFound;
        }

        /// <summary>
        /// Ends the first-run setup. Whatever the person filled in is saved; anything left out (they pressed Skip) stays as it
        /// was, so skipping everything is just an empty request. Nothing is saved if a value isn't valid.
        /// </summary>
        private Task<object?> CompleteOnboarding(JObject request)
        {
            var candidate = new RecorderConfig();
            candidate.CopyFrom(config);

            string? username = request.Value<string>("username");
            string? apiUrl = request.Value<string>("apiUrl");
            string? uploadKey = request.Value<string>("uploadKey");
            if (!string.IsNullOrWhiteSpace(username)) candidate.Username = username;
            if (!string.IsNullOrWhiteSpace(apiUrl)) candidate.ApiUrl = apiUrl;
            if (!string.IsNullOrWhiteSpace(uploadKey)) candidate.UploadKey = uploadKey;

            // Only the three fields the setup asks about are checked. Whatever else is already sitting in the config
            // (untouched here) is none of the setup's business, and Skip must always be able to finish it.
            string? problem = SettingsRules.ValidateUploadFields(candidate);
            if (problem != null) throw new InvalidOperationException(problem);

            config.Username = candidate.Username;
            config.ApiUrl = candidate.ApiUrl;
            config.UploadKey = candidate.UploadKey;
            config.OnboardingCompleted = true;
            saveConfig(config);
            PushStatus();
            return Task.FromResult<object?>(new { canUpload = UploadRules.SettingsFrom(config) != null });
        }

        private Task<object?> SaveSettings(JObject request)
        {
            var incoming = request["config"]?.ToObject<RecorderConfig>() ?? throw new InvalidOperationException("No settings were sent.");
            incoming.Hotkeys ??= config.Hotkeys;

            string? problem = SettingsRules.ValidateAndTidy(incoming);
            if (problem != null) throw new InvalidOperationException(problem);

            bool soundChanged = incoming.RecordAudio != config.RecordAudio || incoming.RecordMicrophone != config.RecordMicrophone
                || incoming.SpeakerDeviceId != config.SpeakerDeviceId || incoming.MicrophoneDeviceId != config.MicrophoneDeviceId
                || incoming.MicrophoneVolumePercent != config.MicrophoneVolumePercent;

            bool captureChanged = !string.Equals(incoming.GameCapture, config.GameCapture, StringComparison.OrdinalIgnoreCase);

            // These are read when FFmpeg starts, so a change does nothing until the recording is restarted. Restarting is
            // what the person asked for, and also what makes "automatic" forget a step it learned under the old settings.
            bool loadChanged = incoming.Fps != config.Fps || incoming.Bitrate != config.Bitrate
                || !string.Equals(incoming.Encoder, config.Encoder, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(incoming.EncoderLoad, config.EncoderLoad, StringComparison.OrdinalIgnoreCase);

            bool voiceChanged = incoming.VoiceClipEnabled != config.VoiceClipEnabled;

            // Edit the live config in place: the recorder and hotkeys hold this same instance.
            config.CopyFrom(incoming);
            saveConfig(config);
            var refused = onConfigSaved();   // hotkeys Windows wouldn't register: another program already uses those keys
            if (soundChanged || captureChanged || loadChanged) recorder.RestartRecording();   // new devices, volume or capture method start with a fresh recording
            if (voiceChanged) voice?.Reconcile(config);   // starts or stops listening for "chrono, clip that" to match
            PushStatus();
            return Task.FromResult<object?>(new { config = JObject.FromObject(config), hotkeyProblems = refused, soundRestarted = soundChanged, captureRestarted = captureChanged, loadRestarted = loadChanged });
        }

        // ---------------------------------------------------------------- updates

        /// <summary>Settings' "Check for updates" button: checks right away, regardless of the timer.</summary>
        private async Task<object?> CheckForUpdates(JObject request)
        {
            var release = updates == null ? null : await updates.CheckAsync();
            PushStatus();
            return new { updateAvailable = release?.Version, releaseUrl = release?.ReleaseUrl };
        }

        /// <summary>
        /// Downloads the installer for the newest known release and hands off to it, then closes Chrono so it can
        /// replace its files. The person has to click Install in its own wizard before that happens, which is more than
        /// enough time for Chrono to already be gone.
        /// </summary>
        private async Task<object?> InstallUpdate(JObject request)
        {
            var release = updates?.Latest ?? throw new InvalidOperationException("No update is ready to install. Check for updates first.");
            if (string.IsNullOrEmpty(release.SetupDownloadUrl))
                throw new InvalidOperationException($"Chrono {release.Version} is out, but it has nothing to install yet. Try again shortly, or get it from GitHub.");

            string path = await updateDownloader.DownloadInstallerAsync(
                release.SetupDownloadUrl, new Progress<double>(f => PushEvent("updateProgress", new { fraction = f })));

            launchInstaller(path);
            _ = Task.Delay(ExitDelay).ContinueWith(_ => host.RequestExit());

            return new { };
        }
    }
}
