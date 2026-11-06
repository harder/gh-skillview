namespace SkillView.Gh.Models;

/// One submitted selection, retaining each child result and selectors never
/// submitted after a failure/cancellation. A repository-wide child is one
/// outcome; its output cannot establish per-skill success.
public sealed record InstallAttemptSummary(
    IReadOnlyList<InstallResult> Results,
    IReadOnlyList<string> NotAttempted, bool CancellationRequested = false)
{
    internal const int MaxBatchOutputCharacters = 1024 * 1024;

    internal static InstallResult Retain(InstallResult result, ref int remaining)
    {
        var stdout = Take(result.StdOut, ref remaining);
        var stderr = Take(result.StdErr, ref remaining);
        return result with
        {
            StdOut = stdout,
            StdErr = stderr,
            StdOutTruncated = result.StdOutTruncated || stdout.Length != result.StdOut.Length,
            StdErrTruncated = result.StdErrTruncated || stderr.Length != result.StdErr.Length,
            ErrorMessage = result.ErrorMessage is { Length: > 8192 } error ? error[..8192] + "\n[truncated]" : result.ErrorMessage,
        };
    }

    private static string Take(string text, ref int remaining)
    {
        var length = Math.Min(text.Length, remaining);
        remaining -= length;
        return length == text.Length ? text : text[..length];
    }

    public bool MayHaveChangedFiles => Results.Any(result => result.MayHaveChangedFiles);
    public bool Succeeded => Results.Count > 0 && NotAttempted.Count == 0 && Results.All(result => result.Succeeded);
    public bool Canceled => CancellationRequested || Results.Any(result => result.Canceled);
    public static InstallAttemptSummary Single(InstallResult result) => new([result], []);
}
