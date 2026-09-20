# Development notes

Notes for anyone working on the code in this repository.

## What this is

Chrono is a game-clip recorder in two independent halves that talk over HTTP:

- `client/ChronoRecorder/` — Windows-only .NET 8 WinForms app. Runs a rolling FFmpeg buffer, saves the last N seconds on a global hotkey, uploads the clip, and puts a share link on the clipboard. Hosts a WebView2 UI.
- `worker/` — the backend: a TypeScript Cloudflare Worker with an R2 bucket for video and a D1 database for metadata. It serves a `/watch/:id` page with the Open Graph tags Discord reads to embed the video.
- `backend/` — the legacy FastAPI/Fly.io backend. Superseded by `worker/`; kept only until the Worker is deployed and confirmed, then to be deleted. Don't build on it.

Both halves have tests (xUnit for the client, vitest for the Worker). There is no linter.

## Commands

Worker (run from `worker/`):
```
npm install
npm test                      # vitest, runs inside the Workers runtime with local D1/R2
npm run typecheck             # wrangler types + tsc for src and tests
npx vitest run test/upload.test.ts -t "wrong size"     # one file / one test name
npx wrangler d1 migrations apply chrono --local        # schema for local dev
npx wrangler dev --port 8799                           # local server
npx wrangler deploy
```
- Production: `https://your-server.workers.dev`, on the Cloudflare account for the account owner (D1 database `chrono`, R2 bucket `chrono-clips`; their IDs are in `wrangler.jsonc`, and they are not secrets). Schema changes ship with `npx wrangler d1 migrations apply chrono --remote` before `npx wrangler deploy`.
- The production upload key is in `worker/.prod-upload-key` (git-ignored). Set or rotate it with `npx wrangler secret put UPLOAD_KEY < .prod-upload-key`, and paste the same value into the app's Settings.
- Cloudflare's bot filter answers `403 error code: 1010` to clients with a default library user-agent (Python's urllib, for one). The app sends `Chrono/1.0`, which passes; give any script a User-Agent too.
- Local secrets live in `worker/.dev.vars` (git-ignored; needs `UPLOAD_KEY`). In production set it with `npx wrangler secret put UPLOAD_KEY`; never pass secret values on the command line.
- Use a non-default `--port` for `wrangler dev`: 8787 is often taken, and on Windows two listeners can share a port, so requests silently go to the wrong server.
- `worker/worker-configuration.d.ts` is generated (`wrangler types`) and git-ignored. Regenerate it after editing `wrangler.jsonc` or adding `src/index.ts`, or the test types break.

Client (run from `client/ChronoRecorder/`):
```
dotnet build
dotnet run
dotnet test ../ChronoRecorder.Tests                                  # xUnit, pure logic only
dotnet test ../ChronoRecorder.Tests --filter "FullyQualifiedName~ClipPlannerTests"   # one class
```
- If Chrono is running, `dotnet build`/`dotnet test` fail with "file is locked by ChronoRecorder" because the running app holds `bin/Debug`. Add `-c Release` (a separate folder) instead of stopping the user's app.
- Requires `ffmpeg` and `ffprobe` on `PATH` (the full FFmpeg build ships both) and the Edge WebView2 runtime.
- Tests cover `ClipPlanner`, `EncoderProfile`, `CaptureCommand`, `CaptureSizing`, `SegmentTracker`, `MonitorLocator`, `AudioFeeder`, `HotkeyParser`, config handling, `UploadRules` and `Uploader` (against a fake HTTP handler). Anything that spawns FFmpeg or needs a window is verified by hand, so keep new logic in small pure classes where you can.
- It is an `Exe` that logs heavily with `Console.WriteLine`; run it from a terminal to see the log.
- `UI/**` is copied to the output dir, so edits to the HTML only take effect after a build/run.
- User config lives at `%APPDATA%\Chrono\config.json`. Delete it to regenerate defaults (including GPU encoder auto-detection).

## Client architecture

`Program.Main` wires four long-lived objects that all share one `RecorderConfig` instance: `Recorder`, `HotkeyManager`, and the `WebViewHost` form (plus `ConfigManager` for persistence).

**Recording pipeline (`Recorder.cs`, `CaptureCommand.cs`, `SegmentTracker.cs`).** One long-running FFmpeg process captures one monitor and writes rolling 10-second MPEG-TS segments into `TempFolder` (`-f segment`). `CaptureCommand` builds the arguments (pure, unit-tested); `SegmentTracker` reads the folder to tell finished segments from the one being written, and prunes the oldest beyond `RecorderConfig.RequiredBufferSeconds`, which is derived, not a setting: the longest hotkey clip plus two segments, so editing a clip length resizes the buffer. Files are named `segment_{run}_{index}.ts`, where `run` is a timestamp taken when recording started. The `Recorder` restarts FFmpeg a few times if a capture drops mid-recording, and reports through `RecordingFailed` when it gives up.

