using System.Collections.Immutable;

namespace SkillView.Gh.Models;

/// Result of discovering the skills available in a repository via
/// `gh skill install <repo>` run non-interactively (gh ≥ 2.95.0,
/// cli/cli#13548). On success, <see cref="Skills"/> holds the parsed rows;
/// on failure the listing is empty and <see cref="ErrorMessage"/> carries the
/// trimmed stderr.
public sealed record RepoSkillListing
{
    public required string Repo { get; init; }
    public string? Version { get; init; }
    public required ImmutableArray<RepoSkill> Skills { get; init; }
    public required bool Succeeded { get; init; }
    public required int ExitCode { get; init; }
    public string? ErrorMessage { get; init; }
    public required IReadOnlyList<string> CommandLine { get; init; }

    public static RepoSkillListing Failure(
        string repo, string? version, int exitCode, string? error, IReadOnlyList<string> commandLine) =>
        new()
        {
            Repo = repo,
            Version = version,
            Skills = ImmutableArray<RepoSkill>.Empty,
            Succeeded = false,
            ExitCode = exitCode,
            ErrorMessage = error,
            CommandLine = commandLine,
        };
}
