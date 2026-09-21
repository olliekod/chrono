using System;
using System.Collections.Generic;
using System.Linq;

namespace ChronoRecorder
{
    /// <summary>What Chrono can cheaply learn about one running program.</summary>
    /// <param name="ExePath">Full path of the program, or null when Windows won't say.</param>
    /// <param name="HasVisibleWindow">It owns a window on screen (games without one aren't worth recording).</param>
    /// <param name="CoversMonitor">Its window fills a whole monitor: fullscreen or borderless fullscreen.</param>
    /// <param name="UsesGraphicsApi">It has Direct3D, Vulkan or OpenGL loaded.</param>
    /// <param name="KnownToWindowsAsGame">Windows' own game list (Game Bar / Game Mode) holds this exact program.</param>
    public sealed record ProcessFacts(
        string Name, string? ExePath, bool HasVisibleWindow, bool CoversMonitor, bool UsesGraphicsApi, bool KnownToWindowsAsGame);

    public sealed record GameVerdict(bool IsGame, string Reason);

    /// <summary>
    /// Decides whether a running program is a game, so recording can start without anyone choosing anything.
    /// Games have priority and nothing else may be picked, so this errs on the side of "not a game": a missed game
    /// can still be chosen by hand, while recording a browser tab or a video call would be a privacy problem.
    /// Pure and unit-tested; <see cref="ProcessScanner"/> gathers the facts.
    /// </summary>
    public static class GameClassifier
    {
        /// <summary>Programs that are never games, even fullscreen with a graphics API loaded (browsers and players
        /// go fullscreen too) or sitting in a game folder (launchers, helpers). Compared with the exe name without ".exe".</summary>
        private static readonly HashSet<string> NeverGames = new(StringComparer.OrdinalIgnoreCase)
        {
            // Chrono, Windows, shells
            "ChronoRecorder", "explorer", "dwm", "taskmgr", "SystemSettings", "ApplicationFrameHost", "ShellExperienceHost",
            "SearchHost", "StartMenuExperienceHost", "TextInputHost", "LockApp", "WindowsTerminal", "cmd", "powershell", "conhost",
            "mstsc", "Rainmeter",
            // browsers
            "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "iexplore", "arc", "zen",
            // chat, calls, work
            "Discord", "DiscordPTB", "DiscordCanary", "Teams", "ms-teams", "Zoom", "Slack", "Skype", "Telegram", "WhatsApp", "Signal",
            "Outlook", "olk", "WINWORD", "EXCEL", "POWERPNT", "ONENOTE",
            // media
            "Spotify", "vlc", "mpv", "mpc-hc", "mpc-hc64", "PotPlayerMini64", "PotPlayerMini", "wmplayer", "Video.UI", "Music.UI", "itunes",
            // stores, launchers and their helpers (the game itself is a different program)
            "Steam", "steamwebhelper", "steamservice", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "Agent", "Origin", "EADesktop",
            "EABackgroundService", "UbisoftConnect", "upc", "UplayWebCore", "GalaxyClient", "RiotClientServices", "RiotClientUx",
            "RiotClientUxRender", "Launcher", "XboxPcApp", "GameBar", "GameBarFTServer", "NVIDIA Overlay", "NVIDIA app", "nvcontainer",
            // streaming, capture, tools
            "obs64", "obs32", "streamlabs", "Streamlabs OBS", "medal", "Medal", "Overwolf", "OverwolfBrowser", "AnyDesk", "TeamViewer",
            "Code", "devenv", "rider64", "idea64", "notepad", "Notepad", "notepad++", "sublime_text", "Photoshop", "Illustrator", "blender",
            "Wallpaper32", "Wallpaper64", "wallpaper_engine", "vrmonitor", "vrserver", "steamvr_room_setup",
        };

        /// <summary>Anywhere in the name: marks a helper of a game rather than the game ("UnityCrashHandler64", "vc_redist.x64").</summary>
        private static readonly string[] HelperFragments =
        {
            "crashhandler", "crashreporter", "crashpad", "redist", "unins", "launcher", "anticheat", "easyanticheat", "beclient",
        };

        /// <summary>At the end of the name only, so a game called "Update Zero" isn't lost.</summary>
        private static readonly string[] HelperSuffixes = { "setup", "install", "installer", "updater", "helper", "bootstrapper" };

        /// <summary>Folders where game stores put games. The exe must be inside, not just the store's own client.</summary>
        private static readonly string[] GameLibraryFragments =
        {
            @"\steamapps\common\",
            @"\epic games\",
            @"\gog galaxy\games\", @"\gog games\",
            @"\riot games\",
            @"\ubisoft game launcher\games\", @"\ubisoft\ubisoft game launcher\games\",
            @"\ea games\", @"\origin games\", @"\electronic arts\",
            @"\xboxgames\",
            @"\battle.net\games\", @"\blizzard entertainment\",
            @"\rockstar games\", @"\itch\apps\", @"\amazon games\library\",
        };

        public static GameVerdict Classify(ProcessFacts p)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(p.Name);

            if (!p.HasVisibleWindow) return new(false, "no window");
            if (NeverGames.Contains(name)) return new(false, "known non-game program");

            string lower = name.ToLowerInvariant();
            if (HelperFragments.Any(lower.Contains) || HelperSuffixes.Any(s => lower.EndsWith(s, StringComparison.Ordinal)))
                return new(false, "looks like a launcher or helper");

            // Windows' own list is the strongest signal: it has seen this exact program run as a game.
            if (p.KnownToWindowsAsGame) return new(true, "on Windows' game list");

            if (p.ExePath != null)
            {
                string path = p.ExePath.Replace('/', '\\').ToLowerInvariant();
                if (GameLibraryFragments.Any(f => path.Contains(f))) return new(true, "in a game library folder");
            }

            if (p.CoversMonitor && p.UsesGraphicsApi) return new(true, "fullscreen with a 3D graphics API");

            return new(false, "nothing says it is a game");
        }

        /// <summary>Best game to record from those running, or null. Keeps the current one while it runs; otherwise the one in front, otherwise the newest.</summary>
        public static GameCandidate? Choose(IReadOnlyList<GameCandidate> games, int? currentPid)
        {
            if (games.Count == 0) return null;
            if (currentPid is int keep && games.FirstOrDefault(g => g.Pid == keep) is { } current) return current;

            return games
                .OrderByDescending(g => g.IsForeground)
                .ThenByDescending(g => g.StartedAtUtc)
                .First();
        }
    }

    /// <param name="DisplayName">What the UI shows, e.g. "Risk of rain 2".</param>
    public sealed record GameCandidate(int Pid, string DisplayName, string ProcessName, DateTime StartedAtUtc, bool IsForeground);
}