**Which monitor, and how.** `MonitorLocator` lists displays through DXGI (COM interop) and matches the chosen game's own window to an `HMONITOR` (in Display mode, the foreground window's), so only the game's monitor is recorded, not the whole desktop. `CaptureSourceChooser` then picks the method: **Desktop Duplication** (FFmpeg's `ddagrab`, frames stay on the GPU) when the monitor is on GPU 0 and not rotated, otherwise a **GDI** region grab as a fallback. If `ddagrab` dies within 5 seconds of starting, the recorder falls back to GDI automatically.

**Resolution (`CaptureSizing.cs`, `ExportCommand.cs`).** Recording is always at the monitor's native size, because that is nearly free (frames never leave the GPU): about 5% of one core at 1440p60 on an RTX 4080. Resizing *live* costs a full core (`hwdownload` + CPU `scale` measured 85-105%) and the GPU alternatives all failed (see gotchas). So the `Resolution` setting is the size of the *saved clip*, and shrinking happens when you press the hotkey, where a few seconds of work doesn't matter and the game isn't affected. The height picks the quality, the monitor's aspect ratio is kept, dimensions are even, and it never upscales (`CaptureSizing.Resolve`).

**Saving a clip.** `SaveClip` snapshots the finished segments plus the one being written, then `ClipPlanner` (pure, unit-tested) picks the newest segments and a seek offset. `ExportPlanner` then chooses one of three `ExportMode`s, and `ExportCommand` builds the FFmpeg command (all pure and tested):
- **Copy** (no resize needed): segments are joined with the concat demuxer, `-ss` before `-i`, `-c copy`. Instant, but copy mode can only start on a keyframe, so clips run N to about N+2.5 s.
- **Gpu** (NVENC recordings): `-c:v h264_cuvid -resize WxH` decodes *and resizes* inside NVIDIA's decoder, then NVENC encodes. Nothing touches the CPU (1.3 s of CPU time for a 20 s clip, against 11.2 s for CPU scaling).
- **Cpu** (fallback, and any non-NVIDIA encoder): `scale` filter then the configured encoder. It is multi-threaded, so it takes about the same wall time, but a burst of many cores.
Re-encoded clips are cut to exactly the requested length. If the GPU export fails, `Recorder.ExportClip` deletes the partial file and falls back to the CPU export. `PreferGpuExport` exists only to test that fallback.

Why it is built this way, since each part was a bug once:
- The old pipeline was `gdigrab` of the whole virtual desktop, restarted as a new FFmpeg process every 10 seconds. On a 2560x1440 + rotated 1440x2560 setup that is a 4000x2560 grab: 134% of a core, ~20 fps delivered instead of 60, and 216 stalls over 40 ms in 12 seconds, which made games hitch. A single `ddagrab` process measured 4.4% of a core, 59 fps, no stalls.
- Segments are `.ts` with `flush_packets=1` because an MP4 is unreadable until FFmpeg finishes it. TS can be read mid-write, which is how the newest seconds make it into a clip.
- The in-progress segment's length is measured with `ffprobe`. Its wall-clock age overstates what is on disk by about 2s (TS mux and NVENC delay).
- Copy mode can only start on a keyframe and drops everything before the first one at or after `-ss`. So the plan asks for `EncoderProfile.KeyframeIntervalSeconds` of extra footage, and clips come out between N and about N+2.5s, never shorter. Keyframes are forced by time (`-force_key_frames`), which also makes the segment muxer split on exact 10.000s boundaries.
- Encoder flags live in `EncoderProfile`: presets are per-encoder (`-preset p4` is NVENC-only). Only NVENC was verified on real hardware; AMF and QSV are untested.

**Audio (`AudioCapture.cs`, `AudioFeeder.cs`, `AudioInput.cs`).** FFmpeg on Windows can't capture system sound (it only sees microphones through DirectShow), so sound comes from Windows itself: NAudio's WASAPI loopback of the default output (the game, Discord voice, music), plus optionally the default microphone. `AudioCapture` runs each source into a named pipe that FFmpeg reads as a raw audio input, and `CaptureCommand` mixes several with `amix` and encodes stereo AAC. The pipes must exist before FFmpeg starts, so `Recorder.LaunchFfmpeg` creates the captures first. A missing device or unsupported format only drops that source (a `Warning` event and a notification); it never stops the recording. `RecordAudio` is on by default and `RecordMicrophone` off, since recording a mic is a privacy decision.

