namespace ChronoRecorder
{
    /// <summary>When the recorder should be recording. Pure, so the rule is easy to test and to change.</summary>
    public static class RecordingPolicy
    {
        /// <param name="selectedAppRunning">Whether the game is running: the chosen one in Application mode, a detected one in Auto mode. Not read in Display mode.</param>
        public static bool ShouldRecord(bool enabled, RecorderConfig.RecordingMode mode, string? selectedApplication, bool selectedAppRunning)
        {
            if (!enabled) return false;

            return mode switch
            {
                // While the game is running, not only while it has focus: capture is cheap now, and clicking over to
                // Discord on the other monitor shouldn't pause the buffer.
                RecorderConfig.RecordingMode.Application => !string.IsNullOrWhiteSpace(selectedApplication) && selectedAppRunning,
                RecorderConfig.RecordingMode.Auto => selectedAppRunning,
                RecorderConfig.RecordingMode.Display => true,
                _ => false
            };
        }
    }
}
