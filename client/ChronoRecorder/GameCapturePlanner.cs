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

    public static class GameCapturePlanner
    {
        /// <param name="preference">The setting: "window" or "monitor".</param>
        /// <param name="windowCaptureFailed">Window capture already failed to start for this game.</param>
        /// <param name="haveWindow">The game's window handle was found.</param>
        public static GameCapturePlan Plan(string? preference, bool windowCaptureFailed, bool haveWindow)
        {
            if (!haveWindow) return GameCapturePlan.Unavailable;

            bool monitor = string.Equals(preference, "monitor", System.StringComparison.OrdinalIgnoreCase) || windowCaptureFailed;
            return monitor ? GameCapturePlan.MonitorWhenInFront : GameCapturePlan.Window;
        }

        /// <summary>May the recorder be capturing right now? Monitor capture pauses whenever the game isn't in front.</summary>
        public static bool MayCapture(GameCapturePlan plan, bool gameInFront)
            => plan == GameCapturePlan.Window || (plan == GameCapturePlan.MonitorWhenInFront && gameInFront);
    }
}
