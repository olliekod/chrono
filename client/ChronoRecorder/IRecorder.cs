using System.Collections.Generic;
using System.Drawing;

namespace ChronoRecorder
{
    /// <summary>What the window needs from the recorder. An interface so the window's logic can be tested without FFmpeg.</summary>
    public interface IRecorder
    {
        bool IsRecordingActive { get; }

        /// <summary>The game being recorded, "display", or "no game running".</summary>
        string TargetName { get; }

        /// <summary>The size of the screen that is (or will be) recorded.</summary>
        Size RecordingSize();

        /// <summary>The encoder in use, e.g. "h264_nvenc".</summary>
        string EncoderName { get; }

        void SetRecorderEnabled(bool enabled);
        void SetRecordingMode(RecorderConfig.RecordingMode mode);
        void SetTrackedApplication(string appName);

        /// <summary>Sound devices, volume or the capture method changed: restart the recording so it uses them.</summary>
        void RestartRecording();

        /// <summary>Names of programs with a window, for the "Pick a game" list.</summary>
        IReadOnlyList<string> RunningApplications();
    }
}
