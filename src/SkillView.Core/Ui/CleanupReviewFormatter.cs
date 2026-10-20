using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Ui;

/// <summary>Display-only descriptions of captured cleanup observations.</summary>
internal static class CleanupReviewFormatter
{
    internal static string Issue(CleanupClassifier.Candidate candidate) => candidate.Kind switch
    {
        CleanupClassifier.CandidateKind.BrokenSymlink => "Broken link",
        CleanupClassifier.CandidateKind.EmptyDirectory => "Empty directory",
        CleanupClassifier.CandidateKind.Malformed => "Missing or invalid skill file",
        CleanupClassifier.CandidateKind.Duplicate or CleanupClassifier.CandidateKind.NameCollision => "Multiple copies",
        CleanupClassifier.CandidateKind.Untracked => "Source not recorded",
        CleanupClassifier.CandidateKind.MetadataConflict => "Conflicting installation records",
        CleanupClassifier.CandidateKind.BrokenSharedMapping => "Shared link needs attention",
        _ => "Installation needs review"
    };

    internal static string Effect(CleanupClassifier.Candidate candidate) => candidate.ReviewOnly
        ? "Inspection only; automatic removal is unavailable."
        : candidate.Kind == CleanupClassifier.CandidateKind.BrokenSymlink || candidate.Impact?.Operation == "unlink"
            ? "Removes only this link. Target files stay."
            : candidate.Kind == CleanupClassifier.CandidateKind.EmptyDirectory
                ? "Removes this directory only if it is still empty."
                : "Removes this installation's files. Its consumers may lose access.";

    internal static string Render(CleanupClassifier.Candidate candidate)
    {
        var fields = MaintenanceQueueFormatter.RenderFields(Fields(candidate), CancellationToken.None,
            MaintenanceQueueFormatter.MaxSummaryCharacters - 256);
        return $"## {Issue(candidate)}\n\n{fields.Text}"
            + (fields.OmittedFields > 0 ? "Some values are too long for this summary; press F2 for details.\n\n" : "")
            + "Opening review changes nothing.";
    }

    internal static void AppendConfirmation(StringBuilder text, CleanupClassifier.Candidate candidate)
    {
        text.AppendLine($"### {Issue(candidate)}");
        text.AppendLine();
        // Confirmation retains every exact selected path, even when the bounded
        // overview omits a long field. These labels never become selectors.
        Field(text, "Location", candidate.Path);
        Field(text, "Changes", Effect(candidate));
        if (candidate.ReviewOnly && candidate.Kind == CleanupClassifier.CandidateKind.EmptyDirectory)
            Field(text, "Review needed", candidate.Reason);
        foreach (var (label, value) in AgentFields(candidate)) Field(text, label, value);
    }

    private static IEnumerable<(string, string?)> Fields(CleanupClassifier.Candidate candidate)
    {
        yield return ("Why", candidate.Kind switch
        {
            CleanupClassifier.CandidateKind.BrokenSymlink => "This link's target could not be found.",
            CleanupClassifier.CandidateKind.EmptyDirectory => "This directory contains no files.",
            CleanupClassifier.CandidateKind.Malformed => "The skill file is missing or its metadata needs attention.",
            CleanupClassifier.CandidateKind.Duplicate or CleanupClassifier.CandidateKind.NameCollision => "Other copies use this name; compare their sources and versions.",
            CleanupClassifier.CandidateKind.Untracked => "Not listed by gh; another installer may own it.",
            _ => candidate.Reason
        });
        if (candidate.ReviewOnly && candidate.Kind == CleanupClassifier.CandidateKind.EmptyDirectory)
            yield return ("Review needed", candidate.Reason);
        yield return ("Next", candidate.ReviewOnly
            ? "Inspect the details and compare or repair this copy."
            : "Space selects; Enter reviews removal.");
        yield return ("Changes", Effect(candidate));
        yield return ("Undo", candidate.ReviewOnly ? "Inspection changes nothing." : "Deletion has no automatic undo.");
        foreach (var field in AgentFields(candidate)) yield return field;
        yield return ("Location", candidate.Path);
    }

    private static IEnumerable<(string, string?)> AgentFields(CleanupClassifier.Candidate candidate)
    {
        var members = candidate.Impact is { } impact
            ? impact.AffectedExposures.Select(exposure => exposure.Membership)
            : candidate.Skill?.Agents.AsEnumerable() ?? Enumerable.Empty<AgentMembership>();
        var found = false;
        foreach (var member in members.Distinct())
        {
            found = true;
            yield return ("Affected agent", Describe(member));
        }
        if (!found) yield return ("Affected agents", "No agent exposure was recorded.");
        if (candidate.Impact is { } captured)
        {
            foreach (var retained in captured.RetainedExposures)
                yield return ("Retained agent", Describe(retained.Membership));
            if (!captured.ExposureCoverageComplete)
                yield return ("Coverage", "Other consumers may exist; discovery is incomplete.");
        }
    }

    private static string Describe(AgentMembership member) =>
        $"{member.AgentId} · {member.Scope?.ToString() ?? "scope unknown"}"
        + (member.Profile is null ? "" : $" · profile {member.Profile}")
        + (member.Context is null ? "" : $" · {member.Context}");

    private static void Field(StringBuilder text, string label, string? value)
    {
        text.AppendLine($"**{label}:** {MarkdownTableFormatter.FormatCodeSpan(value ?? "unknown")}");
        text.AppendLine();
    }
}
