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

    internal static bool FitsConfirmationBudget(IReadOnlyList<CleanupClassifier.Candidate> selected, int budget, int maxConsumers)
    {
        long characters = 0;
        var consumers = 0;
        foreach (var candidate in selected)
        {
            characters += candidate.Path.Length + 256L;
            if (candidate.ReviewOnly && candidate.Kind == CleanupClassifier.CandidateKind.EmptyDirectory) characters += candidate.Reason.Length;
            var affected = candidate.Impact?.AffectedExposures.Select(exposure => exposure.Membership)
                ?? candidate.Skill?.Agents.AsEnumerable() ?? [];
            var retained = candidate.Impact?.RetainedExposures.Select(exposure => exposure.Membership) ?? [];
            foreach (var member in affected.Concat(retained))
            {
                if (++consumers > maxConsumers) return false;
                characters += member.AgentId.Length + (long)member.Path.Length + (member.Profile?.Length ?? 0) + (member.Context?.Length ?? 0) + 128;
                if (characters > budget) return false;
            }
            if (characters > budget) return false;
        }
        return true;
    }

    internal static IEnumerable<(string, string?)> ConfirmationFields(CleanupClassifier.Candidate candidate, bool technical)
    {
        yield return ("Issue", Issue(candidate));
        yield return ("Location", candidate.Path);
        yield return ("Changes", Effect(candidate));
        if (candidate.ReviewOnly && candidate.Kind == CleanupClassifier.CandidateKind.EmptyDirectory)
            yield return ("Review needed", candidate.Reason);
        var affected = candidate.Impact?.AffectedExposures.Select(exposure => exposure.Membership)
            ?? candidate.Skill?.Agents.AsEnumerable() ?? [];
        var retained = candidate.Impact?.RetainedExposures.Select(exposure => exposure.Membership) ?? [];
        var found = false;
        foreach (var member in affected)
        {
            found = true;
            foreach (var field in ConsumerFields("Affected", member)) yield return field;
        }
        if (!found) yield return ("Affected agents", "No agent exposure was recorded.");
        foreach (var member in retained)
            foreach (var field in ConsumerFields("Retained", member)) yield return field;
        if (candidate.Impact is { } impact)
        {
            yield return ("Coverage", impact.ExposureCoverageComplete ? "Captured consumers shown; active sessions remain unknown." : "Other consumers may exist; discovery is incomplete.");
            if (technical)
            {
                yield return ("Installation address ID", impact.Subject.InstallationId);
                yield return ("Materialization", impact.Subject.Materialization);
                yield return ("Native observation", impact.Subject.NativeObject.ObjectId);
                yield return ("Source", impact.Subject.Source.Origin);
                yield return ("Captured", impact.Subject.CapturedAt?.ToString("O"));
            }
        }
    }

    private static IEnumerable<(string, string?)> ConsumerFields(string role, AgentMembership member)
    {
        yield return (role + " agent", member.AgentId);
        yield return (role + " location", member.Path);
        yield return (role + " scope", member.Scope?.ToString());
        yield return (role + " profile", member.Profile);
        yield return (role + " context", member.Context);
    }

    private static IEnumerable<(string, string?)> Fields(CleanupClassifier.Candidate candidate)
    {
        // Preserve the actionable address ahead of potentially long captured
        // reasons and consumer lists. Full evidence remains available with F2.
        yield return ("Location", candidate.Path);
        yield return ("Why", candidate.Kind switch
        {
            CleanupClassifier.CandidateKind.BrokenSymlink => "This link's target could not be found.",
            CleanupClassifier.CandidateKind.EmptyDirectory => candidate.ReviewOnly ? candidate.Reason : "This directory contains no files.",
            CleanupClassifier.CandidateKind.Malformed => "The skill file is missing or its metadata needs attention.",
            CleanupClassifier.CandidateKind.Duplicate or CleanupClassifier.CandidateKind.NameCollision => "Other copies use this name; compare their sources and versions.",
            CleanupClassifier.CandidateKind.Untracked => "Not listed by gh; another installer may own it.",
            _ => candidate.Reason
        });
        yield return ("Next", candidate.ReviewOnly
            ? "Inspect the details and compare or repair this copy."
            : "Space selects; Enter reviews removal.");
        yield return ("Changes", Effect(candidate));
        yield return ("Undo", candidate.ReviewOnly ? "Inspection changes nothing." : "Deletion has no automatic undo.");
        foreach (var field in AgentFields(candidate)) yield return field;
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

}
