using System.Text.Json.Serialization;

namespace SkillView.Gh.Models;

/// `gh skill search --json` fields (§7.1.B).
public sealed record SearchResultSkill(
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("namespace")] string? Namespace,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("repo")] string? Repo,
    [property: JsonPropertyName("skillName")] string? SkillName,
    [property: JsonPropertyName("stars")] int? Stars
);
