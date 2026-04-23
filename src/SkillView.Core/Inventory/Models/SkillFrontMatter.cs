using System.Collections.Immutable;

namespace SkillView.Inventory.Models;

/// Parsed SKILL.md front-matter. Only the fields SkillView actually uses are
/// modeled — unknown keys are ignored. §9, §7.1.A.
public sealed record SkillFrontMatter
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public string? License { get; init; }
    public string? Upstream { get; init; }
    public string? GithubTreeSha { get; init; }
    public bool Pinned { get; init; }
    public ImmutableArray<string> AllowedTools { get; init; } = ImmutableArray<string>.Empty;
    public ImmutableArray<string> Agents { get; init; } = ImmutableArray<string>.Empty;
    public ImmutableDictionary<string, string> Extra { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    public static SkillFrontMatter Empty { get; } = new();
}
