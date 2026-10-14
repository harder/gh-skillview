namespace SkillView.Inventory;

internal readonly record struct NativePayloadItem(string RelativePath, string Kind,
    Stream? Content, int? UnixMode, byte[]? LinkBytes, string? LinkTarget, SecureFileIdentity Identity);

internal readonly record struct NativePayloadRoot(SecureFileIdentity Identity, int? UnixMode);

internal readonly record struct PayloadFileEvidence(string Hash, long Bytes);

internal static class SecurePayloadWalker
{
    internal static NativePayloadRoot Walk(string path, int maxDepth,
        Func<NativePayloadItem, PayloadFileEvidence?> observe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SecureRemovalBackend.IsSupported)
            throw new IOException($"Native payload inspection unavailable: {SecureRemovalBackend.UnsupportedReason}");
        return OperatingSystem.IsWindows()
            ? WindowsSecureRemovalBackend.WalkPayload(path, maxDepth, observe, cancellationToken)
            : UnixSecureRemovalBackend.WalkPayload(path, maxDepth, observe, cancellationToken);
    }

    internal static void VerifyContent(Stream stream, PayloadFileEvidence expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!stream.CanSeek || stream.Length != expected.Bytes)
            throw new IOException("payload file length changed before snapshot completion");
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(buffer);
            if (count == 0) break;
            bytes += count;
            if (bytes > expected.Bytes) throw new IOException("payload file grew before snapshot completion");
            hash.AppendData(buffer.AsSpan(0, count));
        }
        if (bytes != expected.Bytes || stream.Length != expected.Bytes
            || Convert.ToHexString(hash.GetHashAndReset()) != expected.Hash)
            throw new IOException("payload file content changed before snapshot completion");
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
