using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

/// Human-facing summaries. Captured plans, approvals and mutation authority stay
/// with the existing update workflow; these strings are never action selectors.
internal static class UpdateReviewPresentation
{
    internal const int MaxCharacters = 8192;

    internal static string Selection(InstalledSkill skill) => Render("Selected installation", SelectionFields(skill),
        "**Check updates** checks availability. **Review update** prepares one saved candidate. " +
        "**Choose version...** requests a Git tag. Nothing is applied on entry.");

    private static IEnumerable<(string, string?)> SelectionFields(InstalledSkill skill)
    {
        yield return ("Skill", skill.Name);
        yield return ("Location", skill.ResolvedPath);
        yield return ("Recorded ref", skill.Revision.RepositoryRef);
        yield return ("Authored version", skill.FrontMatter.Version);
        yield return ("Used by", CopiesPresentation.Consumers(skill));
        yield return ("Source", skill.SourceIdentity.Origin);
        yield return ("Pin", skill.Revision.PinnedRef ?? (skill.Pinned ? "pinned; ref unknown" : "no pin recorded"));
    }

    internal static string Plan(ReplacementResult plan, bool awaitingReview) =>
        Render(awaitingReview ? "Review this change" : "Saved update result", PlanFields(plan), ReviewGuidance(plan, awaitingReview));

    private static IEnumerable<(string, string?)> PlanFields(ReplacementResult plan)
    {
        yield return ("Skill", plan.Subject.Name);
        yield return ("Location", plan.OriginalPath);
        var agents = plan.Subject.Agents.Select(agent => agent.AgentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        yield return ("Affected consumers", agents.Length == 0 ? "none recorded; coverage may be incomplete" : string.Join(", ", agents));
        if (plan.Revision is { } target) yield return ("Ref change", (plan.Subject.Metadata.GithubRef ?? "unknown") + " → " + target.Source.Ref);
        yield return ("Change", plan.PinPolicy is not null ? "Clear the verified pin; keep instructions, extras and source revision."
            : plan.LocalDisposition is not null ? "Apply the reviewed local merge."
            : "Replace this copy with the saved candidate; retain the original, including local edits, for undo.");
        if (plan.Revision is { } revision)
        {
            var authoredVersion = plan.LocalDisposition is { } local ? local.ResultAuthoredVersion : revision.DesiredMetadata.Version;
            yield return ("Authored version", (plan.Subject.Metadata.Version ?? "unknown") + " → " + (authoredVersion ?? "unknown"));
            yield return ("Local changes", revision.LocalChanges.Length.ToString());
            foreach (var path in revision.LocalChanges.Take(6)) yield return ("Local change", path);
            if (revision.LocalChanges.Length > 6) yield return ("More local changes", (revision.LocalChanges.Length - 6).ToString());
        }
        yield return ("Shared files", "Every consumer of this physical installation is affected.");
        yield return ("Changed paths", plan.ChangedPaths.Length.ToString());
        foreach (var path in plan.ChangedPaths.Take(8)) yield return ("Changed", path);
        if (plan.ChangedPaths.Length > 8) yield return ("More changed paths", (plan.ChangedPaths.Length - 8).ToString());
        yield return ("State", plan.Status);
        if (plan.SourceReview is not null) yield return ("Source review", "Inspect frozen source changes and omissions in F2 Details before declaring review. This supplies no authenticity or safety assurance.");
        if (plan.SourceReviewWarning is not null) yield return ("Source notice", plan.SourceReviewWarning);
        yield return ("Undo", "The exact original is retained for explicit recovery/undo. Running-agent reload state is unknown.");
    }

    private static string ReviewGuidance(ReplacementResult plan, bool awaitingReview)
    {
        var guidance = awaitingReview && plan.PinPolicy is not null
            ? "Review every affected consumer, approve retention of the original, then apply the unpin-only change."
            : awaitingReview && plan.LocalDisposition is not null
                ? "Inspect the exact merged result and affected consumers. Fresh retention approval is required before applying this reviewed merge."
            : awaitingReview
            ? "Review the changes and affected consumers. Approve retention of the original before applying. " +
                "Local edits can be preserved or reviewed separately; replacement uses the saved candidate."
            : "Inspect the recorded outcome. F2 Details retains saved-plan and recovery evidence.";
        if (plan.Revision?.Source.Ref.StartsWith("refs/tags/", StringComparison.Ordinal) == true && plan.PinPolicy is null)
            guidance += " " + VersionUpdateGuidance.FutureUpdates;
        return guidance;
    }

    internal static string Result(UpdateResult result, bool dryRun, bool awaitingReview)
    {
        var heading = awaitingReview ? "Review this change" : result.Cancelled ? "Update canceled"
            : !result.Succeeded ? "Update failed or refused" : dryRun ? "Update availability" : "Update result";
        return Render(heading, ResultFields(result, awaitingReview), result.SavedPlan is { } plan ? ReviewGuidance(plan, awaitingReview)
            : dryRun ? "An availability check does not prepare or apply a saved update. Inspect notices in F2 Details."
            : "Inspect the recorded outcome. F2 Details retains complete diagnostics and any recovery evidence.");
    }

    private static IEnumerable<(string, string?)> ResultFields(UpdateResult result, bool awaitingReview)
    {
        string? latestNotice = null;
        if (result.ReconciliationPartial) yield return ("Disk verification", "Incomplete; refresh inventory and inspect details before continuing.");
        if (result.MutationAttempted && !result.Succeeded) yield return ("Recovery", "A write may have started. Inspect saved recovery evidence in F2 Details.");
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) yield return ("Reason", result.ErrorMessage);
        if (!awaitingReview)
        {
            var latest = result.Outcomes.LastOrDefault();
            yield return ("Outcome", latest?.Status ?? result.SavedPlan?.Status ?? (result.Succeeded ? "complete; inspect observations below" : "failed or refused"));
            if (latest is not null)
            {
                if (result.SavedPlan?.OriginalPath != latest.Path) yield return ("Location", latest.Path);
                latestNotice = latest.Message;
            }
        }
        if (result.SavedPlan is { } plan)
            foreach (var field in PlanFields(plan)) yield return field;
        if (latestNotice is not null) yield return ("Notice", latestNotice);
        foreach (var field in OutcomeFields(result, awaitingReview)) yield return field;
        foreach (var entry in result.Entries.Take(8))
            yield return (entry.Name, entry.Status + (entry.FromVersion is null && entry.ToVersion is null ? ""
                : " · " + (entry.FromVersion ?? "unknown") + " → " + (entry.ToVersion ?? "unknown")));
        if (result.Entries.Length > 8) yield return ("More availability entries", (result.Entries.Length - 8).ToString());
        if (result.SavedPlan is null && result.Outcomes.IsEmpty && result.Entries.IsEmpty)
            yield return ("Observation", "No structured installation outcome was returned; inspect the full output in F2 Details.");
        if (result.Notices.Length > 0 || !string.IsNullOrWhiteSpace(result.StdErr))
            yield return ("Diagnostics", "Notices are available in F2 Details.");
    }

