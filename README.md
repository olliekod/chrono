# Chrono

A small, quiet clip recorder for Windows. It records your games in the background, and when something great happens you press a hotkey to save the last 30 seconds (or however long you choose). Trim it, give it a title, and share a link that plays right inside Discord.

- **Light on your PC.** On an NVIDIA graphics card, recording uses about 5% of one CPU core. It runs from the system tray with no overlay.
- **Finds your game by itself.** Start a game and Chrono starts recording it. Browsers, Discord, video players and other apps are never recorded.
- **Nothing is shared unless you choose.** A hotkey only saves the clip to your library on your PC. Uploading is a button you press.
- **A Discord-style library** with a trim editor, rename, filters by game and date, and a "Copy link" button.
- **No ads, no account, no overlay.**

## Install

1. Download `Chrono-win-x64.zip` from the [Releases](../../releases) page and unzip it anywhere (for example `C:\Chrono`).
2. Run `ChronoRecorder.exe`.
   - Windows may show a blue "Windows protected your PC" screen because Chrono is not code-signed. Click **More info**, then **Run anyway**.
3. Chrono opens once so you can look around, then lives in the system tray (bottom right, near the clock). Click its icon to open the window.

Everything Chrono needs is in the zip (FFmpeg included); there is nothing else to install. It needs Windows 10 or 11. An NVIDIA graphics card gives the lightest recording; AMD and Intel cards are supported but less tested.

## Saving a clip

Start a game, then press:

| Keys | Saves |
| --- | --- |
| Ctrl + PageUp | the last 30 seconds |
| Ctrl + PageDown | the last 2 minutes |

You will hear a short chime, and the clip appears in your **Library**. Change the keys and lengths in **Settings > Hotkeys**; any combination of up to three keys works (for example Ctrl + Shift + \\).

## Sharing clips

Uploading needs a Chrono server, which whoever gave you this app runs. In **Settings > Uploading** enter:

- **Server address**: they will send you this.
- **Upload key**: they will send you this too. Keep it private.
- **Username**: shown on links you share.

Both start blank in a fresh install. Then open a clip in the Library and press **Upload**. When it finishes the link is on your clipboard, and **Copy link** is always there on uploaded clips.

## Sound

**Settings > Sound** lets you pick the speakers/headphones and microphone to record (the same list as Windows Sound settings) and make your microphone louder or quieter, with a live level bar. The microphone is off until you turn it on.

## Where things are

| | |
| --- | --- |
| Clips | `Videos\Chrono` (change it in Settings) |
| Settings | `%APPDATA%\Chrono\config.json` |
| Library index | `%APPDATA%\Chrono\library.json` |
| Log (for bug reports) | `%LOCALAPPDATA%\Chrono\logs\chrono.log` |

To remove Chrono, exit it from the tray menu and delete its folder. Turn off **Settings > App > Start Chrono with Windows** first if you had it on.

## For whoever runs the server, and for developers

See [DEVELOPMENT.md](DEVELOPMENT.md) for how everything works, the build and test commands, and how to deploy the server (`worker/`, a Cloudflare Worker with R2 storage). To build the download: `powershell -File scripts\publish.ps1`.

Chrono includes FFmpeg (GPL) from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/); its licence and source pointer ship beside it in the `ffmpeg` folder.
