using System.Drawing;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    public sealed class UiBridgeTests : IDisposable
    {
        private const string Server = "https://clips.test";

        private readonly string root = Path.Combine(Path.GetTempPath(), "chrono_bridge_" + Guid.NewGuid().ToString("N"));
        private readonly RecorderConfig config;
        private readonly FakeRecorder recorder = new();
        private readonly FakeHost host = new();
        private readonly StubServer http = new();
        private readonly ClipLibrary library;
        private readonly UiBridge bridge;
        private int saves;
        private int configSavedCallbacks;
        private IReadOnlyList<string> refusedHotkeys = Array.Empty<string>();
        private string LastRaw = "";

        public UiBridgeTests()
        {
            Directory.CreateDirectory(Path.Combine(root, "clips"));
            config = new RecorderConfig { OutputFolder = Path.Combine(root, "clips"), Username = "Oliver" };
            config.SetDefaultHotkeys();
            library = new ClipLibrary(config, Path.Combine(root, "library.json"));
            var media = new ClipMedia(config, library, () => "h264_nvenc", Path.Combine(root, "thumbs"));
            bridge = new UiBridge(config, recorder, library, media, new Uploader(new HttpClient(http)) { RetryDelay = _ => TimeSpan.Zero },
                host, () => { configSavedCallbacks++; return refusedHotkeys; }, _ => saves++,
                diagnostics: new DiagnosticsCollector(new FakeDiagnosticsSource()));
        }

        public void Dispose() { try { Directory.Delete(root, true); } catch { } }

        // -------------------------------------------------------------------- helpers

        private string AddClip(string name, int bytes = 2500)
        {
            string path = Path.Combine(config.OutputFolder, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
            return path;
        }

        private async Task<JObject> Call(string action, object? payload = null)
        {
            var request = payload == null ? new JObject() : JObject.FromObject(payload);
            request["requestId"] = 7;
            request["action"] = action;
            string? reply = await bridge.HandleAsync(request.ToString());
            Assert.NotNull(reply);
            var json = JObject.Parse(reply!);
            Assert.Equal(7, (int?)json["requestId"]);
            LastRaw = reply!;
            return json;
        }

        private async Task<JObject> Ok(string action, object? payload = null)
        {
            var reply = await Call(action, payload);
            Assert.True((bool?)reply["ok"], $"{action} failed: {reply["error"]}");
            return (JObject)reply["data"]!;
        }

        private async Task<string> Fails(string action, object? payload = null)
        {
            var reply = await Call(action, payload);
            Assert.False((bool?)reply["ok"]);
            return (string)reply["error"]!;
        }

        private async Task<string> IdOfOnlyClip()
        {
            var data = await Ok("getLibrary");
            return (string)data["clips"]![0]!["id"]!;
        }

        private void ConfigureUploads()
        {
            config.ApiUrl = Server;
            config.UploadKey = "key";
        }

        // ------------------------------------------------------------------- protocol

        [Fact]
        public async Task ARequestWithoutAnId_OrNotJson_GetsNoReply()
        {
            Assert.Null(await bridge.HandleAsync("not json"));
            Assert.Null(await bridge.HandleAsync("{\"action\":\"getStatus\"}"));
            Assert.Null(await bridge.HandleAsync("{\"id\":5,\"action\":\"getStatus\"}"));   // a payload id alone is not a request id
        }

        [Fact]
        public async Task AnUnknownRequest_FailsWithAMessage_InsteadOfHanging()
        {
            Assert.Contains("Unknown request", await Fails("makeCoffee"));
        }

        [Fact]
        public async Task ARequestForAMissingClip_SaysSo()
        {
            Assert.Contains("isn't in the library", await Fails("getClip", new { id = "nope" }));
        }

        // --------------------------------------------------------------------- status

        [Fact]
        public async Task StatusSaysWhatIsBeingRecorded()
        {
            recorder.Recording = true; recorder.Target = "Risk of rain 2";
            config.Mode = RecorderConfig.RecordingMode.Auto;

            var s = await Ok("getStatus");

            Assert.Equal("recording", (string?)s["state"]);
            Assert.Equal("Recording Risk of rain 2", (string?)s["headline"]);
            Assert.Equal("Auto", (string?)s["mode"]);
            Assert.Equal("2560x1440", (string?)s["screen"]);
            Assert.Equal("Oliver", (string?)s["username"]);
            Assert.False((bool?)s["canUpload"]);
            Assert.Equal(2, s["hotkeys"]!.Count());
            Assert.Equal(config.Hotkeys[0].Modifiers.Concat(new[] { config.Hotkeys[0].Key }), s["hotkeys"]![0]!["keys"]!.Select(k => (string?)k));
            Assert.Equal(140, (int?)s["bufferSeconds"]);
        }

        [Theory]
        [InlineData(false, false, "recording", "paused")]
        [InlineData(true, false, "idle", "idle")]
        public void StateFollowsTheRecorder(bool enabled, bool recording, string _, string expected)
        {
            config.RecorderEnabled = enabled;
            recorder.Recording = recording;
            Assert.Equal(expected, StatusPresenter.Build(config, recorder).State);
        }

        [Fact]
        public void PlainWordsForQualityAndSound()
        {
            Assert.Equal("1080p60", StatusPresenter.ClipQualityText("1920x1080", 60));
            Assert.Equal("Native, 60 FPS", StatusPresenter.ClipQualityText("native", 60));
            Assert.Equal("Game sound and microphone", StatusPresenter.AudioText(true, true));
            Assert.Equal("Game sound", StatusPresenter.AudioText(true, false));
            Assert.Equal("Microphone only", StatusPresenter.AudioText(false, true));
            Assert.Equal("No sound", StatusPresenter.AudioText(false, false));
        }

        [Fact]
        public async Task SettingTheMode_TellsTheRecorder_AndAppliesTheChosenGame()
        {
            await Ok("setMode", new { mode = "Application", app = "Risk of rain 2" });

            Assert.Equal(RecorderConfig.RecordingMode.Application, recorder.Mode);
            Assert.Equal("Risk of rain 2", recorder.Tracked);
            Assert.Contains("Unknown mode", await Fails("setMode", new { mode = "Sideways" }));
        }

        [Fact]
        public async Task PausingAndResuming_GoesToTheRecorder()
        {
            await Ok("setRecorderEnabled", new { enabled = false });
            Assert.False(recorder.Enabled);
            await Ok("setRecorderEnabled", new { enabled = true });
            Assert.True(recorder.Enabled);
        }

        // -------------------------------------------------------------------- library

        [Fact]
        public async Task TheLibraryListsClipsNewestFirst_WithSafeVideoAddresses()
        {
            AddClip("first clip #1.mp4");
            var older = AddClip("older.mp4");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-3));

            var data = await Ok("getLibrary");

            var clips = (JArray)data["clips"]!;
            Assert.Equal(2, clips.Count);
            // A '#' or a space in a file name must not break the address the page plays it from.
            Assert.Contains(clips, c => (string?)c["videoUrl"] == "https://clips.chrono/first%20clip%20%231.mp4");
            Assert.All(clips, c => Assert.Null((string?)c["link"]));
            Assert.Equal(config.OutputFolder, (string?)data["clipsFolder"]);
        }

        [Fact]
        public async Task ClipDatesAreSentAsUtcSoThePageShowsLocalTime()
        {
            AddClip("a.mp4");
            await Ok("getLibrary");
            Assert.Matches("\"createdUtc\":\"[^\"]+Z\"", LastRaw);   // 'Z' = UTC, so the page converts it to local time
        }

        // --------------------------------------------------------------------- rename

        [Fact]
        public async Task RenamingTidiesTheTitle_AndKeepsItAcrossARefresh()
        {
            AddClip("a.mp4");
            string id = await IdOfOnlyClip();

            var data = await Ok("renameClip", new { id, title = "  Triple   kill \n on the boss " });

            Assert.Equal("Triple kill on the boss", (string?)data["clip"]!["title"]);
            var again = await Ok("getLibrary");
            Assert.Equal("Triple kill on the boss", (string?)again["clips"]![0]!["title"]);
            Assert.Empty(http.Requests);   // a clip that isn't uploaded has nothing on the server to rename
        }

        [Fact]
        public async Task RenamingAnUploadedClip_AlsoRenamesItOnTheServer()
        {
            ConfigureUploads();
            AddClip("a.mp4");
            string id = await IdOfOnlyClip();
            library.MarkUploaded(id, $"{Server}/watch/abc123def456", "abc123def456");

            var data = await Ok("renameClip", new { id, title = "Nice one" });

            var patch = Assert.Single(http.Requests);
            Assert.Equal("PATCH", patch.Method);
            Assert.Equal($"{Server}/api/clips/abc123def456", patch.Url);
            Assert.Equal("Nice one", (string?)JObject.Parse(patch.Body)["title"]);
            Assert.Equal("Bearer key", patch.Authorization);
            Assert.Null((string?)data["remoteError"]);
        }

        [Fact]
        public async Task IfTheServerRenameFails_TheLocalRenameStillHappens_AndTheUserIsTold()
        {
            ConfigureUploads();
            http.PatchStatus = HttpStatusCode.Unauthorized;
            AddClip("a.mp4");
            string id = await IdOfOnlyClip();
            library.MarkUploaded(id, $"{Server}/watch/abc123def456", "abc123def456");

            var data = await Ok("renameClip", new { id, title = "Nice one" });

            Assert.Equal("Nice one", (string?)data["clip"]!["title"]);
            Assert.Contains("upload key", (string?)data["remoteError"]);
        }

        // --------------------------------------------------------------------- upload

        [Fact]
        public async Task UploadingWithoutSetUp_SaysWhatToDo()
        {
            AddClip("a.mp4");
            string id = await IdOfOnlyClip();

            Assert.Contains("Settings", await Fails("uploadClip", new { id }));
            Assert.Empty(http.Requests);
        }

        [Fact]
        public async Task UploadingSendsTheTitle_ReportsProgress_KeepsTheLink_AndCopiesIt()
        {
            ConfigureUploads();
            AddClip("a.mp4");
            string id = await IdOfOnlyClip();
            await Ok("renameClip", new { id, title = "Triple kill" });

            var data = await Ok("uploadClip", new { id });

            var clip = data["clip"]!;
            Assert.Equal($"{Server}/watch/abc123def456", (string?)clip["link"]);
            var create = JObject.Parse(http.Requests[0].Body);
            Assert.Equal("Triple kill", (string?)create["title"]);
            Assert.Equal("a.mp4", (string?)create["filename"]);
            Assert.Equal($"{Server}/watch/abc123def456", host.Clipboard);
            Assert.Contains(host.Posted, m => JObject.Parse(m)["event"]?.ToString() == "uploadProgress");
            Assert.True(library.Find(id)!.IsUploaded);
            Assert.Equal("abc123def456", library.Find(id)!.RemoteId);
        }

        [Fact]
        public async Task AFailedUpload_LeavesTheClipAlone_WithTheServersReason()
        {
            ConfigureUploads();
            http.RejectKey = true;
            AddClip("a.mp4");
            string id = await IdOfOnlyClip();

            string error = await Fails("uploadClip", new { id });

            Assert.Contains("upload key", error);
            Assert.False(library.Find(id)!.IsUploaded);
        }

        [Fact]
        public async Task CopyLink_NeedsAnUploadedClip()
        {
            AddClip("a.mp4");
            string id = await IdOfOnlyClip();
            Assert.Contains("hasn't been uploaded", await Fails("copyLink", new { id }));

            library.MarkUploaded(id, "https://x.test/watch/zzz", "zzz");
            await Ok("copyLink", new { id });
            Assert.Equal("https://x.test/watch/zzz", host.Clipboard);
        }

        // ------------------------------------------------------------------- settings

        [Fact]
        public async Task SettingsAreEditedInPlace_AndSavedOnce()
        {
            var sent = JObject.FromObject(config);
            sent["Fps"] = 120;
            sent["Bitrate"] = 15000;
            sent["Username"] = "  Ollie  ";

            var data = await Ok("saveSettings", new { config = sent });

            Assert.Equal(120, config.Fps);
            Assert.Equal(15000, config.Bitrate);
            Assert.Equal("Ollie", config.Username);
            Assert.Equal(120, (int?)data["config"]!["Fps"]);
            Assert.Equal(1, saves);
            Assert.Equal(1, configSavedCallbacks);
        }

        [Fact]
        public async Task ChangingTheMicrophoneOrItsVolume_RestartsTheRecordingSoItTakesEffect()
        {
            var sent = JObject.FromObject(config);
            sent["MicrophoneVolumePercent"] = 300;

            var data = await Ok("saveSettings", new { config = sent });

            Assert.Equal(300, config.MicrophoneVolumePercent);
            Assert.Equal(1, recorder.AudioRestarts);
            Assert.True((bool?)data["soundRestarted"]);
        }

        [Fact]
        public async Task ChangingHowGamesAreCaptured_RestartsTheRecording()
        {
            var sent = JObject.FromObject(config);
            sent["GameCapture"] = "monitor";

            var data = await Ok("saveSettings", new { config = sent });

            Assert.Equal("monitor", config.GameCapture);
            Assert.Equal(1, recorder.AudioRestarts);
            Assert.True((bool?)data["captureRestarted"]);
            Assert.False((bool?)data["soundRestarted"]);
        }

        [Fact]
        public async Task AnUnknownCaptureMethod_IsRefused()
        {
            var sent = JObject.FromObject(config);
            sent["GameCapture"] = "screenshot-everything";

            Assert.Contains("capture games", await Fails("saveSettings", new { config = sent }));
            Assert.Equal("window", config.GameCapture);
        }

        [Fact]
        public async Task ChangingSomethingUnrelated_DoesNotInterruptTheRecording()
        {
            var sent = JObject.FromObject(config);
            sent["ShowNotifications"] = !config.ShowNotifications;
            sent["Username"] = "someone";

            var data = await Ok("saveSettings", new { config = sent });

            Assert.Equal(0, recorder.AudioRestarts);
            Assert.False((bool?)data["soundRestarted"]);
            Assert.False((bool?)data["loadRestarted"]);
        }

        [Theory]
        [InlineData("Fps", 30)]
        [InlineData("Bitrate", 15000)]
        [InlineData("Encoder", "libx264")]
        [InlineData("EncoderLoad", "light")]
        public async Task ChangingWhatFfmpegIsStartedWith_RestartsTheRecordingSoItTakesEffect(string setting, object value)
        {
            // These are read when FFmpeg starts. Before, a new frame rate did nothing until the next game launch.
            var sent = JObject.FromObject(config);
            sent[setting] = JToken.FromObject(value);

            var data = await Ok("saveSettings", new { config = sent });

            Assert.Equal(1, recorder.AudioRestarts);
            Assert.True((bool?)data["loadRestarted"]);
        }

        // ---------------------------------------------------------------- diagnostics

        [Fact]
        public async Task TheDiagnosticsPageGetsItsSections_InTheShapeItReads()
        {
            var data = await Ok("getDiagnostics");

            var sections = (JArray)data["sections"]!;
            Assert.Equal(new[] { "Recording", "Performance", "This PC" }, sections.Select(x => (string)x["title"]!));

            var rows = sections[0]["rows"]!.ToDictionary(r => (string)r["label"]!, r => (string)r["value"]!);
            Assert.Equal("Recording Deadlock", rows["Status"]);
            Assert.Equal("NVIDIA NVENC H.264", rows["Encoder"]);

            Assert.NotEmpty((JArray)data["findings"]!);
            Assert.Equal("Recording started", ((string)data["events"]![0]!).Substring(9));
            Assert.False(string.IsNullOrEmpty((string?)data["version"]));
        }

        [Fact]
        public async Task CopyingTheReport_PutsPlainTextOnTheClipboard_WithNothingPersonalInIt()
        {
            await Ok("copyDiagnostics");

            Assert.NotNull(host.Clipboard);
            Assert.Contains("[Recording]", host.Clipboard);
            Assert.Contains("Encoder: NVIDIA NVENC H.264", host.Clipboard);
            Assert.DoesNotContain(config.Username, host.Clipboard);   // the name shown on shared links
            Assert.DoesNotContain(root, host.Clipboard);              // folders
        }

        [Fact]
        public async Task TheLogFolderCanBeOpenedFromTheDiagnosticsPage()
        {
            await Ok("openLogsFolder");

            Assert.Equal(FileLog.DefaultFolder, host.Opened);
        }

        [Fact]
        public async Task AnEncoderLoadThatIsntOnTheList_IsRefused()
        {
            var sent = JObject.FromObject(config);
            sent["EncoderLoad"] = "turbo";

            string error = await Fails("saveSettings", new { config = sent });

            Assert.Contains("how hard recording", error);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(501)]
        public async Task ANonsenseMicrophoneVolume_IsRefused(int percent)
        {
            var sent = JObject.FromObject(config);
            sent["MicrophoneVolumePercent"] = percent;

            Assert.Contains("microphone volume", await Fails("saveSettings", new { config = sent }));
            Assert.Equal(100, config.MicrophoneVolumePercent);
        }

        [Fact]
        public async Task TheAudioDeviceLists_ComeBackAsSpeakersAndMicrophones()
        {
            var data = await Ok("getAudioDevices");

            Assert.IsType<JArray>(data["speakers"]);
            Assert.IsType<JArray>(data["microphones"]);
            foreach (var d in data["speakers"]!) { Assert.False(string.IsNullOrEmpty((string?)d["id"])); Assert.False(string.IsNullOrEmpty((string?)d["name"])); }
        }

        [Fact]
        public async Task HotkeysWindowsRefused_AreReportedBackToThePage()
        {
            refusedHotkeys = new[] { "Quick Clip (Control + F8)" };
            var sent = JObject.FromObject(config);

            var data = await Ok("saveSettings", new { config = sent });

            Assert.Equal(new[] { "Quick Clip (Control + F8)" }, data["hotkeyProblems"]!.Select(t => (string?)t));
        }

        [Fact]
        public async Task SavingSettingsWithoutHotkeys_KeepsTheExistingOnes()
        {
            var sent = JObject.FromObject(config);
            sent.Remove("Hotkeys");

            await Ok("saveSettings", new { config = sent });

            Assert.Equal(2, config.Hotkeys.Count);
        }

        [Theory]
        [InlineData("ApiUrl", "http://evil.example.com", "server address")]
        [InlineData("Fps", 5, "frame rate")]
        [InlineData("Bitrate", 200, "bitrate")]
        [InlineData("Resolution", "huge", "clip size")]
        [InlineData("Encoder", "quantum", "encoder")]
        public async Task BadSettings_AreRefused_AndNothingIsChanged(string field, object value, string mention)
        {
            var sent = JObject.FromObject(config);
            sent[field] = JToken.FromObject(value);
            int fpsBefore = config.Fps;

            string error = await Fails("saveSettings", new { config = sent });

            Assert.Contains(mention, error);
            Assert.Equal(fpsBefore, config.Fps);
            Assert.Equal(0, saves);
            Assert.Equal(0, configSavedCallbacks);
        }

        [Fact]
        public async Task TheRecommendedBitrate_ComesFromTheScreenAndFrameRate()
        {
            var at60 = await Ok("getRecommendedBitrate", new { fps = 60 });
            var at30 = await Ok("getRecommendedBitrate", new { fps = 30 });

            Assert.Equal(20000, (int?)at60["kbps"]);
            Assert.Equal(10000, (int?)at30["kbps"]);
            Assert.Equal("2560x1440", (string?)at60["size"]);
        }

        [Fact]
        public async Task GetSettingsSendsTheWholeConfig_ForTheSameObjectToComeBack()
        {
            var data = await Ok("getSettings");

            var sent = (JObject)data["config"]!;
            Assert.Equal("Oliver", (string?)sent["Username"]);
            Assert.NotNull(sent["Hotkeys"]);
            Assert.NotNull(sent["TempFolder"]);   // settings the page never shows still travel, so they aren't reset on save
            Assert.NotNull(data["recommended"]);
        }

        // ------------------------------------------------------------------ test doubles

        private sealed class FakeRecorder : IRecorder
        {
            public bool Recording;
            public string Target = "no game running";
            public bool Enabled = true;
            public RecorderConfig.RecordingMode? Mode;
            public string? Tracked;

            public bool IsRecordingActive => Recording;
            public string TargetName => Target;
            public string EncoderName => "h264_nvenc";
            public Size RecordingSize() => new(2560, 1440);
            public void SetRecorderEnabled(bool enabled) => Enabled = enabled;
            public void SetRecordingMode(RecorderConfig.RecordingMode mode) => Mode = mode;
            public void SetTrackedApplication(string appName) => Tracked = appName;
            public int AudioRestarts;
            public void RestartRecording() => AudioRestarts++;
            public IReadOnlyList<string> RunningApplications() => new[] { "Discord", "Risk of rain 2" };
        }

        private sealed class FakeHost : IUiHost
        {
            public readonly List<string> Posted = new();
            public string? Clipboard;
            public string? Opened;
            public void Post(string json) => Posted.Add(json);
            public void CopyToClipboard(string text) => Clipboard = text;
            public void ShowInFolder(string path) { }
            public void OpenFolder(string path) => Opened = path;
        }

        private sealed class FakeDiagnosticsSource : IDiagnosticsSource
        {
            public RecorderFacts GetFacts() => new(
                Recording: true, Target: "Deadlock", Method: CaptureMethod.WindowCapture, Size: new Size(2560, 1440), ConfiguredFps: 60, EffectiveFps: 60,
                Encoder: "h264_nvenc", LoadLevel: 0, LoadSetting: "auto", GovernorActive: true, BitrateKbps: 19900, BufferSeconds: 140, FfmpegPid: 0,
                StartedUtc: DateTime.UtcNow.AddMinutes(-3), Stats: new EncodeStats(9000, 59.9, 120, 0, 0.998, 0, 150, DateTime.UtcNow),
                EncoderNote: "", AudioSources: new[] { "game sound" }, Events: new[] { "12:00:01 Recording started" },
                ScreenAdapterName: "NVIDIA GeForce RTX 4080", ScreenAdapterVendorId: 0x10DE);
        }

        private sealed record Recorded(string Method, string Url, string Body, string? Authorization);

        /// <summary>A stand-in for the clip server: accepts creates, parts, completes and renames.</summary>
        private sealed class StubServer : HttpMessageHandler
        {
            public readonly List<Recorded> Requests = new();
            public bool RejectKey;
            public HttpStatusCode PatchStatus = HttpStatusCode.OK;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                string body = request.Content == null ? "" : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(ct));
                Requests.Add(new Recorded(request.Method.Method, request.RequestUri!.ToString(), request.Content?.Headers.ContentType?.MediaType == "application/json" ? body : "", request.Headers.Authorization?.ToString()));

                if (RejectKey) return Json(HttpStatusCode.Unauthorized, new { error = "Missing or invalid upload key" });

                string path = request.RequestUri.AbsolutePath;
                if (request.Method.Method == "PATCH")
                    return PatchStatus == HttpStatusCode.OK ? Json(HttpStatusCode.OK, new { id = "abc123def456" }) : Json(PatchStatus, new { error = "Missing or invalid upload key" });
                if (path == "/api/clips") return Json(HttpStatusCode.Created, new { id = "abc123def456", partSize = 1000, link = $"{Server}/watch/abc123def456" });
                if (path.Contains("/parts/")) return Json(HttpStatusCode.OK, new { partNumber = int.Parse(path[(path.LastIndexOf('/') + 1)..]), etag = "e" });
                return Json(HttpStatusCode.OK, new { link = $"{Server}/watch/abc123def456" });
            }

            private static HttpResponseMessage Json(HttpStatusCode status, object body)
                => new(status) { Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json") };
        }
    }

    public class SettingsRulesTests
    {
        private static RecorderConfig Valid()
        {
            var c = new RecorderConfig();
            c.SetDefaultHotkeys();
            return c;
        }

        [Fact]
        public void TheDefaultSettingsAreValid() => Assert.Null(SettingsRules.ValidateAndTidy(Valid()));

        [Fact]
        public void AnEmptyServerAddressIsFine_ItJustMeansNoUploading()
        {
            var c = Valid(); c.ApiUrl = "";
            Assert.Null(SettingsRules.ValidateAndTidy(c));
        }

        [Fact]
        public void TheUsernameAndKeyAreTrimmed_AndTheNameIsCapped()
        {
            var c = Valid(); c.Username = "  " + new string('a', 50) + "  "; c.UploadKey = "  key\n";
            SettingsRules.ValidateAndTidy(c);
            Assert.Equal(32, c.Username.Length);
            Assert.Equal("key", c.UploadKey);
        }

        [Theory]
        [InlineData("", "PageUp", 30, "name")]
        [InlineData("Clip", "", 30, "needs keys")]
        [InlineData("Clip", "Fn", 30, "can't use the key")]
        [InlineData("Clip", "PageUp", 3, "between")]
        [InlineData("Clip", "PageUp", 5000, "between")]
        public void BadHotkeysAreRefused(string name, string key, int seconds, string mention)
        {
            var c = Valid();
            c.Hotkeys = new List<HotkeyConfig> { new() { Name = name, Key = key, Modifiers = new List<string> { "Control" }, ClipLengthSeconds = seconds } };
            Assert.Contains(mention, SettingsRules.ValidateAndTidy(c));
        }

        [Fact]
        public void TwoHotkeysOnTheSameKeys_AreRefused_EvenIfWrittenDifferently()
        {
            var c = Valid();
            c.Hotkeys = new List<HotkeyConfig>
            {
                new() { Name = "A", Key = "PageUp", Modifiers = new List<string> { "Control" }, ClipLengthSeconds = 30 },
                new() { Name = "B", Key = "pageup", Modifiers = new List<string> { "ctrl" }, ClipLengthSeconds = 60 },
            };
            Assert.Contains("same keys", SettingsRules.ValidateAndTidy(c));
        }

        [Fact]
        public void TheSameKeyWithDifferentModifiers_IsFine()
        {
            var c = Valid();
            c.Hotkeys = new List<HotkeyConfig>
            {
                new() { Name = "A", Key = "PageUp", Modifiers = new List<string> { "Control" }, ClipLengthSeconds = 30 },
                new() { Name = "B", Key = "PageUp", Modifiers = new List<string> { "Control", "Shift" }, ClipLengthSeconds = 60 },
            };
            Assert.Null(SettingsRules.ValidateAndTidy(c));
        }
    }
}
