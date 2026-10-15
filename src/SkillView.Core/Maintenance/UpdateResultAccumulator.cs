using System.Collections.Immutable;
using System.Text;
using SkillView.Gh.Models;

namespace SkillView.Maintenance;

/// Retains results across one captured review queue without retaining workers,
/// saved candidates or an unbounded copy of each subprocess response.
internal sealed class UpdateResultAccumulator
{
    internal const int MaxStreamChars = 1024 * 1024;
    internal const int MaxErrorChars = 64 * 1024;
    internal const int MaxNotices = 128;
    internal const int MaxEntries = SkillUpdateCoordinator.MaxTargets * 2;
    internal const int MaxFieldChars = 4096;
    private readonly StringBuilder stdout = new(), stderr = new(), errors = new();
    private readonly List<UpdateNotice> notices = [];
    private readonly List<UpdateEntry> entries = [];
    private bool stdoutOmitted, stderrOmitted, errorsOmitted, noticesOmitted, entriesOmitted;
    private bool succeeded = true, canceled, partial, attempted;
    private int failureExit;

    internal UpdateResult Append(UpdateResult result)
    {
        AppendText(stdout, result.StdOut, MaxStreamChars, ref stdoutOmitted);
        AppendText(stderr, result.StdErr, MaxStreamChars, ref stderrOmitted);
        AppendText(errors, result.ErrorMessage, MaxErrorChars, ref errorsOmitted);
        foreach (var notice in result.Notices)
        {
            if (notices.Count == MaxNotices) { noticesOmitted = true; continue; }
            notices.Add(new(Bound(notice.Stream, ref noticesOmitted)!, Bound(notice.Text, ref noticesOmitted)!));
        }
        foreach (var entry in result.Entries)
        {
            if (entries.Count == MaxEntries) { entriesOmitted = true; continue; }
            entries.Add(entry with
            {
                Name = Bound(entry.Name, ref entriesOmitted)!,
                Repo = Bound(entry.Repo, ref entriesOmitted),
                FromVersion = Bound(entry.FromVersion, ref entriesOmitted),
                ToVersion = Bound(entry.ToVersion, ref entriesOmitted),
                Status = Bound(entry.Status, ref entriesOmitted)!
            });
        }
        succeeded &= result.Succeeded;
        canceled |= result.Cancelled;
        partial |= result.ReconciliationPartial;
        attempted |= result.MutationAttempted;
        if (failureExit == 0 && result.ExitCode != 0) failureExit = result.ExitCode;
        var retained = notices.ToImmutableArray().ToBuilder();
        if (stdoutOmitted) retained.Add(new("capture", "Queue stdout exceeded its capture limit; earlier output is retained and later text was omitted."));
        if (stderrOmitted) retained.Add(new("capture", "Queue stderr exceeded its capture limit; earlier diagnostics are retained and later text was omitted."));
        if (errorsOmitted) retained.Add(new("capture", "Queue error summaries exceeded their capture limit; earlier summaries are retained."));
        if (noticesOmitted) retained.Add(new("capture", "Some queue notices or notice text were omitted at the capture limit."));
        if (entriesOmitted) retained.Add(new("capture", "Some queue entries or entry fields were omitted at the capture limit; inspect exact installation outcomes."));
        // SavedPlan and Outcomes belong to the caller's current review. In
        // particular, a previous plan must never become the next Apply target.
        return result with
        {
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
            ErrorMessage = errors.Length == 0 ? null : errors.ToString(),
            Notices = retained.ToImmutable(),
            Entries = entries.ToImmutableArray(),
            Succeeded = succeeded && !canceled && !partial,
            ExitCode = canceled ? 130 : !succeeded || partial ? failureExit == 0 ? 2 : failureExit : failureExit,
            Cancelled = canceled,
            ReconciliationPartial = partial,
            MutationAttempted = attempted
        };
    }

    private static string? Bound(string? value, ref bool omitted)
    {
        if (value is null || value.Length <= MaxFieldChars) return value;
        omitted = true;
        return value[..MaxFieldChars];
    }

    private static void AppendText(StringBuilder destination, string? value, int limit, ref bool omitted)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (destination.Length > 0 && destination.Length < limit) destination.Append('\n');
        var count = Math.Min(value.Length, limit - destination.Length);
        destination.Append(value.AsSpan(0, count));
        omitted |= count < value.Length;
    }
}
