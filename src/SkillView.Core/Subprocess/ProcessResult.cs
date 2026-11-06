namespace SkillView.Subprocess;

public enum ProcessStartState { Unknown, NotStarted, Started }

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
    public ProcessStartState StartState { get; init; }
    public bool ChildDrained { get; init; }
    public bool Started { get; init; }
    public bool Cancelled { get; init; }
    public bool StdOutTruncated { get; init; }
    public bool StdErrTruncated { get; init; }
}
