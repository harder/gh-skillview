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
}
