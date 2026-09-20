# Development notes

Notes for anyone working on the code in this repository.

## What this is

Chrono is a game-clip recorder in two independent halves that talk over HTTP:

- `client/ChronoRecorder/` — Windows-only .NET 8 WinForms app. Runs a rolling FFmpeg buffer, saves the last N seconds on a global hotkey, and hosts a WebView2 UI.
- `backend/` — FastAPI service. Accepts clip uploads, stores video in Cloudflare R2, keeps metadata in SQLite, and serves a `/watch/{id}` page with Discord/Twitter embed tags. Deployed on Fly.io (`chrono-clips.fly.dev`).

There are no tests and no linter configured in either half.

## Commands

Backend (run from `backend/`):
```
python -m venv .venv && .venv\Scripts\activate
pip install -r requirements.txt
uvicorn main:app --reload --port 8000
```
- Needs a `.env` (see `config.py` for required keys: R2 credentials/endpoint/public URL, `JWT_SECRET_KEY`, optional `BASE_URL`). `Settings()` is built at import time, so a missing key fails startup.
- The `backend/venv/` directory is a macOS venv (`bin/`, python3.11) and does not work on Windows. Make a fresh one.
- Deploy: `fly deploy` from `backend/` (uses the `Dockerfile`).

Client (run from `client/ChronoRecorder/`):
```
dotnet build
dotnet run
```
- Requires `ffmpeg` on `PATH` and the Edge WebView2 runtime.
- It is an `Exe` that logs heavily with `Console.WriteLine`; run it from a terminal to see the log.
- `UI/**` is copied to the output dir, so edits to the HTML only take effect after a build/run.
- User config lives at `%APPDATA%\Chrono\config.json`. Delete it to regenerate defaults (including GPU encoder auto-detection).

## Client architecture

`Program.Main` wires four long-lived objects that all share one `RecorderConfig` instance: `Recorder`, `HotkeyManager`, and the `WebViewHost` form (plus `ConfigManager` for persistence).

**Recording pipeline (`Recorder.cs`).** FFmpeg (`gdigrab` on the whole desktop) records fixed 10-second segments into `TempFolder`. Each segment is a separate FFmpeg process; its `Exited` event queues the segment and starts the next one. `CleanupOldSegments` trims the queue to `BufferDurationSeconds`. `SaveClip` walks segments backwards until it has enough duration, then joins them with the FFmpeg concat demuxer (`-c copy`, no re-encode). The output lands in `TempFolder`, not `OutputFolder`.

**When it records.** A 1-second timer polls the foreground process (`WindowDetector`, user32 P/Invoke). In `Application` mode recording starts and stops as the selected app gains and loses focus; in `Display` mode it runs whenever the recorder is enabled. Application mode only gates start/stop; capture is always the full desktop.

**Hotkeys (`HotkeyManager.cs`).** Uses `RegisterHotKey` against a hidden `NativeWindow`. The hotkey ID is the list index + 1. Hotkeys are registered once at startup, so changes made in Settings only apply after restart (the UI tells the user this).

**UI bridge (`WebViewHost.cs` + `UI/*.html`).** Two borderless WebView2 windows (main `index.html`, settings `settings.html`). JS sends `chrome.webview.postMessage({action: ...})`, and the C# `switch` on `action` handles it. C# replies with `PostWebMessageAsJson`, and anything touching the WebView must be marshalled onto the UI thread (`InvokeRequired`). Window dragging is implemented through `startDrag`/`dragWindow`/`stopDrag` messages because the form has no title bar.

**Config (`Config.cs`).** Newtonsoft.Json. `Hotkeys` is deliberately initialised to `null` with `ObjectCreationHandling.Replace`, and defaults are applied by `SetDefaultHotkeys()`. This is the fix for a bug where deserialisation appended saved hotkeys to the default list, duplicating them. Don't give the property an initialiser.

## Client gotchas

- Saving settings does `this.config = configToSave` inside `WebViewHost` only. `Recorder` and `HotkeyManager` still hold the original config object, so most setting changes need a restart to take effect.
- `Recorder.BuildFFmpegArgs` hardcodes `-preset p4`, which is an NVENC preset, whatever `Encoder` is set to (`GpuDetector` can choose `h264_amf` / `h264_qsv`).
- Not implemented yet: client-side upload (nothing calls `/upload`, though `AutoUpload`, `ApiUrl`, and `CopyLinkToClipboard` exist in config), the trim UI (`TODO` in `Program.OnHotkeyPressed`), and the library (`openLibrary` is a stub). `SendStatusUpdate()` (no args) sends placeholder buffer text.

## Backend architecture

Four small modules: `main.py` (routes), `config.py` (pydantic-settings from `.env`), `database.py` (raw `sqlite3`), `storage.py` (boto3 against R2's S3-compatible endpoint).

Flow: `POST /upload` (multipart file plus `username` and optional metadata) validates the extension, generates a 12-char nanoid, streams the file to R2 at `clips/{username}/{id}.ext`, inserts a row, and returns `{link, clip_id, storage_url}`. `GET /watch/{id}` renders an inline HTML f-string page with `og:`/`twitter:` tags pointing straight at the R2 public URL, so playback never goes through the API. `GET /api/clip/{id}` returns the row as JSON.

Auth is minimal and advisory. `POST /auth/token` issues a 15-minute JWT to any username. `/upload` validates a Bearer token only if one is sent, and does not require one.

## Backend gotchas

- `database.py` hardcodes `DB_FILE = "clips.db"` relative to the working directory. `settings.database_url` is unused, as are `max_upload_size_mb` and `get_user_clips`/`delete_file`.
- `fly.toml` defines no volume, so `clips.db` sits on the machine's ephemeral disk.
- Column names differ from the dict keys used in `main.py`. The insert maps `duration`→`duration_seconds`, `file_size`→`file_size_bytes` and `bitrate`→`bitrate_kbps`, and `/watch` reads the DB names.
- Port mismatch: the `Dockerfile` runs uvicorn on 8000 but `EXPOSE`s 8080, and the `Procfile` uses 8080. `fly.toml` expects 8000.
- `/watch` interpolates `owner` and `filename` into HTML without escaping, and both come from the uploader.
