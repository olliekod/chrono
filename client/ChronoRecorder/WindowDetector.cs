using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace ChronoRecorder
{
    /// <summary>
    /// Detects the currently active window/application
    /// </summary>
    public class WindowDetector
    {
        // Windows API imports
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>
        /// Get the name of the currently active application
        /// </summary>
        public static string GetActiveApplicationName()
        {
            try
            {
                // Get foreground window handle
                IntPtr hwnd = GetForegroundWindow();
                
                if (hwnd == IntPtr.Zero)
                {
                    return "display";
                }

                // Get process ID
                uint processId;
                GetWindowThreadProcessId(hwnd, out processId);

                // Get process
                var process = Process.GetProcessById((int)processId);
                string processName = process.ProcessName;

                // Clean up process name
                string appName = CleanApplicationName(processName);

                // If it's Explorer or system app, return "display"
                if (IsSystemApp(appName))
                {
                    return "display";
                }

                return appName;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Could not detect active window: {ex.Message}");
                return "display";
            }
        }

        /// <summary>
        /// Get window title (for debugging/logging)
        /// </summary>
        public static string GetActiveWindowTitle()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "";

                const int nChars = 256;
                StringBuilder buff = new StringBuilder(nChars);
                
                if (GetWindowText(hwnd, buff, nChars) > 0)
                {
                    return buff.ToString();
                }
            }
            catch { }

            return "";
        }

        /// <summary>
        /// Clean application name for use in filename
        /// </summary>
        public static string CleanApplicationName(string name)
        {
            // Remove common suffixes
            name = name.Replace(".exe", "");
            name = name.Replace("Application", "");
            
            // Capitalize first letter
            if (name.Length > 0)
            {
                name = char.ToUpper(name[0]) + name.Substring(1).ToLower();
            }

            // Handle known game launchers
            if (name.ToLower() == "javaw") return "Minecraft";
            if (name.ToLower() == "steam") return "Steam";

            return name;
        }

        /// <summary>
        /// Check if this is a system app (desktop, explorer, etc.)
        /// </summary>
        private static bool IsSystemApp(string name)
        {
            string[] systemApps = { 
                "explorer", "dwm", "taskmgr", "systemsettings", 
                "applicationframehost", "shellexperiencehost",
                "searchhost", "startmenuexperiencehost"
            };

            foreach (var sysApp in systemApps)
            {
                if (name.ToLower().Contains(sysApp.ToLower()))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Get formatted filename with app name and timestamp
        /// </summary>
        public static string GetClipFilename()
        {
            string appName = GetActiveApplicationName();
            DateTime now = DateTime.Now;
            
            // Format: AppName_Hh.Mm.Ss_Mm.Dd.Yyyy
            string filename = $"{appName}_{now:HH}.{now:mm}.{now:ss}_{now:MM}.{now:dd}.{now:yyyy}";
            
            return filename;
        }
    }
}