using SkillView.Gh.Models;

namespace SkillView.Ui;

/// Static helpers for rendering update results as Markdown. The interactive
/// view is now <see cref="Tabs.UpdatesTabView"/>; the modal Show() subloop
/// was retired in Phase 5 of the winget-tui redesign. RenderResult stays on
/// this type so the UpdateScreenTests assertions don't need updating.
public static class UpdateScreen
{
    internal static string RenderResult(UpdateResult result, bool dryRun, bool allChecked, List<string> skills)
    {
        var sb = new System.Text.StringBuilder();
        var heading = dryRun ? "Dry-run results" : "Update results";
        sb.AppendLine($"## {heading}");
        sb.AppendLine();
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
                foreach (var exposure in outcome.Exposures)
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
        if (result.Outcomes.IsDefaultOrEmpty && string.IsNullOrWhiteSpace(result.StdOut)
            && string.IsNullOrWhiteSpace(result.StdErr))
            sb.AppendLine("_Completed with no output; availability was not independently established._");
        return sb.ToString();
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
