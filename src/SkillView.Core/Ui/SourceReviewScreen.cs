using System.Text;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal static class SourceReviewScreen
{
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
