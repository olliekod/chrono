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
- Requires `ffmpeg` and `ffprobe` on `PATH` (the full FFmpeg build ships both) and the Edge WebView2 runtime.
- Tests cover `ClipPlanner`, `EncoderProfile`, `HotkeyParser`, config handling, `UploadRules` and `Uploader` (against a fake HTTP handler). Anything that spawns FFmpeg or needs a window is verified by hand, so keep new logic in small pure classes where you can.
- It is an `Exe` that logs heavily with `Console.WriteLine`; run it from a terminal to see the log.
- `UI/**` is copied to the output dir, so edits to the HTML only take effect after a build/run.
- User config lives at `%APPDATA%\Chrono\config.json`. Delete it to regenerate defaults (including GPU encoder auto-detection).

## Client architecture

`Program.Main` wires four long-lived objects that all share one `RecorderConfig` instance: `Recorder`, `HotkeyManager`, and the `WebViewHost` form (plus `ConfigManager` for persistence).

**Recording pipeline (`Recorder.cs`).** FFmpeg (`gdigrab` on the whole desktop) records fixed 10-second MPEG-TS segments into `TempFolder`. Each segment is a separate FFmpeg process; its `Exited` event queues the segment and starts the next one. The queue is guarded by `segmentLock` (FFmpeg exit thread, monitor timer and UI thread all touch it). `CleanupOldSegments` trims it to `RecorderConfig.RequiredBufferSeconds`, which is the larger of the `BufferDurationSeconds` setting and the longest hotkey plus two segments.

**Saving a clip.** `SaveClip` snapshots the finished segments plus the segment still being written, then `ClipPlanner` (pure, unit-tested) picks the newest segments and a seek offset. The segments are joined with the FFmpeg concat demuxer using `-ss` before `-i` and `-c copy`, and written to `OutputFolder` with a timestamped name.

Why it is built this way, since each part was a bug once:
- Segments are `.ts` with `-flush_packets 1` because an MP4 is unreadable until FFmpeg finishes it. TS can be read mid-write, which is how the newest seconds make it into a clip.
- The in-progress segment's length is measured with `ffprobe`. Its wall-clock age overstates what is on disk by about 2s (TS mux and NVENC delay).
- Copy mode can only start on a keyframe and drops everything before the first one at or after `-ss`. So the plan asks for `EncoderProfile.KeyframeIntervalSeconds` of extra footage, and clips come out between N and about N+2s, never shorter. Keyframes are forced by time (`-force_key_frames`), because `-g` counts frames and `gdigrab` drops them.
- Encoder flags live in `EncoderProfile`: presets are per-encoder (`-preset p4` is NVENC-only).

**Sharing (`Uploader.cs`, `UploadRules.cs`, `Notifier.cs`, `Program.ShareAsync`).** After a clip is saved, `Uploader` runs the Worker's protocol: `POST /api/clips` (the server picks the part size), `PUT` each part, `POST .../complete`, and the link comes back. Each part retries on its own (network errors, 5xx, 429); 4xx errors fail at once with the server's message. `UploadRules` decides whether uploading is configured and cleans user input: usernames become `[A-Za-z0-9_-]{1,32}`, and plain `http://` server addresses are refused except on loopback so the upload key never crosses the network in clear text. Results and errors go through `Notifier` (tray balloon tips), not modal dialogs, so nothing pops over a game. The old default server (`chrono-clips.fly.dev`) is treated as unconfigured and cleared from old config files.

**When it records.** A 1-second timer polls the foreground process (`WindowDetector`, user32 P/Invoke). In `Application` mode recording starts and stops as the selected app gains and loses focus; in `Display` mode it runs whenever the recorder is enabled. Application mode only gates start/stop; capture is always the full desktop.

**Hotkeys (`HotkeyManager.cs`, `HotkeyParser.cs`).** Uses `RegisterHotKey` against a hidden `NativeWindow`; the ID is the list index + 1, and pressed IDs are resolved through a dictionary of what was actually registered. Saving settings raises `WebViewHost.ConfigSaved`, and `Program` responds with `ReloadHotkeys()`, so no restart is needed.

**UI bridge (`WebViewHost.cs` + `UI/*.html`).** Two borderless WebView2 windows (main `index.html`, settings `settings.html`). JS sends `chrome.webview.postMessage({action: ...})`, and the C# `switch` on `action` handles it. C# replies with `PostWebMessageAsJson`, and anything touching the WebView must be marshalled onto the UI thread (`InvokeRequired`). Window dragging is implemented through `startDrag`/`dragWindow`/`stopDrag` messages because the form has no title bar.

**Config (`Config.cs`).** Newtonsoft.Json. `Hotkeys` is deliberately initialised to `null` with `ObjectCreationHandling.Replace`, and defaults are applied by `SetDefaultHotkeys()`. This is the fix for a bug where deserialisation appended saved hotkeys to the default list, duplicating them. Don't give the property an initialiser. The config is one shared instance: settings are applied with `RecorderConfig.CopyFrom` (edit in place), never by replacing the object, or the recorder and hotkey manager would keep the old one.

## Client gotchas

- `gdigrab` is CPU-bound and delivered only ~21 fps when asked for 30 at 1280x720 on an RTX 4080 machine. Segments hold real time but fewer frames than the configured fps. Replacing the capture path is planned.
- Each segment is a new FFmpeg process, so there is a short gap in footage at every segment boundary. A single continuous encode would remove it.
- No audio is captured yet.
- The settings page (`UI/settings.html`) builds the outgoing config field by field. A new config property that isn't added there is silently reset to its default on every save.
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
