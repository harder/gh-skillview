using SkillView.Subprocess;

namespace SkillView.Maintenance;

/// Failed source requests retain both diagnostic streams. Successful blob/tree
/// responses are payload inputs, not human update output.
internal sealed class GhSourceRequestException(string message, string stdout, string stderr) : IOException(message)
{
    private const int MaxDiagnosticChars = 64 * 1024;
    internal string StdOut { get; } = Bound(stdout);
    internal string StdErr { get; } = Bound(stderr);
    internal GhSourceRequestException(string message, ProcessResult result) : this(message, result.StdOut, result.StdErr) { }
    private static string Bound(string value) => value.Length <= MaxDiagnosticChars ? value
        : value[..MaxDiagnosticChars] + "\n[Source diagnostic capture limit reached; remaining text omitted.]";
}
