# Chrono

Chrono is lightweight clipping software for Windows that shares clips as links, the way Medal does. Press a hotkey to save the last 30 seconds (or whatever length you set), trim it, give it a title, and send a link anywhere.

- It finds your game on its own. Start a game and recording starts. Browsers, Discord, video players and other apps are left alone.
- A game is recorded as its own window, so alt-tabbing to Discord or your desktop never puts them in a clip. If you minimize the game, the clip shows its last frame.
- Chrono lives in the system tray and draws no overlay.
- A hotkey only saves the clip to your PC. Uploading happens when you press the Upload button.
- The library has a trim editor, renaming, filters for game, date and uploaded or on this PC, and a Copy link button.
- There are no ads and no account.

## How light is it?

These are Chrono's own measurements, taken on an RTX 4080 recording a 2560x1440 monitor at 60 FPS:

- Recording uses about 3 to 5% of one CPU core.
- In the tray with the window closed, Chrono uses about 19 MB of memory. With the window open it uses about 225 MB, and it goes back to 19 MB when you close it.
- Saving a clip at a smaller size (1080p, say) takes a few seconds and uses almost no CPU, because the graphics card does the resizing.

Chrono has not been benchmarked against Medal, and the effect on your game's frame rate has not been measured, so there is no comparison number here. Your results will depend on your PC.

The design keeps the work small:

- On NVIDIA cards, frames are captured and encoded on the graphics card and never pass through the CPU.
- One FFmpeg process writes the rolling buffer, so nothing restarts or piles up in the background.
- There is no in-game overlay and no hook injected into the game. Chrono reads the game's window through Windows' own capture API.
- The window is a web page that Chrono only creates while you are looking at it. Closed, it leaves just the tray icon.
- Resizing happens when you save a clip, so the game never pays for it while you play.

## Install

1. Download `Chrono-Setup.exe` from the [Releases](../../releases) page and run it. It installs for your user only, so it does not ask for admin rights.
2. Windows may show a blue "Windows protected your PC" screen, because Chrono is not code-signed. Click **More info**, then **Run anyway**.
3. Chrono opens once so you can look around, then stays in the system tray (bottom right, near the clock). Click its icon to open the window.

FFmpeg and everything else Chrono needs is inside the installer. It runs on Windows 10 and 11. An NVIDIA card gives the lightest recording. AMD and Intel cards work but have had less testing.

If you prefer no installer, `Chrono-win-x64.zip` on the same page is a portable copy. Unzip it anywhere and run `ChronoRecorder.exe`.

To uninstall, use Windows Settings > Apps > Installed apps > Chrono. Your clips and settings stay on disk. Delete `Videos\Chrono` and `%APPDATA%\Chrono` if you want those gone too. For the portable copy, exit Chrono from the tray menu and delete its folder, after turning off **Settings > App > Start Chrono with Windows** if you had it on.

## Saving a clip

Start a game, then press:

| Keys | Saves |
| --- | --- |
| Ctrl + PageUp | the last 30 seconds |
| Ctrl + PageDown | the last 2 minutes |

A short chime plays and the clip appears in your Library. You can change the keys and lengths in **Settings > Hotkeys**. Any combination of up to three keys works, for example Ctrl + Shift + \\.

If a game records as a black picture, open **Settings > Recording** and set **Capture games as** to the monitor option.

## Sharing clips

Uploading needs a Chrono server, run by whoever gave you this app. In **Settings > Uploading**, enter the server address and upload key they send you (keep the key private). You can also set the username shown on your links; it starts as `username`. The address and key are blank in a fresh install. Then open a clip in the Library and press **Upload**. When it finishes, the link is on your clipboard, and uploaded clips keep a **Copy link** button.

## Sound

**Settings > Sound** lists the same speakers and microphones as Windows Sound settings, so you can pick which ones to record. It also lets you make your microphone louder or quieter, with a live level bar. The microphone stays off until you turn it on.

## Where things are

| | |
| --- | --- |
| Clips | `Videos\Chrono` (change it in Settings) |
| Settings | `%APPDATA%\Chrono\config.json` |
| Library index | `%APPDATA%\Chrono\library.json` |
| Log, for bug reports | `%LOCALAPPDATA%\Chrono\logs\chrono.log` |

## For whoever runs the server, and for developers

[DEVELOPMENT.md](DEVELOPMENT.md) explains how everything works, the build and test commands, and how to deploy the server (`worker/`, a Cloudflare Worker with R2 storage). To build the downloads, run `powershell -File scripts\build-installer.ps1`. It produces `dist\Chrono-Setup.exe` and the portable `dist\Chrono-win-x64.zip`.

Chrono includes FFmpeg (GPL) from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/). Its licence and source pointer sit beside it in the `ffmpeg` folder.
