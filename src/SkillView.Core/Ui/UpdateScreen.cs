using SkillView.Gh.Models;

namespace SkillView.Ui;

/// Static helpers for rendering update results as Markdown. The interactive
/// view is now <see cref="Tabs.UpdatesTabView"/>; the modal Show() subloop
/// was retired in Phase 5 of the winget-tui redesign. RenderResult stays on
/// this type so the UpdateScreenTests assertions don't need updating.
public static class UpdateScreen
{
    internal static string RenderResult(UpdateResult result, bool dryRun, bool allChecked, List<string> skills, bool unpinOnly = false)
    {
        var sb = new System.Text.StringBuilder();
        var heading = dryRun ? "Dry-run results" : unpinOnly || result.SavedPlan?.PinPolicy is not null ? "Unpin-only results" : "Update results";
        sb.AppendLine($"## {heading}");
        sb.AppendLine();
        if (result.SavedPlan is { } plan) sb.AppendLine(RenderPlan(plan));
        var scope = allChecked
            ? "_all skills_"
            : skills.Count == 1
                ? MarkdownTableFormatter.FormatCodeSpan(skills[0])
                : $"{skills.Count} skills";
        sb.AppendLine($"**Scope:** {scope}  ·  **Exit:** {result.ExitCode}  ·  **Entries:** {result.Entries.Length}");
        sb.AppendLine();
        if (!result.Succeeded)
        {
            sb.AppendLine("### ⛔ Failed or refused");
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) AppendFencedBlock(sb, result.ErrorMessage);
        }
        if (!result.Outcomes.IsDefaultOrEmpty)
        {
            sb.AppendLine("| Installation | Outcome | Evidence |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var outcome in result.Outcomes)
            {
                sb.AppendLine($"| {MarkdownTableFormatter.FormatCodeSpan(outcome.Path)} | " +
                    $"{MarkdownTableFormatter.FormatTableCell(outcome.Status)} | " +
                    $"{MarkdownTableFormatter.FormatTableCell(outcome.Message ?? outcome.ChangedFileCount + " changed file(s)")} |");
                if (outcome.Impact is { } impact) OperationImpactFormatter.Append(sb, impact);
                else foreach (var exposure in outcome.Exposures)
                    sb.AppendLine($"Affected exposure: {MarkdownTableFormatter.FormatCodeSpan(exposure)}  ");
            }
            sb.AppendLine();
        }
        if (result.Entries.Length > 0)
        {
            sb.AppendLine("| Skill | Status | Change |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var entry in result.Entries)
            {
                var change = string.IsNullOrWhiteSpace(entry.FromVersion) && string.IsNullOrWhiteSpace(entry.ToVersion)
                    ? "—" : MarkdownTableFormatter.FormatCodeSpan($"{entry.FromVersion ?? "?"} -> {entry.ToVersion ?? "?"}");
                sb.AppendLine($"| {MarkdownTableFormatter.FormatCodeSpan(entry.Name)} | " +
                    $"{MarkdownTableFormatter.FormatTableCell(entry.Status)} | {change} |");
            }
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            sb.AppendLine("### Standard output");
            AppendFencedBlock(sb, result.StdOut);
        }
        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            sb.AppendLine("### Notices and errors");
            AppendFencedBlock(sb, result.StdErr);
        }
        foreach (var notice in result.Notices)
        {
            sb.AppendLine($"### {MarkdownTableFormatter.FormatTableCell(notice.Stream)}");
            AppendFencedBlock(sb, notice.Text);
        }
        if (result.Outcomes.IsDefaultOrEmpty && string.IsNullOrWhiteSpace(result.StdOut)
            && string.IsNullOrWhiteSpace(result.StdErr))
            sb.AppendLine("_Completed with no output; availability was not independently established._");
        return sb.ToString();
    }

