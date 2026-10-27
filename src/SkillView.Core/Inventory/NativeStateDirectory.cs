namespace SkillView.Inventory;

/// State files are created/enumerated relative to a held native directory.
/// Canonical addresses are display/policy evidence, never a replacement for it.
internal abstract class NativeStateDirectory : IDisposable
{
    internal abstract string CanonicalPath { get; }
    internal abstract OwnedDirectoryLease CaptureOwnership(CancellationToken token);
    internal abstract FileStream OpenFile(string name, bool writerLock, CancellationToken token);
    internal abstract FileStream OpenExistingFile(string name, bool writerLock, CancellationToken token);
    internal abstract IEnumerable<(string Name, long Bytes, string? Error)> EnumerateEntries(CancellationToken token);
    internal virtual IEnumerable<(string Name, long Bytes, string? Error, bool Directory)> EnumerateHistoryEntries(CancellationToken token) =>
        EnumerateEntries(token).Select(entry => (entry.Name, entry.Bytes, entry.Error, false));
    internal IEnumerable<(string Name, long Bytes)> EnumerateFiles(CancellationToken token)
    {
        foreach (var entry in EnumerateEntries(token))
        {
            if (entry.Error is not null) throw new IOException(entry.Error);
            yield return (entry.Name, entry.Bytes);
        }
    }
    internal abstract void FlushPublication(CancellationToken token);
    internal virtual void FlushNamespace(CancellationToken token) => FlushPublication(token);
    internal virtual FileStream CreateAnonymousMetadataWitness(CancellationToken token) =>
        throw new IOException("Anonymous native metadata witnesses are unavailable.");
    internal abstract void ValidateLocation(CancellationToken token);
    internal abstract NativeStateDirectory CreateStagingDirectory(string name, CancellationToken token);
    internal virtual NativePayloadDirectory CreatePayloadDirectory(string name, CancellationToken token) =>
        throw new IOException("Payload materialization is unavailable for this state authority.");
    internal virtual NativePayloadDirectory CreateMetadataPayloadDirectory(string name, CancellationToken token) => CreatePayloadDirectory(name, token);
    internal abstract void SealStagingDirectory(int unixMode, CancellationToken token);
    internal abstract void ReceiveDirectory(NativeDirectoryMoveLease source, string name, CancellationToken token);
    public abstract void Dispose();

    internal static NativeStateDirectory OpenOrCreate(string path, IReadOnlyList<string> excluded, CancellationToken token, Action<string>? afterCreate = null, Action? beforeAncestorRefresh = null,
        bool allowCreate = true)
    {
        token.ThrowIfCancellationRequested();
        if (!SecureRemovalBackend.IsSupported) throw new IOException("Native state authority is unavailable: " + SecureRemovalBackend.UnsupportedReason);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in excluded)
        {
            token.ThrowIfCancellationRequested();
            paths.Add(OwnershipPath.CanonicalCandidate(item) ?? Path.GetFullPath(item));
        }
        var canonicalExclusions = paths.ToArray();
        CheckExcluded(OwnershipPath.CanonicalCandidate(path) ?? Path.GetFullPath(path), canonicalExclusions);
        return OperatingSystem.IsWindows()
            ? WindowsSecureRemovalBackend.OpenStateDirectory(path, canonicalExclusions, token, afterCreate, beforeAncestorRefresh, allowCreate)
            : UnixSecureRemovalBackend.OpenStateDirectory(path, canonicalExclusions, token, afterCreate, beforeAncestorRefresh, allowCreate);
    }

    internal static void CheckExcluded(string candidate, IReadOnlyList<string> excluded)
    {
        if (excluded.Any(payload => PathIdentity.IsInside(candidate, payload)))
            throw new IOException("Maintenance state must remain outside every known installed skill/managed skills directory.");
    }

    internal static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255
            || name.Any(character => character is '/' or '\\' or ':' || char.IsControl(character)))
            throw new IOException("Invalid state entry name.");
    }
}
