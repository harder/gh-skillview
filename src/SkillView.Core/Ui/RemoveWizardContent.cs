using System.Collections.Immutable;
using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Ui;

internal static class RemoveWizardContent
{
    public static string ActionText(RemoveTarget target) => target.Kind switch
    {
        RemoveTargetKind.AgentSymlink when target.AgentMembership is { } agent => $"Unlink {agent.AgentId}",
        RemoveTargetKind.PackageGroup => "Remove package",
        RemoveTargetKind.RepoGroup => "Remove repo",
        _ => "Remove skill",
    };

    public static string BuildReviewMarkdown(RemoveTargetEvaluation evaluation)
    {
        var sb = new StringBuilder();
        var headline = evaluation.CanExecute
            ? evaluation.RequiresSecondConfirm ? "Needs confirmation" : "Ready to remove"
            : "Blocked";

        sb.AppendLine($"## {headline}");
        sb.AppendLine();
        sb.AppendLine(BuildIntro(evaluation));
        sb.AppendLine();
        sb.AppendLine("### Selection");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Action | {MarkdownTableFormatter.FormatTableCell(evaluation.Target.Title)} |");
        sb.AppendLine($"| Summary | {MarkdownTableFormatter.FormatTableCell(evaluation.Target.Description)} |");
        if (evaluation.Target.Skills.Length == 1)
        {
            sb.AppendLine($"| Path | {MarkdownTableFormatter.FormatCodeSpan(evaluation.Target.Skills[0].ResolvedPath)} |");
        }

        if (evaluation.Target.Kind == RemoveTargetKind.AgentSymlink && evaluation.Target.AgentMembership is { } agent)
        {
            sb.AppendLine($"| Link path | {MarkdownTableFormatter.FormatCodeSpan(agent.Path)} |");
        }

        sb.AppendLine($"| Skills affected | {MarkdownTableFormatter.FormatTableCell(evaluation.Target.Skills.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))} |");

        sb.AppendLine();
        sb.AppendLine("### Skills");
        sb.AppendLine();
        sb.AppendLine("| Skill | Path |");
        sb.AppendLine("| --- | --- |");
        foreach (var skill in evaluation.Target.Skills)
        {
            sb.AppendLine($"| {MarkdownTableFormatter.FormatTableCell(skill.Name)} | {MarkdownTableFormatter.FormatCodeSpan(skill.ResolvedPath)} |");
        }

        if (!evaluation.Errors.IsDefaultOrEmpty)
        {
            sb.AppendLine();
            sb.AppendLine("### Why SkillView is blocking this");
            sb.AppendLine();
            foreach (var error in evaluation.Errors)
            {
                sb.AppendLine($"- {MarkdownTableFormatter.FormatTableCell(Describe(error))}");
            }
        }

        if (!evaluation.Warnings.IsDefaultOrEmpty)
        {
            sb.AppendLine();
            sb.AppendLine("### Warnings");
            sb.AppendLine();
            foreach (var warning in evaluation.Warnings)
            {
                sb.AppendLine($"- {MarkdownTableFormatter.FormatTableCell(Describe(warning))}");
            }
        }

        var incomingLinks = evaluation.Items
            .SelectMany(item => item.Validation.IncomingSymlinkPaths)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        if (!incomingLinks.IsDefaultOrEmpty)
        {
            sb.AppendLine();
            sb.AppendLine("### Related links");
            sb.AppendLine();
            sb.AppendLine("| Incoming symlink |");
            sb.AppendLine("| --- |");
            foreach (var incoming in incomingLinks)
            {
                sb.AppendLine($"| {MarkdownTableFormatter.FormatCodeSpan(incoming)} |");
            }
        }

        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    public static string BuildConfirmMarkdown(RemoveTargetEvaluation evaluation)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {ActionText(evaluation.Target)}");
        sb.AppendLine();

        if (evaluation.Target.Kind == RemoveTargetKind.AgentSymlink && evaluation.Target.AgentMembership is { } agent)
        {
            sb.AppendLine($"SkillView will unlink the **{MarkdownTableFormatter.FormatTableCell(agent.AgentId)}** symlink at {MarkdownTableFormatter.FormatCodeSpan(agent.Path)} and leave the canonical install in place.");
        }
        else
        {
            sb.AppendLine($"SkillView will remove **{MarkdownTableFormatter.FormatTableCell(evaluation.Target.Skills.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))}** skill install(s):");
            sb.AppendLine();
            foreach (var skill in evaluation.Target.Skills)
            {
                sb.AppendLine($"- **{MarkdownTableFormatter.FormatTableCell(skill.Name)}** — {MarkdownTableFormatter.FormatCodeSpan(skill.ResolvedPath)}");
            }
        }

        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    public static string BuildLegacySummary(InstalledSkill target, RemoveValidator.RemoveValidation validation)
    {
        var evaluation = new RemoveTargetEvaluation(
            new RemoveTarget(
                RemoveTargetKind.CurrentInstall,
                "Remove this skill",
                "Delete the selected installed skill.",
                [target]),
            [new RemoveTargetItem(target, validation)]);

        return BuildReviewMarkdown(evaluation);
    }

    private static string BuildIntro(RemoveTargetEvaluation evaluation)
    {
        if (!evaluation.CanExecute)
        {
            return "SkillView can't do this safely. Review the blocking reason below or choose a less destructive option.";
        }

        if (evaluation.Target.Kind == RemoveTargetKind.AgentSymlink)
        {
            return "This only removes the selected agent link. The shared canonical copy stays on disk.";
        }

        return evaluation.RequiresSecondConfirm
            ? "SkillView can do this, but it found related installs or repository state that deserves one more confirmation."
            : "SkillView can remove this safely.";
    }

    private static string Describe(RemoveValidator.Error error) => error.Kind switch
    {
        RemoveValidator.ErrorKind.OutsideKnownRoots =>
            "The selected install is outside SkillView's managed scan roots.",
        RemoveValidator.ErrorKind.ResolvedOutsideKnownRoots =>
            "The resolved path escapes SkillView's managed scan roots.",
        RemoveValidator.ErrorKind.AncestorSymlinkEscapesRoot =>
            "A parent symlink escapes the scan root, so SkillView won't follow it for deletion.",
        RemoveValidator.ErrorKind.NotASkillDirectory =>
            "The target no longer looks like a skill directory.",
        RemoveValidator.ErrorKind.ContainsGitDirectory =>
            "The target looks like a git clone, so SkillView won't delete it automatically.",
        RemoveValidator.ErrorKind.TargetIsScanRoot =>
            "The selected path is itself a scan root, so deleting it would remove the whole skill root.",
        _ => error.Detail,
    };

    private static string Describe(RemoveValidator.Warning warning) => warning.Kind switch
    {
        RemoveValidator.WarningKind.TrackedByParentGitRepo =>
            "The target lives inside a git working tree.",
        RemoveValidator.WarningKind.HasIncomingSymlinks =>
            "Other installs still link into this target.",
        RemoveValidator.WarningKind.TargetIsSymlinkWithOtherIncoming =>
            "This is a symlinked install, and the canonical copy still has other incoming links.",
        _ => warning.Detail,
    };
}
