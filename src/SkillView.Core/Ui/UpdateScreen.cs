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
            sb.AppendLine($"### ⛔ Failed");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                AppendFencedBlock(sb, result.ErrorMessage);
            }
            return sb.ToString();
        }
        if (dryRun && result.Entries.Length > 0)
        {
            sb.AppendLine("| Skill | Status | Change |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var entry in result.Entries)
            {
                var change = string.IsNullOrWhiteSpace(entry.FromVersion) && string.IsNullOrWhiteSpace(entry.ToVersion)
                    ? "—"
                    : MarkdownTableFormatter.FormatCodeSpan($"{entry.FromVersion ?? "?"} -> {entry.ToVersion ?? "?"}");
                sb.AppendLine(
                    $"| {MarkdownTableFormatter.FormatCodeSpan(entry.Name)} | " +
                    $"{MarkdownTableFormatter.FormatTableCell(entry.Status)} | " +
                    $"{change} |");
            }
            if (!string.IsNullOrWhiteSpace(result.StdOut))
            {
                sb.AppendLine();
                sb.AppendLine("### Raw output");
                sb.AppendLine();
                AppendFencedBlock(sb, result.StdOut);
            }
            return sb.ToString();
        }
        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            sb.AppendLine(dryRun
                ? "_No updates available for the selected skill(s)._"
                : "_Completed with no output._");
            return sb.ToString();
        }
        AppendFencedBlock(sb, result.StdOut);
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
