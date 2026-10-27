using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Ui;

internal sealed record InstalledInstallHandoff(InventorySnapshot Snapshot, InstalledOverview Overview,
    string GroupKey, IReadOnlyList<string> CopyKeys)
{
    internal string? BrowseRepository { get; init; }
    internal string? BrowseContextKey { get; init; }
    internal bool BrowseOnly { get; init; }
    internal InstallNavigationEvidence? Evidence { get; init; }
    internal long CatalogGeneration { get; init; }
    internal long RequestGeneration { get; init; }
}

internal static class InstalledInstallHandoffResolver
{
    internal static InstalledInstallHandoff Resolve(InstallNavigationEvidence evidence, InventorySnapshot snapshot,
        InstalledOverview overview, CancellationToken token, IReadOnlyList<IReadOnlyList<string>>? agentFolders = null)
    {
        token.ThrowIfCancellationRequested();
        if (!evidence.ExactPlacement && agentFolders is null)
            throw new IOException("Per-agent placement alternatives were not captured; the requested locations could not be verified.");
        if (agentFolders is not null && (agentFolders.Count == 0 || agentFolders.Any(set => set.Count == 0)
            || !agentFolders.SelectMany(set => set).Select(PathIdentity.NormalizeKey).ToHashSet(StringComparer.Ordinal)
                .SetEquals(evidence.Roots.Select(PathIdentity.NormalizeKey))))
            throw new IOException("The requested installation folders do not match the captured placement.");
        var browseOnly = agentFolders?.Any(set => set.Count > 1) == true;
        if (snapshot.IsPartial || !snapshot.ConfiguredRootsComplete || !snapshot.OwnershipReadDiagnostics.IsEmpty)
            throw new IOException("Inventory coverage is partial; the installed copy could not be verified.");
        if (evidence.Roots.Count == 0) throw new IOException("No installation folders were captured.");
        if (overview.Groups.Sum(group => 1L + group.Copies.Count) > InstalledOverview.MaxRows)
            throw new IOException("Installed display coverage is bounded; the result may be omitted. Narrow the scan before choosing a copy.");
        var copies = overview.Groups.SelectMany(group => group.Copies.Select(copy => (group, copy)))
            .Where(row => row.copy.Records.Any(skill => skill.SourceIdentity.Key == evidence.Source.Key)
                && row.copy.Records.Any(skill => InPlacement(skill, evidence.Roots))).ToArray();
        if (copies.Length == 0)
            throw new IOException("No copy with the selected repository and source path was found in the installation folders.");
        if (copies.Select(row => row.group.Key).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new IOException("The selected source has conflicting groups; choose a copy in Installed after inspection.");
        if (browseOnly)
        {
            // Expanding an existing source group also shows its older copies.
            // Verify every displayed child, without attributing any copy to gh.
            var group = copies[0].group;
            copies = group.Copies.Select(copy => (group, copy)).ToArray();
        }
        var coveredRoots = browseOnly ? snapshot.ScannedRoots.Select(root => root.Path)
            .Where(root => copies.Any(row => InPlacement(row.copy.Records[0], [root]))).ToArray() : evidence.Roots;
        if (browseOnly && copies.Any(row => !InPlacement(row.copy.Records[0], coveredRoots)))
            throw new IOException("A displayed copy is outside the completed scan; its location could not be verified.");
        foreach (var root in coveredRoots)
        {
            token.ThrowIfCancellationRequested();
            if (!snapshot.ScannedRoots.Any(scan => PathIdentity.Equals(scan.Path, root))
                || !snapshot.Providers.Any(provider => provider.Provider == "gh" && provider.Succeeded
                    && provider.CoveredRoots.Any(covered => PathIdentity.Equals(covered, root))))
                throw new IOException("An installation folder is outside the completed scan; the installed copy could not be verified.");
        }
        foreach (var (_, copy) in copies)
        {
            token.ThrowIfCancellationRequested();
            if (copy.Ambiguous || copy.Target is not { } skill || skill.SourceIdentity.Key != evidence.Source.Key
                || !skill.MetadataConflicts.IsEmpty || !skill.OwnershipDiagnostics.IsEmpty
                || skill.AllOwnershipClaims.Any(owner => (skill with { FrontMatter = new(), Package = owner, OwnershipClaims = [] }).SourceIdentity.Key != evidence.Source.Key)
                || skill.ObservedSubject is not { ExposureCoverageComplete: true, NativeObject.Status: "observed" } subject
                || subject.Source.Key != evidence.Source.Key || subject.InstallationId != skill.InstallationId
                || !PathIdentity.Equals(subject.Path, skill.ResolvedPath))
                throw new IOException("Source, owner or physical-copy evidence is unknown or conflicted.");
            if (!SecureRemovalBackend.TryObserveIdentity(skill.ResolvedPath, out var identity, out _)
                || NativeObjectObservation.From(identity, subject.NativeObject.CapturedAt!.Value, subject.NativeObject.Role) != subject.NativeObject)
                throw new IOException("The installed physical copy changed during verification; refresh Installed before choosing it.");
        }
        // Strict placement needs every root. Alternatives still need at least
        // one verified location for each independently requested agent.
        if (browseOnly)
        {
            if (agentFolders!.Any(set => !copies.Any(row => InPlacement(row.copy.Target!, set))))
                throw new IOException("Not all requested locations were verified.");
        }
        else if (evidence.Roots.Any(root => !copies.Any(row => InPlacement(row.copy.Target!, [root]))))
            throw new IOException("The selected source was not verified in every installation folder.");
        return new(snapshot, overview, copies[0].group.Key, copies.Select(row => row.copy.Key).ToArray()) { Evidence = evidence, BrowseOnly = browseOnly };
    }

    private static bool InPlacement(InstalledSkill skill, IReadOnlyList<string> roots) => roots.Any(root =>
        PathIdentity.Equals(Path.GetDirectoryName(skill.ResolvedPath) ?? "", root)
        || skill.Agents.Any(agent => PathIdentity.Equals(Path.GetDirectoryName(agent.Path) ?? "", root)));
}
