namespace SkillView.Gh.Models;

public enum InstallAttemptOutcome
{
    Unknown,
    RefusedPreflight,
    CanceledBeforeStart,
    StartFailed,
    ChildFailed,
    ChildCanceled,
    Succeeded,
}

/// Outcome of a `gh skill install` invocation. The raw `StdOut` is preserved
/// for UI surfacing — `gh skill install` prints an install summary that the
/// adapter does not try to structurally parse (no `--json` on install as of
/// gh 2.94.0).
public sealed record InstallResult
{
    // Transient navigation observation only; excluded from every wire format.
    [System.Text.Json.Serialization.JsonIgnore]
    internal InstallNavigationEvidence? Navigation { get; init; }

    // Unknown is intentional for old constructors/factories: a failure alone
    // cannot prove no writer ran. These facts never grant mutation authority.
    public bool CancellationRequested { get; init; }
    public bool StdOutTruncated { get; init; }
    public bool StdErrTruncated { get; init; }
    public bool ReadOnlyDiscovery { get; init; }
    public bool ChildDrained { get; init; }
    public InstallAttemptOutcome Attempt { get; init; }
    public bool MayHaveChangedFiles => !ReadOnlyDiscovery && Attempt is not (InstallAttemptOutcome.RefusedPreflight
        or InstallAttemptOutcome.CanceledBeforeStart or InstallAttemptOutcome.StartFailed);
    public bool Canceled => CancellationRequested || Attempt is InstallAttemptOutcome.CanceledBeforeStart or InstallAttemptOutcome.ChildCanceled;
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
