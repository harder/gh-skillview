namespace SkillView.Inventory;

/// Read-only metadata inspection needs a verified native ABI, not removal's
/// stronger traversal capability (such as Linux openat2).
internal static class RegularFileInspection
{
    internal static FileStream Open(string path) => OperatingSystem.IsWindows()
        ? WindowsSecureRemovalBackend.OpenRegularFileForSnapshot(path)
        : UnixSecureRemovalBackend.OpenRegularFileForSnapshot(path, out _);

    internal static void Capture(FileStream file, Func<NativePayloadItem, PayloadFileEvidence> observe, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) WindowsSecureRemovalBackend.CaptureRegularFile(file, observe, token);
        else UnixSecureRemovalBackend.CaptureRegularFile(file, observe, token);
    }

    internal static void Capture(string path, Func<NativePayloadItem, PayloadFileEvidence> observe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = OperatingSystem.IsWindows() ? WindowsSecureRemovalBackend.OpenRegularFileForMetadata(path) : Open(path);
        if (OperatingSystem.IsWindows()) WindowsSecureRemovalBackend.CaptureRegularFile(file, observe, token);
        else UnixSecureRemovalBackend.CaptureRegularFile(file, observe, token);
    }
}