**Keeping sound and picture in sync is the hard part, and the design comes from measurement.** Picture and sound are both timed from one shared instant, T0 (`Recorder` takes it before creating the audio sources). Video frames are stamped `setpts=(time(0)-T0)/TB` with `-fps_mode cfr`; `AudioFeeder` places every chunk of sound in the stream at (its capture time - T0). What was tried, in order, each measured with a rig that flashes a monitor white while playing a 1 kHz tone:
- Plain pipe: sound **0.4-0.9 s early**, and varying per run. FFmpeg zeroes each input's clock at that input's own first data, and it spends 3-5 s starting up before it reads any audio, so the backlog plays back as a head start.
- Timestamping frames and audio by wall-clock arrival: warnings, mangled timelines.
- Waiting for FFmpeg to start reading, then starting the audio clock: sound **0.2-0.6 s early**, still variable.
- Placing sound by *write* time: a constant ~0.5 s **late**. FFmpeg reads the pipe in lumps while it also handles video, so captured sound waited 100-400 ms in the queue and inherited that as delay.
- Placing sound by *capture* time (current design): **+1 to +23 ms** over repeated runs, and +1 ms in a clip saved through the whole segment/join path. Nothing that delays the writing can shift it.

Details that matter: the pipe has a 4 KB buffer so writes block while FFmpeg starts (the feeder catches up to "now" when it unblocks instead of playing the backlog); loopback capture delivers nothing while nothing plays (and on the dev machine also delivers continuous zero buffers), so the feeder pads silence to keep the stream on the clock; the first sound after silence is placed exactly, but while sound keeps flowing chunks are written back to back and only realigned if they drift more than 250 ms (idle padding also waits until the stream is ~400 ms behind the clock). An earlier 40 ms tolerance realigned to every wobble in WASAPI's delivery times, which punched a ~70 ms hole into the sound every ~200 ms and dropped 10-20% of it: real recordings sounded like the audio was "bobbing in and out", while the synthetic tone rig (perfectly regular chunks) never showed it. `AudioFeederTests` has a lumpy-delivery test for this; a real clip can be checked for runs of exact-zero samples; `AudioDelayMs` in the config trims any residual constant offset.

To re-measure sync, flash a full-screen window white on a monitor that is being recorded while a tone starts, then find both in the saved file. Do it on a monitor with no fullscreen game: a fullscreen game draws over the flash. Play the tone from an already-running low-latency render stream (`WasapiOut`), not `SoundPlayer`, whose start-up delay under load (a game running) is hundreds of ms. Find the tone by frequency (Goertzel at 1 kHz), not by loudness, because game audio may be present. Injecting fake audio into the feeder while the real device is also delivering data adds up: the device fills the timeline, so each injected beep shifts everything later.

**When it records.** A 1-second timer polls the foreground process (`WindowDetector`, user32 P/Invoke). In `Application` mode recording starts and stops as the selected app gains and loses focus; in `Display` mode it runs whenever the recorder is enabled. Application mode only gates start/stop; capture is always the full desktop.

**Hotkeys (`HotkeyManager.cs`, `HotkeyParser.cs`).** Uses `RegisterHotKey` against a hidden `NativeWindow`; the ID is the list index + 1, and pressed IDs are resolved through a dictionary of what was actually registered. Saving settings raises `WebViewHost.ConfigSaved`, and `Program` responds with `ReloadHotkeys()`, so no restart is needed.

**UI bridge (`WebViewHost.cs` + `UI/*.html`).** Two borderless WebView2 windows (main `index.html`, settings `settings.html`). JS sends `chrome.webview.postMessage({action: ...})`, and the C# `switch` on `action` handles it. C# replies with `PostWebMessageAsJson`, and anything touching the WebView must be marshalled onto the UI thread (`InvokeRequired`). Window dragging is implemented through `startDrag`/`dragWindow`/`stopDrag` messages because the form has no title bar. The main page re-requests the running-apps list every 3 s (skipped while its dropdown is open, since rebuilding options closes it), keeps the chosen game in the list as "(not running)" when it isn't, and `WebViewHost` pushes a fresh `config` message to it after Settings are saved. To test page logic without a window, run its `<script>` in Node against a fake `document` and replay `runningApps` / `config` messages.

**Config (`Config.cs`).** Newtonsoft.Json. `Hotkeys` is deliberately initialised to `null` with `ObjectCreationHandling.Replace`, and defaults are applied by `SetDefaultHotkeys()`. This is the fix for a bug where deserialisation appended saved hotkeys to the default list, duplicating them. Don't give the property an initialiser. The config is one shared instance: settings are applied with `RecorderConfig.CopyFrom` (edit in place), never by replacing the object, or the recorder and hotkey manager would keep the old one.

## Client gotchas

