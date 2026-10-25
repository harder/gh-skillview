namespace SkillView.Inventory.Models;

/// A read-only distributor boundary, independent of upstream source and session loading.
public sealed record ManagedSkillRegion(string Path, string Provider, string AgentId,
    string Kind, string? PluginId = null, string? PackageVersion = null, string? Context = null)
{
    public string? ObservedCanonicalPath { get; init; }
    public Scope? RegistrationScope { get; init; }
    public string? RegistrationPath { get; init; }
    // Read-only consumers report an unknown comparison as partial coverage.
    // Mutation admission always uses the strict Covers/IsInside predicates.
    internal bool? ObserveCoverage(string path, InventoryPathComparison? paths = null)
    {
        try { return CoversObserved(path, paths); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    // Reverse containment also matters: an ordinary install root can contain a
    // managed child through an ancestor alias. Keep the captured boundary even
    // if that alias is subsequently retargeted.
    internal bool IsInside(string parent)
    {
        if (PathIdentity.IsInside(Path, parent)) return true;
        var canonicalParent = OwnershipPath.CanonicalCandidate(parent) ?? OwnershipPath.EntryAddress(parent)
            ?? throw new IOException("Managed ownership parent could not be inspected.");
        var canonicalRegion = OwnershipPath.CanonicalCandidate(Path) ?? OwnershipPath.EntryAddress(Path)
            ?? throw new IOException("Managed ownership address could not be inspected.");
        return PathIdentity.IsInside(canonicalRegion, canonicalParent)
            || ObservedCanonicalPath is { } captured && PathIdentity.IsInside(captured, canonicalParent);
    }
    internal bool Covers(string path) => CoversObserved(path, null);

    private bool CoversObserved(string path, InventoryPathComparison? paths)
    {
        paths ??= InventoryPathComparison.Uncached;
        if (paths.IsInside(path, Path)) return true;
        if (ObservedCanonicalPath is { } captured && paths.IsInside(path, captured)) return true;
        var region = paths.Canonical(Path) ?? paths.Entry(Path);
        var candidate = paths.Canonical(path) ?? paths.Entry(path);
        if (region is null || candidate is null)
            throw new IOException("Managed ownership address could not be inspected.");
        return paths.IsInside(candidate, region)
            || ObservedCanonicalPath is { } previous && paths.IsInside(candidate, previous);
    }

    internal SkillPackage Claim(InstalledSkill skill) => new(PluginId ?? Kind, "unknown", null, null, null)
    {
        Provider = Provider,
        ReadOnly = true,
        IsExclusiveOwner = true,
        PluginName = PluginId,
        PackageVersion = PackageVersion,
        ManagedRegion = this,
        InstallationMode = "managed-read-only",
        InstallationRoot = System.IO.Path.GetDirectoryName(skill.ResolvedPath),
        CoveredRoots = [System.IO.Path.GetDirectoryName(skill.ResolvedPath)!],
        Selector = System.IO.Path.GetFileName(skill.ResolvedPath),
        ContextRoot = Context
    };
}
