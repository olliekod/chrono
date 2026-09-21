using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace ChronoRecorder
{
    /// <summary>
    /// Chrono's windows are web pages shown by Microsoft's WebView2 runtime. Windows 11 and most Windows 10 PCs
    /// have it; for the rest the download carries Microsoft's installer, which is run here.
    /// </summary>
    public static class WebView2Runtime
    {
        public static bool IsInstalled()
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch (Exception) { return false; }
        }

        /// <summary>Makes sure the runtime is there, installing it if needed. False means Chrono can't show its window.</summary>
        public static bool EnsureInstalled()
        {
            if (IsInstalled()) return true;

            string installer = Path.Combine(AppContext.BaseDirectory, "redist", "MicrosoftEdgeWebview2Setup.exe");
            if (!File.Exists(installer))
            {
                MessageBox.Show("Chrono needs the Microsoft Edge WebView2 runtime, which isn't installed.\n\n" +
                                "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and start Chrono again.",
                                "Chrono", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                Console.WriteLine("Installing the WebView2 runtime...");
                using var process = Process.Start(new ProcessStartInfo(installer, "/silent /install") { UseShellExecute = true });
                process?.WaitForExit(180_000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ WebView2 install failed: {ex.Message}");
            }

            if (IsInstalled()) return true;

            MessageBox.Show("Chrono couldn't install the Microsoft Edge WebView2 runtime it needs.\n\n" +
                            $"Run this yourself, then start Chrono again:\n{installer}",
                            "Chrono", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }
}
