using System.Collections.Concurrent;

namespace SkillView.Inventory;

/// Supplies observations to the shared path predicates. Cached instances belong
/// to one read-only capture; mutation admission must use the uncached mode.
internal sealed class InventoryPathComparison
{
    internal static InventoryPathComparison Uncached { get; } = new(cacheObservations: false);
    private readonly ConcurrentDictionary<string, bool>? _case;
    private readonly ConcurrentDictionary<string, string?>? _canonical;
    private readonly ConcurrentDictionary<string, string?>? _entries;
    private readonly Func<string, bool> _caseProbe;
    private readonly Func<string, string, bool> _equal;
    private readonly Func<string, string?> _canonicalProbe;
    private readonly Func<string, string?> _entryProbe;
    private readonly Func<string, string?> _computeEntry;

    internal InventoryPathComparison(bool cacheObservations = true)
    {
        if (cacheObservations)
        {
            _case = new(StringComparer.Ordinal);
            _canonical = new(StringComparer.Ordinal);
            _entries = new(StringComparer.Ordinal);
        }
        _caseProbe = CaseSensitive;
        _equal = Equal;
        _canonicalProbe = Canonical;
        _entryProbe = Entry;
        _computeEntry = path => OwnershipPath.EntryAddress(path, _canonicalProbe);
    }

    private bool CaseSensitive(string path) => _case is null
        ? PathIdentity.IsCaseSensitive(path) : _case.GetOrAdd(path, PathIdentity.IsCaseSensitive);

    internal string? Canonical(string path) => _canonical is null
        ? OwnershipPath.CanonicalCandidate(path) : _canonical.GetOrAdd(path, OwnershipPath.CanonicalCandidate);

    internal string? Entry(string path) => _entries is null ? _computeEntry(path) : _entries.GetOrAdd(path, _computeEntry);

    internal IEnumerable<string> Keys(string path)
    {
        yield return PathIdentity.Normalize(path);
        if (Canonical(path) is { } canonical) yield return PathIdentity.Normalize(canonical);
        if (Entry(path) is { } entry) yield return PathIdentity.Normalize(entry);
    }

    private bool Equal(string left, string right) => PathIdentity.Equals(left, right, _caseProbe);
    internal bool Same(string left, string right) => OwnershipPath.Same(left, right, _equal, _canonicalProbe, _entryProbe);
    internal bool IsInside(string candidate, string root) => PathIdentity.IsInside(candidate, root, _caseProbe);
}
