namespace SkillView.Gh.Models;

/// Outcome of a `gh skill install` invocation. The raw `StdOut` is preserved
/// for UI surfacing — `gh skill install` prints an install summary that the
/// adapter does not try to structurally parse (no `--json` on install as of
/// v2.91.0; cli/cli#13215 tracks JSON output for automation).
public sealed record InstallResult
{
    public required string Repo { get; init; }
    public required string? SkillName { get; init; }
    public required string? Version { get; init; }
    public required bool Succeeded { get; init; }
    public required int ExitCode { get; init; }
    public required string StdOut { get; init; }
    public required string StdErr { get; init; }
    public required string? ErrorMessage { get; init; }
    public required IReadOnlyList<string> CommandLine { get; init; }

    public static InstallResult Failure(
        string repo,
        string? skillName,
        string? version,
        int exitCode,
        string stdErr,
        IReadOnlyList<string> commandLine) =>
        new()
        {
            Repo = repo,
            SkillName = skillName,
            Version = version,
            Succeeded = false,
            ExitCode = exitCode,
            StdOut = string.Empty,
            StdErr = stdErr,
            ErrorMessage = stdErr,
            CommandLine = commandLine,
        };
}
