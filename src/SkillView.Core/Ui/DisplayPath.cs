namespace SkillView.Ui;

/// Lexical presentation only: never resolve links, probe disk, or create selectors.
internal static class DisplayPath
{
    private static readonly string CurrentHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

    internal static string Abbreviate(string path, string? homeDirectory = null, bool? windows = null)
    {
        var home = homeDirectory ?? CurrentHome;
        var isWindows = windows ?? OperatingSystem.IsWindows();
        bool Separator(char c) => c == '/' || (isWindows && c == '\\');
        var length = home.Length;
        while (length > 0 && Separator(home[length - 1])) length--;
        // Do not treat an absent home or a filesystem root as a user directory.
        if (length == 0 || (isWindows && length == 2 && home[1] == ':')) return path;
        if (path.Length < length) return path;
        for (var i = 0; i < length; i++)
        {
            if (isWindows && Separator(home[i]) && Separator(path[i])) continue;
            if (isWindows ? char.ToUpperInvariant(home[i]) != char.ToUpperInvariant(path[i]) : home[i] != path[i])
                return path;
        }
        if (path.Length != length && !Separator(path[length])) return path;
        return "~" + path[length..];
    }
}
