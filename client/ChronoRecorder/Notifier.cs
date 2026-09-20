using System;
using System.Drawing;
using System.Windows.Forms;

namespace ChronoRecorder
{
    /// <summary>
    /// Tray notifications, so saving and sharing a clip doesn't throw a modal dialog over the game.
    /// Create and use on the UI thread.
    /// </summary>
    public sealed class Notifier : IDisposable
    {
        private readonly RecorderConfig config;
        private readonly NotifyIcon icon;

        public Notifier(RecorderConfig config)
        {
            this.config = config;
            icon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Chrono",
                Visible = true
            };
        }

        /// <summary>Progress and success messages; silenced by the "Show notifications" setting.</summary>
        public void Info(string title, string text)
        {
            if (!config.ShowNotifications) return;
            Show(title, text, ToolTipIcon.Info);
        }

        /// <summary>Failures are always shown. A clip that silently failed to upload is worse than a notification.</summary>
        public void Error(string title, string text) => Show(title, text, ToolTipIcon.Error);

        private void Show(string title, string text, ToolTipIcon kind)
        {
            Console.WriteLine($"[{kind}] {title}: {text}");
            icon.ShowBalloonTip(5000, title, text, kind);
        }

        public void Dispose()
        {
            // Hide first, or Windows leaves a ghost icon in the tray until the mouse passes over it.
            icon.Visible = false;
            icon.Dispose();
        }
    }
}
