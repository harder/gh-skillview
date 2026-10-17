namespace SkillView.Subprocess;

public sealed record ProcessResult(
    string Executable,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Duration
)
{
    public bool Succeeded => ExitCode == 0;
    public bool Started { get; init; }
    public bool Cancelled { get; init; }
    public bool StdOutTruncated { get; init; }
    public bool StdErrTruncated { get; init; }
}