    private static IEnumerable<(string, string?)> OutcomeFields(UpdateResult result, bool awaitingReview)
    {
        // The newest result is already above the plan. Keep the plan and its
        // recovery guidance ahead of bounded earlier batch outcomes.
        var remaining = awaitingReview ? result.Outcomes.Length : Math.Max(0, result.Outcomes.Length - 1);
        foreach (var outcome in result.Outcomes.Take(Math.Min(awaitingReview ? 8 : 7, remaining)))
        {
            yield return ("Location", outcome.Path);
            yield return ("Outcome", outcome.Status);
            if (outcome.Message is not null) yield return ("Notice", outcome.Message);
        }
        if (result.Outcomes.Length > 8) yield return ("More outcomes", (result.Outcomes.Length - 8).ToString());
    }

    internal static string Render(string heading, IEnumerable<(string, string?)> fields, string guidance, int fieldBudget = MaxCharacters - 1024)
    {
        var rendered = MaintenanceQueueFormatter.RenderFields(fields, CancellationToken.None, fieldBudget);
        var compact = string.Join("\n", rendered.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(field => "- " + field));
        return "## " + heading + "\n\n" + compact + "\n\n" + guidance
            + (rendered.OmittedFields > 0 ? $"\n\n{rendered.OmittedFields} fields omitted from this bounded summary; inspect F2 Details and saved evidence." : "");
    }
}
