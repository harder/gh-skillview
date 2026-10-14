namespace SkillView.Inventory.Models;

/// Origin metadata for a skill installed via a "package" tool (e.g. `npx
/// skills`) that records its source repo in a `.skill-lock.json` lockfile.
/// Lets the UI group/sort by package and surface where a skill came from.
public sealed record SkillPackage(
    string Source,
    string SourceType,
    string? SourceUrl,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? UpdatedAt
)
{
    public string? LockFilePath { get; init; }
    public string? InstallationRoot { get; init; }

    /// A same-name lock entry is evidence only within its installation context.
    public bool Describes(InstalledSkill skill) => InstallationRoot is { } root
        && (PathIdentity.IsInside(skill.ResolvedPath, root)
            || skill.Agents.Any(agent => PathIdentity.IsInside(agent.Path, root)));
}
