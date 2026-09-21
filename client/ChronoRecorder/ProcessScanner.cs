using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ChronoRecorder
{
    /// <summary>A game that is running now, with its window (the window says which monitor to record).</summary>
    public sealed record RunningGame(GameCandidate Candidate, IntPtr Window);

    /// <summary>
    /// Finds the games that are running. Gathers the facts about each program with a window and hands them to
    /// <see cref="GameClassifier"/>, which decides. One pass over the desktop's windows, no polling of every
    /// process; the costly checks (program path, loaded libraries) are made once per process and remembered.
    /// </summary>
    public sealed class ProcessScanner
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x80;
        private const int DwmwaCloaked = 14;

        private static readonly string[] GraphicsLibraries =
            { "d3d9.dll", "d3d10.dll", "d3d10_1.dll", "d3d11.dll", "d3d12.dll", "vulkan-1.dll", "opengl32.dll" };

        /// <summary>What never changes for a running program, remembered by process id and start time.</summary>
        private sealed record Stable(string Name, string? ExePath, bool KnownGame, bool? UsesGraphicsApi, DateTime StartedUtc);

        private readonly Dictionary<(int Pid, long Start), Stable> cache = new();
        private HashSet<string> windowsGameList = new(StringComparer.OrdinalIgnoreCase);
        private DateTime gameListReadAt = DateTime.MinValue;

        /// <summary>Every game currently running, best guess first (see <see cref="GameClassifier.Choose"/>).</summary>
        public List<RunningGame> FindGames()
        {
            var games = new List<RunningGame>();
            try
            {
                RefreshWindowsGameList();

                IntPtr foreground = GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out uint foregroundPid);

                var seen = new HashSet<int>();
                foreach (var (pid, window) in ProgramWindows())
                {
                    if (!seen.Add(pid)) continue;   // one entry per program: its biggest window
                    var running = Inspect(pid, window, pid == foregroundPid);
                    if (running != null) games.Add(running);
                }

                Forget(seen);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Game scan failed: {ex.Message}");
            }
            return games;
        }

        /// <summary>Is this game still running? Cheap: a game that has closed has no process with the same id and start time.</summary>
        public bool IsStillRunning(GameCandidate game)
        {
            try
            {
                using var p = Process.GetProcessById(game.Pid);
                return !p.HasExited && p.StartTime.ToUniversalTime() == game.StartedAtUtc;
            }
            catch { return false; }
        }

        /// <summary>
        /// The program's main window even while it is minimized (which the visible-window scan skips), or Zero.
        /// A minimized game must still be capturable as a window: that is what keeps the desktop out of the clip.
        /// </summary>
        public IntPtr MainWindowOf(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return p.MainWindowHandle; }
            catch { return IntPtr.Zero; }
        }

        /// <summary>The program's window now (it may have been recreated since it was found), or Zero.</summary>
        public IntPtr WindowFor(int pid)
            => ProgramWindows().Where(w => w.Pid == pid).Select(w => w.Window).FirstOrDefault();

        /// <summary>The program's biggest visible window for each program that has one.</summary>
        private static List<(int Pid, IntPtr Window)> ProgramWindows()
        {
            var best = new Dictionary<int, (IntPtr Window, long Area)>();

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                if ((GetWindowLong(hWnd, GwlExStyle) & WsExToolWindow) != 0) return true;
                if (DwmGetWindowAttribute(hWnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;   // hidden UWP shells
                if (!GetWindowRect(hWnd, out var r)) return true;

                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area < 200 * 200) return true;   // tooltips, tray helpers

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0 || pid == Environment.ProcessId) return true;
                if (!best.TryGetValue((int)pid, out var current) || area > current.Area) best[(int)pid] = (hWnd, area);
                return true;
            }, IntPtr.Zero);

            return best.Select(kv => (kv.Key, kv.Value.Window)).ToList();
        }

        private RunningGame? Inspect(int pid, IntPtr window, bool isForeground)
        {
            var key = StableKey(pid);
            if (key == null) return null;

            if (!cache.TryGetValue(key.Value, out var stable))
            {
                stable = ReadStable(pid);
                if (stable == null) return null;
                cache[key.Value] = stable;
            }

            bool covers = CoversItsMonitor(window);

            // Loaded libraries are only worth reading when it could change the answer.
            bool? gfx = stable.UsesGraphicsApi;
            if (covers && gfx == null && !stable.KnownGame)
            {
                gfx = LoadsGraphicsApi(pid);
                cache[key.Value] = stable with { UsesGraphicsApi = gfx };
            }

            var verdict = GameClassifier.Classify(new ProcessFacts(
                stable.Name, stable.ExePath, HasVisibleWindow: true, covers, gfx == true, stable.KnownGame));
            if (!verdict.IsGame) return null;

            string display = WindowDetector.CleanApplicationName(Path.GetFileNameWithoutExtension(stable.Name));
            return new RunningGame(new GameCandidate(pid, display, stable.Name, stable.StartedUtc, isForeground), window);
        }

        private static (int, long)? StableKey(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return (pid, p.StartTime.ToUniversalTime().Ticks);
            }
            catch { return null; }   // gone, or not ours to inspect
        }

        private Stable? ReadStable(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                string? path = ImagePath(pid);
                string name = (path != null ? Path.GetFileName(path) : p.ProcessName + ".exe");
                bool known = path != null && windowsGameList.Contains(Normalize(path));
                return new Stable(name, path, known, null, p.StartTime.ToUniversalTime());
            }
            catch { return null; }
        }

        private static string? ImagePath(int pid)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                uint size = (uint)buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            }
            finally { CloseHandle(handle); }
        }

        private static bool CoversItsMonitor(IntPtr window)
        {
            if (!GetWindowRect(window, out var r)) return false;
            var monitor = MonitorLocator.ForWindow(window);
            if (monitor == null) return false;

            var b = monitor.Bounds;
            // A borderless window matches the monitor exactly; some fullscreen modes overhang by a pixel or two.
            return r.Left <= b.Left + 2 && r.Top <= b.Top + 2 && r.Right >= b.Right - 2 && r.Bottom >= b.Bottom - 2;
        }

        private static bool? LoadsGraphicsApi(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                foreach (ProcessModule module in p.Modules)
                {
                    string file = module.ModuleName ?? "";
                    if (GraphicsLibraries.Any(g => file.Equals(g, StringComparison.OrdinalIgnoreCase))) return true;
                }
                return false;
            }
            catch { return null; }   // protected process: can't say
        }

        /// <summary>Windows records every program it has seen run as a game (for Game Bar and Game Mode).</summary>
        private void RefreshWindowsGameList()
        {
            if (DateTime.UtcNow - gameListReadAt < TimeSpan.FromSeconds(30)) return;
            gameListReadAt = DateTime.UtcNow;

            var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
                if (children != null)
                {
                    foreach (var name in children.GetSubKeyNames())
                    {
                        using var child = children.OpenSubKey(name);
                        if (child?.GetValue("MatchedExeFullPath") is string path && path.Length > 0) list.Add(Normalize(path));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Couldn't read Windows' game list: {ex.Message}");
            }
            windowsGameList = list;
            cache.Clear();   // "known" may have changed for programs seen before
        }

        private static string Normalize(string path) => path.Replace('/', '\\').Trim().ToLowerInvariant();

        private void Forget(HashSet<int> stillPresent)
        {
            foreach (var key in cache.Keys.Where(k => !stillPresent.Contains(k.Pid)).ToList()) cache.Remove(key);
        }
    }
}