- Live GPU scaling does not work, and the reasons matter if someone retries. On the FFmpeg 2024-07 build, `hwmap` from `ddagrab`'s Direct3D frames to CUDA is not implemented (`Function not implemented`). On FFmpeg 9.0.2, `scale_d3d11` fails with `Could not create the texture (80070057)` (its options are `width`/`height`, not `w`/`h`). Downloading the frame and uploading it to CUDA costs 25-80% of a core and `scale_cuda` will not take BGRA. That is why scaling moved to save time.
- `-hwaccel cuda -hwaccel_output_format cuda` + `scale_cuda` fails on this machine because NVDEC refuses to open (`cuvidCreateDecoder ... CUDA_ERROR_INVALID_VALUE`). The `h264_cuvid` decoder with its own `-resize` works, which is what the GPU export uses.
- The user's game monitor is 2560x1440 landscape and the second monitor is a rotated 1440x2560, which Desktop Duplication returns un-rotated, hence the GDI route for rotated monitors. A monitor on a second GPU takes the same route.
- In Application mode recording follows whether the game is *running* (`RecordingPolicy`, checked every second), not whether it has focus, so tabbing out to Discord doesn't pause the buffer. The catch is that the buffer then also records whatever you tab to on that same monitor. It only ever lives in the temp folder for a few minutes and becomes a clip only when a hotkey is pressed.
- Sound follows the default output device only when it is *removed*: unplugging a headset restarts the recording on the new device. Changing the default output while both stay connected is not detected, and the loopback keeps capturing the old device.
- FFmpeg must be recent enough for `amix ... normalize=0` (used only when the microphone is on). FFmpeg 7.1-dev (2024-07) and 9.0.2 both have it.
- NAudio is pinned to 2.2.1: 3.x targets .NET 9 only and this project is .NET 8.
- The settings page (`UI/settings.html`) builds the outgoing config field by field. A new config property that isn't added there is silently reset to its default on every save.
- Buffer length is not a setting: `RecorderConfig.RequiredBufferSeconds` is the longest hotkey plus two segments. Old config files that still contain `BufferDurationSeconds` load fine (unknown members are ignored).
- `SaveLocalCopy` is not used yet: clips always stay in `OutputFolder`, and nothing is deleted after upload.
- Not implemented yet: the trim UI (`TODO` in `Program.OnHotkeyPressed`) and the library (`openLibrary` is a stub). `SendStatusUpdate()` (no args) sends placeholder buffer text.

## Worker architecture

`src/index.ts` is a small regex router; `handlers.ts` holds the routes, `db.ts` the D1 queries, and `auth.ts`, `validate.ts`, `html.ts`, `range.ts`, `ids.ts` are pure helpers with unit tests.

Upload flow (all routes need `Authorization: Bearer <UPLOAD_KEY>`):
1. `POST /api/clips` validates the metadata, opens an R2 multipart upload, inserts a row with `status = 'uploading'`, and returns `{id, partSize, link}`.
2. `PUT /api/clips/:id/parts/:n` uploads one part. Its size must be exactly `partSize` (the last part takes the remainder). This is enforced by piping the body through `FixedLengthStream`, not by trusting `Content-Length`.
3. `POST /api/clips/:id/complete` requires exactly parts `1..N`, completes the R2 upload, checks the final size against the declared size, and flips the row to `ready`. Repeating it is harmless.

Viewing (no auth, 12-character random ids): `GET /watch/:id` renders the page with `og:video` tags and a nonce-based CSP, `GET|HEAD /v/:id.mp4` streams from R2 with Range support (`range.ts`), and `GET /api/clips/:id` returns public metadata. Only `ready` clips are visible. The bucket stays private; video is served through the Worker.

Why chunked uploads: Workers cap request bodies at 100 MB on the free plan and a clip can exceed that, so the client sends 16 MiB parts (R2's minimum is 5 MiB, and all parts but the last must be the same size).

## Worker gotchas

- Auth is one shared key for the whole friend group and the username is client-supplied, so anyone with the key can upload under any name. Rotate by changing the secret.
- No rate limiting, and abandoned `uploading` rows are never cleaned up. R2 aborts incomplete multipart uploads after 7 days; the D1 rows are left behind.
- Local R2 (miniflare) returns ~170-character ETags and accepts a completion that lists only some parts. Real R2 uses short ETags, so keep the ETag length cap generous, and keep the explicit "all parts" check in `completeClip`.
- The vitest package is `@cloudflare/vitest-plugin` (Vitest 4+), renamed from `@cloudflare/vitest-pool-workers`. Tests reach the Worker through `exports.default.fetch` and bindings through `env`, both from `cloudflare:workers`.
- The vitest run prints a few `Can't read from request stream because client disconnected` lines for the wrong-size part tests. This is the in-process test runner; a real `wrangler dev` server logs only the intended warning.

## Legacy backend (`backend/`)

The FastAPI app. Known problems, which are why it was replaced: no volume on Fly so `clips.db` was on an ephemeral disk, `/watch` interpolated uploader-controlled text into HTML unescaped, uploads did not require a token, and the `Dockerfile`, `Procfile` and `fly.toml` disagreed on the port. Delete it once the Worker is live.
