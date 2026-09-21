namespace ChronoRecorder
{
    /// <summary>How a game gets captured. Pure and tested: this is the rule that keeps everything but the game out of a clip.</summary>
    public enum GameCapturePlan
    {
        /// <summary>The game's own window. Nothing else on the screen can appear, whatever covers it or however it is minimized.</summary>
        Window,

        /// <summary>The whole monitor, allowed only while the game is in front, because then the monitor shows the game.</summary>
        MonitorWhenInFront,

        /// <summary>There is no window to capture yet, so record nothing rather than record something else.</summary>
        Unavailable
    }

    /// <summary>
    /// Whether Windows lets a program capture a window without drawing a yellow border around it. Only Windows 11 does:
    /// on Windows 10 the border is forced on for every window capture, over the game the whole time, and no capture
    /// option turns it off (FFmpeg's <c>display_border</c> only asks, and Windows 10 ignores the request).
    /// </summary>
    public static class WindowCaptureSupport
    {
        /// <summary>The first Windows 11 build.</summary>
        public const int FirstBuildWithoutBorder = 22000;

        public static bool HasBorderlessCapture(int windowsBuild) => windowsBuild >= FirstBuildWithoutBorder;

        public static bool Borderless => HasBorderlessCapture(System.Environment.OSVersion.Version.Build);
    }

    public static class GameCapturePlanner
    {
        /// <param name="preference">The setting: "auto", "window" or "monitor".</param>
        /// <param name="windowCaptureFailed">Window capture already failed to start for this game.</param>
        /// <param name="haveWindow">The game's window handle was found.</param>
        /// <param name="borderlessCapture">Windows can capture a window with no border (Windows 11). Where it can't, "auto" records the
        /// monitor, only while the game is in front, rather than put a yellow border around the game. Choosing "window" is
        /// respected: someone who doesn't mind the border keeps its stronger guarantee that nothing else can be in a clip.</param>
        public static GameCapturePlan Plan(string? preference, bool windowCaptureFailed, bool haveWindow, bool borderlessCapture = true)
        {
            if (!haveWindow) return GameCapturePlan.Unavailable;

            bool wantsWindow = string.Equals(preference, "window", System.StringComparison.OrdinalIgnoreCase);
            bool wantsMonitor = string.Equals(preference, "monitor", System.StringComparison.OrdinalIgnoreCase);
            bool automatic = !wantsWindow && !wantsMonitor;

            bool monitor = wantsMonitor || windowCaptureFailed || (automatic && !borderlessCapture);
            return monitor ? GameCapturePlan.MonitorWhenInFront : GameCapturePlan.Window;
        }

        /// <summary>May the recorder be capturing right now? Monitor capture pauses whenever the game isn't in front.</summary>
        public static bool MayCapture(GameCapturePlan plan, bool gameInFront)
            => plan == GameCapturePlan.Window || (plan == GameCapturePlan.MonitorWhenInFront && gameInFront);
    }
}
