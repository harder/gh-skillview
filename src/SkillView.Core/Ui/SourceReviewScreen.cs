using System.Text;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal static class SourceReviewScreen
{
    internal const int MaxDiffCharacters = 256 * 1024;

    // Presentation of held evidence only. Opening this text performs no reads,
    // acknowledgement, or mutation and never treats current live files as frozen.
    internal static string RenderDiff(ReplacementResult plan)
    {
        var output = new StringBuilder();
        void Line(string value) => output.AppendLine(TerminalEscapeSanitizer.Sanitize(value));
        Line("Source changes / Diff — saved Installed → Candidate");
        Line("Saved evidence; live files may change. + candidate / - installed.");
        Line("Opening Diff grants no approval. Source and retention approvals stay separate.");
        Line("Ref: " + Literal(plan.Revision is { } refRevision && plan.Subject.Metadata.GithubRef == refRevision.Source.Ref
            ? "same source ref " + refRevision.Source.Ref : UpdateReviewPresentation.RefChange(plan)));
        Line("Authored version: " + Literal(plan.Subject.Metadata.Version ?? "unknown") + " → "
            + Literal(plan.Revision?.DesiredMetadata.Version ?? "unknown"));
        if (plan.SourceReview is { } review)
        {
            Line($"Changed entries: {review.Report.ChangedFileCount}; omitted entries: {review.Report.OmittedFiles}.");
            foreach (var file in review.Report.Files)
            {
                Line("Path: " + Literal(file.Path));
                Line("Installed → Candidate: " + Kind(file.Before) + " → " + Kind(file.After));
                Line($"Mode: {file.Before?.UnixMode.ToString() ?? "absent"} → {file.After?.UnixMode.ToString() ?? "absent"}; bytes: {file.Before?.Bytes.ToString() ?? "absent"} → {file.After?.Bytes.ToString() ?? "absent"}");
                Line(file.Presentation);
                Line(file.TextDiff);
                if (file.OmittedLines > 0) Line("Omitted/truncated diff lines: " + file.OmittedLines);
            }
            Line("\nDeclarations and coverage (limited grammar):");
            Line($"Declaration coverage old/new: {review.Report.Before.Coverage}/{review.Report.After.Coverage}; unmodeled behavior remains unknown.");
            Line(review.Report.Scope);
            foreach (var reason in review.Report.Before.Reasons) Line("Old coverage: " + Literal(reason));
            foreach (var reason in review.Report.After.Reasons) Line("New coverage: " + Literal(reason));
            Line($"Omitted coverage reasons old/new: {review.Report.Before.OmittedReasonCount}/{review.Report.After.OmittedReasonCount}.");
            foreach (var delta in review.Report.NewlyObserved)
                Line(Literal($"{delta.Evidence}: {delta.Observation.Category}/{delta.Observation.Kind} {delta.Observation.Path}: {delta.Observation.Value}"));
            Line("Omitted declaration deltas: " + review.Report.OmittedDeltas);
            if (plan.SourceReviewWarning is { } warning) Line(Literal(warning));
            Line("Source review: " + (plan.SourceReviewAcknowledged ? "user-declared review" : "not-reviewed"));
            Line("Authenticity, source integrity, agent compatibility and behavioral safety remain unknown. See F2 for archive manifests and technical binding.");
        }
        else
        {
            Line(RenderText(plan));
            Line("Saved file comparison (source text unavailable):");
            if (plan.Comparison is { } comparison)
            {
                foreach (var change in comparison.Paths.Take(128))
                    Line("Path: " + Literal(change.Path) + " — " + Kind(change.Installed) + " → " + Kind(change.Candidate));
                Line("Omitted comparison paths: " + Math.Max(0, comparison.Paths.Length - 128));
            }
            else Line("Comparison unavailable; file changes unknown.");
        }
        Line("\n" + UpdateReviewPresentation.ReplacementDisposition(plan));
        if (plan.Revision is { } revision)
        {
            Line("Local changes relative to recorded base: " + revision.LocalChanges.Length);
            foreach (var path in revision.LocalChanges.Take(128))
            {
                var change = plan.Comparison?.Paths.FirstOrDefault(change => change.Path == path);
                var disposition = change is { Installed: not null, Candidate: null }
                    ? "absent from candidate; original only for Undo"
                    : change is { Installed: not null, Candidate: not null }
                        ? "replaced by candidate entry; original version retained for Undo"
                        : change is { Installed: null, Candidate: not null }
                            ? "candidate adds entry; installed entry was absent"
                            : "no changed entry established by saved comparison; do not infer removal";
                Line("Local: " + Literal(path) + " — " + disposition);
            }
            if (revision.LocalChanges.Length > 128) Line("Omitted local paths: " + (revision.LocalChanges.Length - 128));
        }
        Line("Saved ID: " + Literal(plan.Id) + "; fingerprint: " + Literal(plan.PlanFingerprint));
        if (output.Length <= MaxDiffCharacters) return output.ToString();
        var end = MaxDiffCharacters - 240;
        if (char.IsHighSurrogate(output[end - 1])) end--;
        return output.ToString(0, end) + "\nDiff display limit: remaining characters omitted. Complete held report and archive bindings remain in F2 Details/saved evidence. Unknown and omitted evidence is not approval.\n";
    }

    private static string Kind(PayloadEntry? entry) => entry is null ? "absent" : Literal(entry.Kind);
    private static string Literal(string value) => (TerminalEscapeSanitizer.Sanitize(value) ?? "")
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    internal static string RenderText(ReplacementResult plan)
    {
        var output = new StringBuilder();
        if (plan.SourceReview is not { } review)
            return plan.PinPolicy is not null ? "Pin-only metadata change; upstream source review is not applicable.\n"
                : "Source review: legacy evidence unavailable; source remains not-reviewed. Create a new product plan for frozen diffs.\n";
        void Line(string value) => output.AppendLine(TerminalEscapeSanitizer.Sanitize(value));
        Line("Source review: " + (plan.SourceReviewAcknowledged ? "user-declared review" : "not-reviewed"));
        if (plan.SourceReviewWarning is { } warning) Line(warning);
        Line("Source-review fingerprint: " + review.Fingerprint);
        Line("Independent assessments: format not-assessed; agent compatibility unknown; source integrity unknown. User review supplies no authenticity or behavioral safety assurance.");
        Line("Installed archive: " + review.InstalledArchive + "; raw manifest SHA256 " + review.InstalledManifest);
        Line("Candidate archive: " + review.CandidateArchive + "; raw manifest SHA256 " + review.CandidateManifest);
        Line(review.Report.Scope);
        foreach (var file in review.Report.Files)
        {
            Line("Path: " + file.Path);
            Line("  old: " + Summary(file.Before)); Line("  new: " + Summary(file.After));
            Line("  " + file.Presentation);
            Line(file.TextDiff);
            if (file.OmittedLines > 0) Line("  Omitted/truncated diff lines: " + file.OmittedLines + "; use raw archive evidence.");
        }
        Line($"Changed entries: {review.Report.ChangedFileCount}; omitted entries: {review.Report.OmittedFiles}.");
        Line($"Declaration coverage old/new: {review.Report.Before.Coverage}/{review.Report.After.Coverage} (limited grammar).");
        foreach (var reason in review.Report.Before.Reasons) Line("Old coverage: " + reason);
        foreach (var reason in review.Report.After.Reasons) Line("New coverage: " + reason);
        Line($"Omitted coverage reasons old/new: {review.Report.Before.OmittedReasonCount}/{review.Report.After.OmittedReasonCount}.");
        foreach (var delta in review.Report.NewlyObserved)
            Line($"{delta.Evidence}: {delta.Observation.Category}/{delta.Observation.Kind} {delta.Observation.Path}: {delta.Observation.Value}");
        Line("Omitted declaration deltas: " + review.Report.OmittedDeltas);
        var prefix = plan.RegistryRestore is not null ? "registry-restore" : plan.LocalSourceSwitch is not null ? "source-switch" : plan.LocalImport is not null ? "reimport" : plan.Revision is not null ? "update" : "restore";
        Line($"Acknowledge after review: skillview {prefix}-ack-source --id {plan.Id} --expect {plan.PlanFingerprint} --review {review.Fingerprint} --yes (reuse the same scan/workspace/profile and environment context)");
        Line(SourceUpdateReview.Declaration);
        return output.ToString();
    }
    private static string? Bound(string? text) => text is { Length: > 1024 } ? text[..1024] + " [truncated; raw archive retained]" : text;
    private static string Summary(PayloadEntry? entry) => entry is null ? "absent"
        : $"{entry.Kind}; mode={entry.UnixMode}; bytes={entry.Bytes}; raw SHA256={entry.ContentHash}; link={Bound(entry.LinkTarget)}";
}
