using System;
using System.IO;
using Microsoft.Win32;

namespace ChronoRecorder
{
    /// <summary>
    /// "Start with Windows": a value under the user's Run key, so no admin rights and no scheduled task. Chrono
    /// starts with <c>--background</c>, which keeps its window closed and leaves only the tray icon.
    /// </summary>
    public static class AutoStart
    {
        public const string BackgroundFlag = "--background";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string DefaultValueName = "Chrono";

        public static string Command(string exePath) => $"\"{exePath}\" {BackgroundFlag}";

        /// <summary>
        /// True for a real install of Chrono. A build-output copy (bin\Debug...) or the .NET host running a dll must
        /// not be registered: the path would point at a developer's build folder or at dotnet.exe.
        /// </summary>
        public static bool IsRegistrable(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return false;
            if (!Path.GetFileName(exePath).Equals("ChronoRecorder.exe", StringComparison.OrdinalIgnoreCase)) return false;

            string lower = exePath.Replace('/', '\\').ToLowerInvariant();
            return !lower.Contains(@"\bin\debug\") && !lower.Contains(@"\bin\release\") && !lower.Contains(@"\obj\");
        }

        public static bool IsEnabled(string valueName = DefaultValueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(valueName) is string;
        }

        /// <summary>Turn it on or off. Returns false (and does nothing) when this copy can't be registered.</summary>
        public static bool Set(bool enabled, string? exePath, string valueName = DefaultValueName)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (!enabled)
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    return true;
                }

                if (!IsRegistrable(exePath)) return false;
                key.SetValue(valueName, Command(exePath!));
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Couldn't change start with Windows: {ex.Message}");
                return false;
            }
        }
    }
}
