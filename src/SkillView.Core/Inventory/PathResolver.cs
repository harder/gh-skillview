using System.IO;

namespace SkillView.Inventory;

/// Thin wrapper around `Path.GetFullPath` + `FileSystemInfo.ResolveLinkTarget`
/// that returns the canonical, realpath-resolved absolute path with symlinks
/// collapsed. Returns `null` when the link chain is broken or leads outside
/// the filesystem.
public static class PathResolver
{
    public static string? Resolve(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var full = Path.GetFullPath(path);
        try
        {
            if (IsSymlink(full))
            {
                // Try Directory and File link-target resolution in turn — either
                // may return the canonical target. `returnFinalTarget: true`
                // follows link chains; if the final target doesn't exist, treat
                // the whole chain as broken.
                var dirTarget = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
                if (dirTarget is not null)
                {
                    if (!dirTarget.Exists) return null;
                    return Path.GetFullPath(dirTarget.FullName);
                }
                var fileTarget = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
                if (fileTarget is not null)
                {
                    if (!fileTarget.Exists) return null;
                    return Path.GetFullPath(fileTarget.FullName);
                }
                return null;
            }

            if (Directory.Exists(full) || File.Exists(full)) return full;
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsSymlink(string path)
    {
        try
        {
            // `File.GetAttributes` inspects the link itself (doesn't follow),
            // so this returns true even when the target is missing.
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            return false;
        }
    }

    /// True if `candidate`, after resolution, is the same file system object
    /// as `root` or lies inside it. Used by removal guards, but also for
    /// inventory scope classification.
    public static bool IsInside(string candidate, string root)
    {
        var cKey = Normalize(candidate);
        var rKey = Normalize(root);
        if (cKey == rKey) return true;
        return cKey.StartsWith(rKey + "/", StringComparison.Ordinal);
    }

    internal static string Normalize(string path)
    {
        return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
    }
}
