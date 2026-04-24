using System.IO;
using System.Runtime.InteropServices;
using SkillView.Logging;

namespace SkillView.Inventory;

/// Write or remove the `.skillview-ignore` marker file. The marker is
/// the only on-disk state SkillView writes outside explicit user actions on
/// skill directories themselves.
public static class IgnoreMarker
{
    public static string MarkerPathFor(string skillDir) =>
        Path.Combine(skillDir, LocalSkillScanner.IgnoreMarkerName);

    public static bool Exists(string skillDir) => File.Exists(MarkerPathFor(skillDir));

    /// Create the marker (zero-byte file). Returns true if the marker was
    /// newly created, false if it already existed.
    public static bool Write(string skillDir, Logger? logger = null)
    {
        if (!Directory.Exists(skillDir))
        {
            throw new DirectoryNotFoundException($"cannot write ignore marker: '{skillDir}' is not a directory");
        }
        var path = MarkerPathFor(skillDir);
        if (File.Exists(path))
        {
            logger?.Debug("cleanup.ignore", $"marker already present: {path}");
            return false;
        }
        using (File.Create(path)) { }
        TrySetPosixMode(path, "600");
        logger?.Info("cleanup.ignore", $"wrote marker: {path}");
        return true;
    }

    /// Remove the marker. Returns true if it existed and was removed.
    public static bool Remove(string skillDir, Logger? logger = null)
    {
        var path = MarkerPathFor(skillDir);
        if (!File.Exists(path))
        {
            return false;
        }
        File.Delete(path);
        logger?.Info("cleanup.ignore", $"removed marker: {path}");
        return true;
    }

    private static void TrySetPosixMode(string path, string octalMode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var mode = Convert.ToInt32(octalMode, 8);
            _ = chmod(path, mode);
        }
        catch
        {
            // best-effort — marker contents are empty, so permissions are cosmetic.
        }
    }

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int chmod(string path, int mode);
}
