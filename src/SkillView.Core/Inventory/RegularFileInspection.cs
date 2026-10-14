namespace SkillView.Inventory;

/// Read-only metadata inspection needs a verified native ABI, not removal's
/// stronger traversal capability (such as Linux openat2).
internal static class RegularFileInspection
{
    internal static FileStream Open(string path) => OperatingSystem.IsWindows()
        ? WindowsSecureRemovalBackend.OpenRegularFileForSnapshot(path)
        : UnixSecureRemovalBackend.OpenRegularFileForSnapshot(path, out _);
}