    internal static string RenderPlan(SkillView.Maintenance.ReplacementResult plan)
    {
        var sb = new System.Text.StringBuilder();
        void Field(string label, string? value) => sb.AppendLine($"**{label}:** {MarkdownTableFormatter.FormatCodeSpan(value ?? "unknown")}  ");
        sb.AppendLine(plan.PinPolicy is not null ? "### Saved unpin-only review" : "### Saved update review");
        Field("State", plan.Status);
        Field("Installation", plan.OriginalPath);
        Field("Installation ID", plan.Subject.InstallationId);
        Field("Plan ID", plan.Id);
        Field("Plan fingerprint", plan.PlanFingerprint);
        Field("Manifest", plan.PlanPath);
        Field("Retained original", plan.RetainedPath);
        Field("Candidate", plan.CandidatePath);
        Field("Disposition", plan.PinPolicy is not null ? "unpin-only; retain original" : plan.LocalDisposition?.Kind ?? "replace; retain original");
        if (plan.PinPolicy is { } pinPolicy)
        {
            Field("Pin", $"{plan.Subject.Metadata.PinnedRef ?? "verified owner pin"} -> explicitly unpinned");
            Field("Recorded source ref", plan.Subject.Metadata.GithubRef);
            Field("Recorded source tree", plan.Subject.Metadata.GithubTreeSha);
            Field("Native metadata policy", pinPolicy.MetadataPolicy);
            if (pinPolicy.Owner is { } owner)
            {
                Field("Registry", owner.RegistryPath);
                Field("Selected owner entry", owner.Selector);
                Field("Owner fingerprints", owner.BeforeFingerprint + " -> " + owner.DesiredFingerprint);
            }
            sb.AppendLine("Only verified pin tracking changes. Installed instructions, extras and source revision remain as captured; every consumer of this physical installation is affected.");
            sb.AppendLine("Apply swaps in a verified directory with new active object IDs and timestamps. Supported native attributes are preserved; the exact original native directory is retained for undo. This does not detach an individual consumer.");
        }
        if (plan.LocalDisposition is { } local)
        {
            Field("Source baseline candidate", local.SourceCandidate.Identity.CanonicalPath);
            Field("Source baseline payload", local.SourceCandidate.Payload);
            Field("Local choice fingerprint", local.SelectionFingerprint);
            Field("Native metadata policy", local.MetadataPolicy);
        }
        Field("Root mode", $"{plan.OriginalRootMode} -> {plan.CandidateRootMode}");
        if (plan.Revision is { } revision)
        {
            Field("Repository", revision.Source.Repository);
            Field("Resolved ref", revision.Source.Ref);
            Field("Commit", revision.Source.Commit);
            Field("Git tree", revision.Source.Tree);
            var resultVersion = plan.LocalDisposition is { } disposition ? disposition.ResultAuthoredVersion : revision.DesiredMetadata.Version;
            Field("Authored version", $"{plan.Subject.Metadata.Version ?? "unspecified"} -> {resultVersion ?? "unspecified"}");
            if (plan.LocalDisposition is not null) Field("Source authored version", revision.DesiredMetadata.Version ?? "unspecified");
            Field("Pin", plan.Subject.Metadata.Pinned || plan.Subject.Metadata.PinnedRef is not null
                ? $"{plan.Subject.Metadata.PinnedRef ?? plan.Subject.Metadata.GithubRef} -> explicitly unpinned" : "unpinned -> unpinned");
            Field("Local changes", revision.LocalChanges.Length.ToString());
            foreach (var path in revision.LocalChanges) Field("Local", path);
            if (revision.Owner is { } owner)
            {
                Field("Registry", owner.RegistryPath);
                Field("Selected owner entry", owner.Selector);
                Field("Owner fingerprints", owner.BeforeFingerprint + " -> " + owner.DesiredFingerprint);
            }
        }
        if (plan.Impact is { } impact) OperationImpactFormatter.Append(sb, impact);
        else foreach (var agent in plan.Subject.Agents) Field("Affected consumer", $"{agent.AgentId}: {agent.Path}");
        foreach (var path in plan.ChangedPaths) Field("Changed path", path);
        sb.AppendLine("The original directory, including local edits and extras, is retained for explicit recovery/undo. Agent session state is unknown.");
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? "";
    }

    private static void AppendFencedBlock(System.Text.StringBuilder sb, string text)
    {
        var sanitized = TerminalEscapeSanitizer.Sanitize(text)?.TrimEnd() ?? string.Empty;
        var fenceLength = 3;
        var currentRun = 0;
        foreach (var ch in sanitized)
        {
            if (ch == '`')
            {
                currentRun++;
                if (currentRun >= fenceLength)
                {
                    fenceLength = currentRun + 1;
                }
            }
            else
            {
                currentRun = 0;
            }
        }

        var fence = new string('`', fenceLength);
        sb.AppendLine(fence);
        sb.AppendLine(sanitized);
        sb.AppendLine(fence);
    }
}
