using System.Runtime.InteropServices;

namespace SkillView.Logging;

/// Resolves the platform-appropriate cache directory for SkillView logs
/// (§18.3). Logs are cache, not config — they live under the cache root on
/// each platform.
public static class LogPaths
{
    public const string SubfolderName = "SkillView";
    public const string LogsSubfolder = "logs";

    /// Returns the absolute path to `…/SkillView/logs` for the current OS.
    /// The caller is responsible for creating the directory when writing.
    public static string Resolve() => Path.Combine(ResolveCacheRoot(), SubfolderName, LogsSubfolder);

    /// Lower-level: where "SkillView" should live. Exposed for tests / diagnostics.
    public static string ResolveCacheRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // %LOCALAPPDATA% — SpecialFolder.LocalApplicationData maps to it.
            var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                return local;
            }
            return Path.Combine(Path.GetTempPath(), "SkillView-logs");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = System.Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                return Path.Combine(home, "Library", "Caches");
            }
        }

        // Linux / everything else: XDG_CACHE_HOME with ~/.cache fallback.
        var xdg = System.Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return xdg;
        }
        var linuxHome = System.Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(linuxHome))
        {
            return Path.Combine(linuxHome, ".cache");
        }
        return Path.Combine(Path.GetTempPath(), "skillview-cache");
    }

    public static string FileNameForDate(DateOnly date) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "skillview-{0:yyyy-MM-dd}.log", date);
}
