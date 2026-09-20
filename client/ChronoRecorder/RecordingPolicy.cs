namespace ChronoRecorder
{
    /// <summary>When the recorder should be recording. Pure, so the rule is easy to test and to change.</summary>
    public static class RecordingPolicy
    {
        /// <param name="selectedAppRunning">Whether the chosen game currently has a window. Only read in Application mode.</param>
        public static bool ShouldRecord(bool enabled, RecorderConfig.RecordingMode mode, string? selectedApplication, bool selectedAppRunning)
        {
            if (!enabled) return false;

            return mode switch
            {
                // While the game is running, not only while it has focus: capture is cheap now, and clicking over to
                // Discord on the other monitor shouldn't pause the buffer.
                RecorderConfig.RecordingMode.Application => !string.IsNullOrWhiteSpace(selectedApplication) && selectedAppRunning,
                RecorderConfig.RecordingMode.Display => true,
                _ => false
            };
        }
    }
}
