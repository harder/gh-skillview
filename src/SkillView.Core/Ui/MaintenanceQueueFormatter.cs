using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal static class MaintenanceQueueFormatter
{
    internal const int MaxDetailCharacters = 32768;
    internal const int MaxSummaryCharacters = 4096;
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

    internal static string Render(MaintenanceQueueEntry entry, CancellationToken token, bool showDetails = false)
    {
        if (showDetails) return RenderEvidence(entry, token);
        var fields = RenderFields(SummaryFields(entry, token), token, MaxSummaryCharacters - 512);
        var text = new StringBuilder($"## {DescribeIssue(entry)}\n\n").Append(fields.Text);
        if (fields.OmittedFields > 0)
            text.AppendLine("\nSome values are too long for this summary; open details to inspect them.");
        text.AppendLine("\nOpening review changes nothing.");
        token.ThrowIfCancellationRequested();
        return text.ToString();
    }

    internal static string DescribeIssue(MaintenanceQueueEntry entry) => entry.Issue switch
    {
        "installation-review" => "Check this installation",
        "EmptyDirectory" => "Empty directory",
        "Malformed" => "Missing or invalid skill file",
        "BrokenSymlink" => "Broken link",
        "BrokenSharedMapping" => "Shared link needs attention",
        "NameCollision" => "Same name, multiple copies",
        "Duplicate" => "Multiple copies",
        "Untracked" => "Source not recorded",
        "MetadataConflict" => "Conflicting installation records",
        "HiddenNestedResidue" => "Nested files need review",
        "SourceOrphaned" => "Source needs review",
        "OrphanCanonicalCopy" => "Shared copy needs review",
        _ => "Installation needs review"
    };

    private static IEnumerable<(string, string?)> SummaryFields(MaintenanceQueueEntry entry, CancellationToken token)
    {
        yield return ("Skill", entry.Title);
        yield return ("Why", DescribeReason(entry));
        yield return ("Next", entry.Issue is "NameCollision" or "Duplicate"
            ? "Compare copies, then review an update/version or cleanup for one location; automatic cleanup is unavailable for this conflict."
            : entry.Cleanup is null
            ? "Check for updates."
            : entry.Cleanup.ReviewOnly ? "Inspect this issue; automatic cleanup is unavailable."
            : "Review cleanup.");
        yield return ("Changes", DescribeEffect(entry));
        yield return ("Undo", entry.Cleanup is null
            ? "Saved updates keep the original for undo."
            : "Deletion has no automatic undo.");
        var impact = entry.Cleanup?.Impact;
        var exposures = impact?.AffectedExposures ?? entry.Subject.Exposures;
        var agents = new HashSet<(string?, Scope?, string?)>();
        foreach (var exposure in exposures)
        {
            token.ThrowIfCancellationRequested();
            var member = exposure.Membership;
            if (!agents.Add((member.AgentId, member.Scope, member.Profile))) continue;
            if (agents.Count > 8)
            {
                yield return ("More agents", "See technical details for additional recorded exposures.");
                break;
            }
            yield return ("Agent", $"{member.AgentId ?? "unknown"} · {(member.Scope ?? entry.Subject.Scope)?.ToString() ?? "scope unknown"}"
                + (member.Profile is null ? "" : " · " + member.Profile));
        }
        if (exposures.IsEmpty) yield return ("Affected agents", "No agent exposure was recorded.");
        if (!(impact?.ExposureCoverageComplete ?? entry.Subject.ExposureCoverageComplete))
            yield return ("Coverage", "Other consumers may exist; discovery is incomplete.");
        if (impact is { RetainedExposures.IsEmpty: false })
            yield return ("Other consumers", "Other recorded exposures are retained by this link-only action; see details.");
        if (entry.Pinned) yield return ("Pinned", "Routine updates leave this version pinned.");
        if (entry.CleanupSuppressed) yield return ("Cleanup", "Cleanup suggestions are suppressed for this installation.");
        yield return ("Location", entry.Subject.Path);
    }

    private static string DescribeEffect(MaintenanceQueueEntry entry)
    {
        if (entry.Cleanup is not { } cleanup)
            return "Updates affect this copy and agents sharing its files.";
        if (cleanup.ReviewOnly)
            return "Inspection only; no automatic removal.";
        if (cleanup.Impact?.Operation == "unlink")
            return "If confirmed, removes only this link. Target files stay.";
        if (cleanup.Kind == CleanupClassifier.CandidateKind.EmptyDirectory)
            return "If confirmed, removes this directory only while it is empty.";
        return "If confirmed, removes this installation. Its recorded consumers may lose access.";
    }

    private static string DescribeReason(MaintenanceQueueEntry entry) => entry.Cleanup?.Kind switch
    {
        null => "Update availability has not been checked.",
        CleanupClassifier.CandidateKind.Malformed => entry.Cleanup.Skill?.Validity switch
        {
            ValidityState.MissingSkillMd => "This directory has no SKILL.md file.",
            ValidityState.UnparsableFrontMatter => "The skill's metadata could not be read.",
            ValidityState.NameMismatch => "The skill name and its installation directory do not match.",
            _ => "The skill's metadata needs attention."
        },
        CleanupClassifier.CandidateKind.BrokenSymlink => "This link's target could not be found.",
        CleanupClassifier.CandidateKind.NameCollision => "Other copies use this name; sources or versions may differ.",
        CleanupClassifier.CandidateKind.Untracked => "Not listed by gh; another installer may own it.",
        CleanupClassifier.CandidateKind.MetadataConflict => "Installation records disagree about this copy; inspect the details before changing it.",
        _ => entry.Reason
    };

    private static string RenderEvidence(MaintenanceQueueEntry entry, CancellationToken token)
    {
        var rendered = RenderFields(Fields(entry), token, MaxDetailCharacters - 1536);
        token.ThrowIfCancellationRequested();
        var text = new StringBuilder("## Selected maintenance observation\n\n").Append(rendered.Text);
        text.AppendLine($"\nFields omitted from this bounded preview: {rendered.OmittedFields}. Exact fields are omitted whole, never shortened into selectors.");
        text.AppendLine($"For full evidence, make a fresh capture with **queue --install-id {entry.Subject.InstallationId} --json** (retain the same global workspace/profile/scan-root options).");
        text.AppendLine(entry.Issue is "NameCollision" or "Duplicate"
            ? "**Enter:** inspect related physical copies, compare two explicitly, then choose an exact installation for existing update/version or cleanup review."
            : entry.Cleanup is null
            ? "**Enter:** select this exact installation in Updates. **Dry-run** checks availability; **c** compares installed content with one saved candidate and opens its plan review. Explicit retention approval enables apply; the result verifies disk locations. Neither runs on entry."
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
            var formatted = $"**{MarkdownTableFormatter.FormatCodeSpan(label)}:** {MarkdownTableFormatter.FormatCodeSpan(value)}\n\n";
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
        foreach (var reload in AgentReloadGuidance.For(subject))
        {
            yield return ("Reload for " + reload.Agent, reload.Requirement + ": " + reload.Reason);
            yield return ("Reload source", reload.Source);
            yield return ("Reload source version", reload.SourceVersion);
        }
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
