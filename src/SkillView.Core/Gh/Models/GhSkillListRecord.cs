using System.Collections.Immutable;

namespace SkillView.Gh.Models;

/// Speculative shape of a `gh skill list --json` record. cli/cli#13215 has not
/// landed as of v2.91.0 — the field set here is defensive and interpreted via
/// tolerant JSON parsing (see `GhSkillListAdapter`). When the upstream schema
/// lands, the adapter tightens; this record remains the SkillView-internal
/// projection.
public sealed record GhSkillListRecord
{
    public string? Name { get; init; }
    public string? Path { get; init; }
    public string? ResolvedPath { get; init; }
    public string? Repo { get; init; }
    public string? Agent { get; init; }
    public string? Scope { get; init; }
    public string? Version { get; init; }
    public string? GithubTreeSha { get; init; }
    public bool Pinned { get; init; }
    public bool IsSymlink { get; init; }
    public ImmutableArray<string> Agents { get; init; } = ImmutableArray<string>.Empty;
}
