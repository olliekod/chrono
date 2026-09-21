namespace ChronoRecorder
{
    public enum TrayState { Idle, Recording, Paused }

    /// <summary>What the tray icon shows: the answer to "what is Chrono recording right now?" without opening the window.</summary>
    public static class TrayStatus
    {
        /// <summary>Longest tooltip Windows accepts is 127 characters.</summary>
        public const int MaxTooltipLength = 127;

        public static (TrayState State, string Text) Describe(
            bool enabled, bool isRecording, RecorderConfig.RecordingMode mode, string target, string selectedApplication)
        {
            if (!enabled) return (TrayState.Paused, "Paused");
            if (isRecording) return (TrayState.Recording, mode == RecorderConfig.RecordingMode.Display ? "Recording the screen" : $"Recording {target}");

            string waiting = mode switch
            {
                RecorderConfig.RecordingMode.Auto => "Waiting for a game",
                RecorderConfig.RecordingMode.Application when !string.IsNullOrWhiteSpace(selectedApplication) => $"Waiting for {selectedApplication}",
                RecorderConfig.RecordingMode.Application => "Choose a game to record",
                _ => "Not recording"
            };
            return (TrayState.Idle, waiting);
        }

        /// <summary>"Chrono: Recording Risk of rain 2", cut to what the tray accepts.</summary>
        public static string Tooltip(string text)
        {
            string full = $"Chrono: {text}";
            return full.Length <= MaxTooltipLength ? full : full.Substring(0, MaxTooltipLength - 1) + "…";
        }
    }
}
