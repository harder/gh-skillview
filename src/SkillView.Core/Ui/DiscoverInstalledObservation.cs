using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;

namespace SkillView.Ui;

// Frozen display evidence only. Never used for install/update admission.
internal sealed class DiscoverInstalledObservation(InventorySnapshot snapshot)
{
    private readonly HashSet<string> _sources = snapshot.Skills
        .Where(skill => skill.OwnershipDiagnostics.IsEmpty && skill.MetadataConflicts.IsEmpty
            && skill.AllOwnershipClaims.All(owner => (skill with { FrontMatter = new(), Package = owner, OwnershipClaims = [] }).SourceIdentity.Key == skill.SourceIdentity.Key))
        .Select(skill => skill.SourceIdentity).Where(source => source.Provider == "repository" && source.Key is not null)
        .Select(source => source.Key!).ToHashSet(StringComparer.Ordinal);
    private static SkillSourceIdentity? Anchor(SearchResultSkill result) =>
        result.Repo is { } repo && !string.IsNullOrWhiteSpace(result.Path)
            ? InstallNavigationEvidence.SourceAnchor(repo, result.Path) : null;
    internal bool IsObserved(SearchResultSkill result) => result.Repo is { } repo
        && Anchor(result)?.Key is { } key && _sources.Contains(key);

    internal string Describe(SearchResultSkill result)
    {
        var coverage = snapshot.IsPartial || !snapshot.ConfiguredRootsComplete || !snapshot.OwnershipReadDiagnostics.IsEmpty
            ? " Inventory coverage is incomplete." : " Within captured locations only.";
        if (IsObserved(result)) return "Installed: observed repository + source path." + coverage;
        if (result.Repo is null || Anchor(result) is null)
            return "Installed: unknown; search supplied no trustworthy source path. Authored names cannot identify a source subpath." + coverage;
        return "Installed: no matching repository + source path observed." + coverage;
    }
}
