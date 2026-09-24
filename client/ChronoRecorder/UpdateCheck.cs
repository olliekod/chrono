using System.Text.RegularExpressions;

namespace ChronoRecorder
{
    /// <summary>Comparing version numbers like "1.2.10", where plain string or numeric comparison gets it wrong.</summary>
    public static class UpdateCheck
    {
        private static readonly Regex Pattern = new(@"^v?(\d+)\.(\d+)\.(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>"v1.2.10" or "1.2.10" as three numbers, or null if it isn't shaped like a version. A leading "v", as
        /// GitHub's tag names have, is optional.</summary>
        public static (int Major, int Minor, int Patch)? Parse(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            var m = Pattern.Match(version.Trim());
            if (!m.Success) return null;
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> names a version strictly newer than <paramref name="current"/>. Compares
        /// major, then minor, then patch, so "1.2.10" is newer than "1.2.9" (a plain string compare would have it
        /// backwards). Anything that doesn't parse as X.Y.Z counts as not newer, so a malformed tag or a beta build never
        /// nags someone to "update" to something that isn't really a release.
        /// </summary>
        public static bool IsNewer(string? current, string? candidate)
        {
            var currentVersion = Parse(current);
            var candidateVersion = Parse(candidate);
            if (currentVersion == null || candidateVersion == null) return false;
            return candidateVersion.Value.CompareTo(currentVersion.Value) > 0;
        }
    }
}
