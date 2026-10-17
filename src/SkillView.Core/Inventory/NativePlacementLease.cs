using System.Collections.Immutable;

namespace SkillView.Inventory;

/// Placement only: no old payload, historical base or content freshness claim.
internal sealed record NativePlacementEvidence(string Path, string CanonicalPath,
    SecureFileIdentity ExistingAncestor, ImmutableArray<string> MissingSuffix);

internal sealed class NativePlacementLease(NativePlacementEvidence evidence, IDisposable anchor,
    Func<SecureFileIdentity> observeAnchor) : IDisposable
{
    internal NativePlacementEvidence Evidence { get; } = evidence;
    private bool disposed;

    internal static NativePlacementLease Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl)
            || path[Path.GetPathRoot(path)!.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..")) throw new IOException("Placement requires an absolute ordinary directory address.");
        if (!SecureRemovalBackend.IsSupported) throw new IOException("Native placement observation is unavailable: " + SecureRemovalBackend.UnsupportedReason);
        return OperatingSystem.IsWindows() ? WindowsSecureRemovalBackend.OpenPlacement(path, token)
            : UnixSecureRemovalBackend.OpenPlacement(path, token);
    }

    // Wrapper owns the recursive anchor continuously; freshness always walks
    // the original lexical address again, never only an expanded link target.
    internal NativePlacementLease AtLexicalAddress(string path) => new(Evidence with { Path = path }, this, observeAnchor);

    internal void Revalidate(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        var held = observeAnchor();
        if (!SameAnchor(Evidence.ExistingAncestor, held)) throw new IOException("Held source placement ancestor moved or changed identity.");
        using var fresh = Open(Evidence.Path, token);
        RequireSame(Evidence, fresh.Evidence);
    }

    internal static void RequireSame(NativePlacementEvidence expected, NativePlacementEvidence actual)
    {
        if (expected.Path != actual.Path || !PathIdentity.Equals(expected.CanonicalPath, actual.CanonicalPath)
            || !SameAnchor(expected.ExistingAncestor, actual.ExistingAncestor)
            || !expected.MissingSuffix.SequenceEqual(actual.MissingSuffix))
            throw new IOException("Recorded old-source placement changed; create a fresh source-switch review.");
    }

    private static bool SameAnchor(SecureFileIdentity left, SecureFileIdentity right) =>
        NativeDirectoryMoveLease.SameObject(left, right) && PathIdentity.Equals(left.CanonicalPath, right.CanonicalPath);

    public void Dispose() { if (disposed) return; disposed = true; anchor.Dispose(); }
}
