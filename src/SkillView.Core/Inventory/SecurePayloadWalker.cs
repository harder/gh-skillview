namespace SkillView.Inventory;

internal readonly record struct NativePayloadItem(string RelativePath, string Kind,
    Stream? Content, int? UnixMode, byte[]? LinkBytes, string? LinkTarget);

internal static class SecurePayloadWalker
{
    internal static SecureFileIdentity Walk(string path, int maxDepth,
        Action<NativePayloadItem> observe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SecureRemovalBackend.IsSupported)
            throw new IOException($"Native payload inspection unavailable: {SecureRemovalBackend.UnsupportedReason}");
        return OperatingSystem.IsWindows()
            ? WindowsSecureRemovalBackend.WalkPayload(path, maxDepth, observe, cancellationToken)
            : UnixSecureRemovalBackend.WalkPayload(path, maxDepth, observe, cancellationToken);
    }
}

/// Keeps the originally created directory open so its identifier cannot be
/// recycled. Capture never reopens a replacement at its old name.
internal sealed class OwnedDirectoryLease(
    Func<CancellationToken, SecureDirectoryValidationSnapshot> capture,
    Action dispose) : IDisposable
{
    internal SecureDirectoryValidationSnapshot Capture(CancellationToken token) => capture(token);
    public void Dispose() => dispose();

    internal static OwnedDirectoryLease Open(string path) => OperatingSystem.IsWindows()
        ? WindowsSecureRemovalBackend.OpenOwnedDirectory(path)
        : UnixSecureRemovalBackend.OpenOwnedDirectory(path);
}
