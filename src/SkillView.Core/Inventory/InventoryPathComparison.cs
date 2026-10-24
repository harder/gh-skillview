using System.Collections.Concurrent;

namespace SkillView.Inventory;

/// Reuses read-only path observations within one inventory matching pass only.
/// Never use this for mutation admission or retain it across inventory captures.
internal sealed class InventoryPathComparison
{
    private readonly ConcurrentDictionary<string, bool> _case = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _canonical = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _entries = new(StringComparer.Ordinal);

    private bool CaseSensitive(string path) => _case.GetOrAdd(path, PathIdentity.IsCaseSensitive);

    internal string? Canonical(string path) => _canonical.GetOrAdd(path, OwnershipPath.CanonicalCandidate);

    internal string? Entry(string path) => _entries.GetOrAdd(path, OwnershipPath.EntryAddress);

    internal IEnumerable<string> Keys(string path)
    {
        yield return PathIdentity.Normalize(path);
        if (Canonical(path) is { } canonical) yield return PathIdentity.Normalize(canonical);
        if (Entry(path) is { } entry) yield return PathIdentity.Normalize(entry);
    }

    private bool Equal(string left, string right)
    {
        var a = PathIdentity.Normalize(left);
        var b = PathIdentity.Normalize(right);
        return string.Equals(a, b, CaseSensitive(a) && CaseSensitive(b)
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    internal bool Same(string left, string right) => Equal(left, right)
        || Canonical(left) is { } a && Canonical(right) is { } b && Equal(a, b)
        || Entry(left) is { } c && Entry(right) is { } d && Equal(c, d);

    internal bool IsInside(string candidate, string root)
    {
        var path = PathIdentity.Normalize(candidate);
        var parent = PathIdentity.Normalize(root);
        var comparison = CaseSensitive(parent) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(path, parent, comparison)
            || path.StartsWith(parent.EndsWith('/') ? parent : parent + "/", comparison);
    }
}
