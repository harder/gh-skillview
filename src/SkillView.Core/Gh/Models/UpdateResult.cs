using System.Collections.Immutable;

namespace SkillView.Gh.Models;

/// Outcome of a `gh skill update` invocation. Captures the raw `StdOut`
/// (`gh skill update` does not yet emit JSON as of v2.91.0; cli/cli#13215
/// tracks `--json`). When `--dry-run` is set, the adapter parses the stdout
/// into a best-effort list of `UpdateEntry` rows for UI preview; the raw
/// body remains for direct rendering when the parse finds nothing.
public sealed record UpdateResult
{
    public required bool DryRun { get; init; }
    public required bool Succeeded { get; init; }
    public required int ExitCode { get; init; }
    public required string StdOut { get; init; }
    public required string StdErr { get; init; }
    public required string? ErrorMessage { get; init; }
    public required IReadOnlyList<string> CommandLine { get; init; }
    public required ImmutableArray<UpdateEntry> Entries { get; init; }

    public static UpdateResult Failure(
        bool dryRun,
        int exitCode,
        string stdErr,
        IReadOnlyList<string> commandLine) =>
        new()
        {
            DryRun = dryRun,
            Succeeded = false,
            ExitCode = exitCode,
            StdOut = string.Empty,
            StdErr = stdErr,
            ErrorMessage = stdErr,
            CommandLine = commandLine,
            Entries = ImmutableArray<UpdateEntry>.Empty,
        };
}

/// One row from a `gh skill update`(--dry-run) body. `Status` is a loose
/// classification (`updated`, `up-to-date`, `pinned`, `skipped`, `failed`,
/// `unknown`) because upstream output wording is not frozen.
public sealed record UpdateEntry
{
    public required string Name { get; init; }
    public string? Repo { get; init; }
    public string? FromVersion { get; init; }
    public string? ToVersion { get; init; }
    public required string Status { get; init; }
}
