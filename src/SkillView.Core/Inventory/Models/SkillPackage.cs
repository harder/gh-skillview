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
);
