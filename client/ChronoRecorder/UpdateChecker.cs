using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChronoRecorder
{
    /// <summary>
    /// Asks GitHub for the newest release, on a timer, and remembers the answer so the UI can show it without a network
    /// round trip on every request. Respects <see cref="RecorderConfig.CheckForUpdates"/>: off means no request is ever
    /// made, not just no notification.
    /// </summary>
    public sealed class UpdateChecker : IDisposable
    {
        /// <summary>Give the app a moment to finish starting before the first check.</summary>
        public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How often after that. GitHub's unauthenticated rate limit is 60 requests an hour per IP, so this could go
        /// much tighter than it does; an hour is just a reasonable default for how often a small friend-group tool
        /// actually ships. The "Check for updates" button in Settings > App checks right away, regardless of this.
        /// </summary>
        public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

        private readonly RecorderConfig config;
        private readonly GitHubUpdater updater;
        private readonly Func<string> currentVersion;
        private System.Threading.Timer? timer;
        private string? lastAnnounced;

        /// <summary>The newest release known to be newer than this install, or null if none is (or none was found yet).</summary>
        public ReleaseInfo? Latest { get; private set; }

        /// <summary>Raised the first time a given newer version is found (not on every repeated check while it stays current).</summary>
        public event Action<ReleaseInfo>? UpdateFound;

        public UpdateChecker(RecorderConfig config, GitHubUpdater updater, Func<string>? currentVersion = null)
        {
            this.config = config;
            this.updater = updater;
            this.currentVersion = currentVersion ?? (() => DiagnosticsCollector.AppVersion);
        }

        public void Start()
        {
            timer ??= new System.Threading.Timer(_ => _ = CheckAsync(), null, FirstCheckDelay, CheckEvery);
        }

        /// <summary>Checks now, regardless of the timer (Settings' "Check for updates" button uses this).</summary>
        public async Task<ReleaseInfo?> CheckAsync()
        {
            if (!config.CheckForUpdates) return null;

            var release = await updater.GetLatestAsync();
            bool newer = release != null && UpdateCheck.IsNewer(currentVersion(), release.Version);
            Latest = newer ? release : null;

            if (newer && release!.Version != lastAnnounced)
            {
                lastAnnounced = release.Version;
                UpdateFound?.Invoke(release);
            }

            return Latest;
        }

        public void Dispose() => timer?.Dispose();
    }
}
