namespace SkillView.Inventory;

/// State files are created/enumerated relative to a held native directory.
/// Canonical addresses are display/policy evidence, never a replacement for it.
internal abstract class NativeStateDirectory : IDisposable
{
    internal abstract string CanonicalPath { get; }
    internal abstract FileStream OpenFile(string name, bool writerLock, CancellationToken token);
    internal abstract IEnumerable<(string Name, long Bytes)> EnumerateFiles(CancellationToken token);
    internal abstract void FlushPublication(CancellationToken token);
    internal abstract void ValidateLocation(CancellationToken token);
    public abstract void Dispose();

    internal static NativeStateDirectory OpenOrCreate(string path, IReadOnlyList<string> excluded, CancellationToken token, Action<string>? afterCreate = null, Action? beforeAncestorRefresh = null)
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
            ? WindowsSecureRemovalBackend.OpenStateDirectory(path, canonicalExclusions, token, afterCreate, beforeAncestorRefresh)
            : UnixSecureRemovalBackend.OpenStateDirectory(path, canonicalExclusions, token, afterCreate, beforeAncestorRefresh);
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
