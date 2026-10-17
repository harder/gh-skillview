using System.Text;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal static class MaintenanceQueueFormatter
{
    internal const int MaxDetailCharacters = 32768;
    internal const int MaxCoverageCharacters = 8192;
    internal sealed record RenderedFields(string Text, int OmittedFields);

    internal static string RenderDiagnostics(MaintenanceQueue queue, CancellationToken token)
    {
        if (queue.Diagnostics.IsEmpty && !queue.Partial) return string.Empty;
        var rendered = RenderFields(queue.Diagnostics.Select(diagnostic => ("Coverage", (string?)diagnostic)),
            token, MaxCoverageCharacters - 512);
        return "\n## Inventory coverage\n\n" + rendered.Text
            + $"\nCoverage fields omitted: {rendered.OmittedFields}. Full captured diagnostics are emitted by queue --json; running it makes a fresh capture.\n";
    }

    internal static string Render(MaintenanceQueueEntry entry, CancellationToken token)
    {
        var rendered = RenderFields(Fields(entry), token, MaxDetailCharacters - 1536);
        token.ThrowIfCancellationRequested();
        var text = new StringBuilder("## Selected maintenance observation\n\n").Append(rendered.Text);
        text.AppendLine($"\nFields omitted from this bounded preview: {rendered.OmittedFields}. Exact fields are omitted whole, never shortened into selectors.");
        text.AppendLine($"For full evidence, make a fresh capture with **queue --install-id {entry.Subject.InstallationId} --json** (retain the same global workspace/profile/scan-root options).");
        text.AppendLine(entry.Cleanup is null
            ? "**Enter:** select this exact installation in Updates. **Dry-run** checks availability; **u** prepares a saved review. Neither runs on entry."
            : "**Enter:** refresh and review only this exact cleanup issue. Review is required before any action.");
        text.AppendLine("**r:** refresh disk inventory only. Update availability and active sessions remain unknown. The queue is observation, not mutation approval.");
        return text.ToString();
    }

    // Bounds allocation before escaping, then checks the escaped length. Whole
    // fields are skipped, preserving exact identity semantics and omission counts.
    internal static RenderedFields RenderFields(IEnumerable<(string Label, string? Value)> fields,
        CancellationToken token, int budget)
    {
        var text = new StringBuilder();
        var omitted = 0;
        foreach (var (label, raw) in fields)
        {
            token.ThrowIfCancellationRequested();
            var value = raw ?? "unknown";
            var remaining = budget - text.Length;
            if (value.Length > remaining || label.Length + 12 > remaining)
            {
                omitted++;
                continue;
            }
            var formatted = $"**{label}:** {MarkdownTableFormatter.FormatCodeSpan(value)}  \n";
            token.ThrowIfCancellationRequested();
            if (formatted.Length > remaining) omitted++;
            else text.Append(formatted);
        }
        token.ThrowIfCancellationRequested();
        return new(text.ToString(), omitted);
    }

    private static IEnumerable<(string, string?)> Fields(MaintenanceQueueEntry entry)
    {
        var subject = entry.Subject;
        yield return ("Installation address ID", subject.InstallationId);
        yield return ("Physical address", subject.Path);
        yield return ("Issue", entry.Issue);
        yield return ("Reason", entry.Reason);
        yield return ("Confidence", entry.Confidence);
        yield return ("Source", subject.Source.Origin);
        yield return ("Source evidence", subject.Source.Evidence);
        yield return ("Source subpath", subject.Source.Subpath);
        yield return ("Authored version", subject.Revision.AuthoredVersion);
        yield return ("Recorded ref", subject.Revision.RepositoryRef);
        yield return ("Recorded tree", subject.Revision.RecordedTree);
        yield return ("Pinned ref", subject.Revision.PinnedRef);
        yield return ("Cleanup suppression", entry.CleanupSuppressed ? "suppressed" : "no recorded suppression");
        yield return ("Pin", entry.Pinned ? "recorded pin; explicit unpin review required" : "no recorded pin");
        yield return ("Disablement", entry.DisablementState);
        yield return ("Migration", entry.MigrationState);
        yield return ("Update availability", entry.UpdateState);
        yield return ("Update reason", entry.UpdateReason);
        yield return ("Active session", entry.RuntimeState);
        yield return ("Session reason", entry.RuntimeReason);
        yield return ("Materialization", subject.Materialization);
        yield return ("Captured", subject.CapturedAt?.ToString("O"));
        yield return ("Native observation", subject.NativeObject.Status);
        yield return ("Native object ID", subject.NativeObject.ObjectId);
        yield return ("Native observation role", subject.NativeObject.Role);
        yield return ("Exposure coverage", subject.ExposureCoverage);
        foreach (var path in entry.AffectedPaths) yield return ("Affected path", path);
        foreach (var owner in subject.Owners)
        {
            yield return ("Owner provider", owner.Provider);
            yield return ("Owner source", owner.Source);
            yield return ("Owner mode", owner.InstallationMode);
            yield return ("Owner registry", owner.LockFilePath);
        }
        var impact = entry.Cleanup?.Impact ?? OperationImpact.Payload(subject, "review-observation");
        yield return ("Payload retained", impact.PayloadRetained.ToString());
        yield return ("Retained payload", impact.RetainedPayloadPath);
        foreach (var (exposure, affected) in impact.AffectedExposures.Select(exposure => (exposure, true))
            .Concat(impact.RetainedExposures.Select(exposure => (exposure, false))))
        {
            yield return (affected ? "Affected exposure path" : "Retained exposure path", exposure.Membership.Path);
            yield return ("Exposure agent", exposure.Membership.AgentId);
            yield return ("Exposure scope", exposure.Membership.Scope?.ToString());
            yield return ("Exposure profile", exposure.Membership.Profile);
            yield return ("Exposure context", exposure.Membership.Context);
            yield return ("Exposure discovery root", exposure.Membership.DiscoveryRoot);
            yield return ("Exposure agent version", exposure.Membership.AgentVersion);
        }
    }
}
