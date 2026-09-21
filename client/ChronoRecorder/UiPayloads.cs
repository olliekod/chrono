using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace ChronoRecorder
{
    /// <summary>A clip as the window sees it (JSON, camelCase).</summary>
    public sealed record ClipDto(
        string Id, string Title, string? Game, DateTime CreatedUtc, double? Duration, long SizeBytes, string? Resolution,
        string? Link, DateTime? UploadedUtc, string VideoUrl, string FileName, bool CanRemoveUpload)
    {
        public static ClipDto From(ClipRecord clip, string clipsBaseUrl) => new(
            clip.Id, clip.Title, clip.Game, DateTime.SpecifyKind(clip.CreatedUtc, DateTimeKind.Utc), clip.DurationSeconds, clip.SizeBytes, clip.Resolution,
            string.IsNullOrEmpty(clip.Link) ? null : clip.Link,
            clip.UploadedUtc == null ? null : DateTime.SpecifyKind(clip.UploadedUtc.Value, DateTimeKind.Utc),
            clipsBaseUrl + Uri.EscapeDataString(clip.FileName), clip.FileName,
            // The token itself never leaves the app; the page only learns whether removing is possible.
            clip.IsUploaded && !string.IsNullOrEmpty(clip.OwnerToken));
    }

    public sealed record HotkeyDto(string Name, IReadOnlyList<string> Keys, int Seconds);

    /// <summary>Everything the Recording page and the sidebar show about what Chrono is doing right now.</summary>
    public sealed record StatusDto(
        bool Enabled, bool Recording, string State, string Headline, string Target, string Mode, string SelectedApplication,
        string Screen, int Fps, string ClipQuality, string Audio, int BufferSeconds, string Username, bool CanUpload,
        IReadOnlyList<HotkeyDto> Hotkeys, bool ShowDiagnostics, string Version, bool NeedsOnboarding);

    public static class StatusPresenter
    {
        public static StatusDto Build(RecorderConfig config, IRecorder recorder)
        {
            var (state, headline) = TrayStatus.Describe(
                config.RecorderEnabled, recorder.IsRecordingActive, config.Mode, recorder.TargetName, config.SelectedApplication);

            Size screen = recorder.RecordingSize();

            return new StatusDto(
                Enabled: config.RecorderEnabled,
                Recording: recorder.IsRecordingActive,
                State: state.ToString().ToLowerInvariant(),
                Headline: headline,
                Target: recorder.TargetName,
                Mode: config.Mode.ToString(),
                SelectedApplication: config.SelectedApplication ?? "",
                Screen: screen.Width > 0 ? CaptureSizing.Describe(screen) : "",
                Fps: config.Fps,
                ClipQuality: ClipQualityText(config.Resolution, config.Fps),
                Audio: AudioText(config.RecordAudio, config.RecordMicrophone),
                BufferSeconds: config.RequiredBufferSeconds,
                Username: string.IsNullOrWhiteSpace(config.Username) ? "You" : config.Username,
                CanUpload: UploadRules.SettingsFrom(config) != null,
                Hotkeys: (config.Hotkeys ?? new List<HotkeyConfig>())
                    .Select(h => new HotkeyDto(h.Name, h.Modifiers.Concat(new[] { h.Key }).ToList(), h.ClipLengthSeconds)).ToList(),
                ShowDiagnostics: config.ShowDiagnostics,
                Version: DiagnosticsCollector.AppVersion,
                NeedsOnboarding: config.NeedsOnboarding);
        }

        public static string ClipQualityText(string? resolution, int fps)
        {
            string label = CaptureSizing.QualityLabel(resolution, fps);
            return label.StartsWith("Native", StringComparison.Ordinal) ? $"Native, {fps} FPS" : label;
        }

        public static string AudioText(bool game, bool microphone) => (game, microphone) switch
        {
            (true, true) => "Game sound and microphone",
            (true, false) => "Game sound",
            (false, true) => "Microphone only",
            _ => "No sound"
        };
    }
}
