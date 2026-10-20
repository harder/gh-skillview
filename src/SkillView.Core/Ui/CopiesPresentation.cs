using System.Text;
using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal sealed record CopiesGroup(InstalledSkill[] Copies, int Omitted, int Ambiguous, bool Partial)
{
    internal ImmutableDictionary<string, CleanupClassifier.Candidate> CleanupCandidates { get; init; }
        = ImmutableDictionary<string, CleanupClassifier.Candidate>.Empty;
}

internal static class CopiesPresentation
{
    internal const int MaxCopies = 128;

    internal static CopiesGroup Build(InventorySnapshot snapshot, InstalledSkill selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = selected.SourceIdentity.Key;
        var selectedId = selected.InstallationId;
        var related = new List<(InstalledSkill Skill, string Id)>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var skill in snapshot.Skills)
        {
            token.ThrowIfCancellationRequested();
            var id = skill.InstallationId;
            counts[id] = counts.GetValueOrDefault(id) + 1;
            if (skill.Name.Equals(selected.Name, StringComparison.OrdinalIgnoreCase)
                || source is not null && skill.SourceIdentity.Key == source) related.Add((skill, id));
        }
        var groups = related.GroupBy(row => row.Id).ToArray();
        var ambiguous = groups.Count(group => counts[group.Key] != 1);
        var partial = snapshot.IsPartial || !snapshot.ConfiguredRootsComplete;
        // Never replace a missing/ambiguous reference with a same-name sibling.
        var selectedCount = counts.GetValueOrDefault(selectedId);
        if (selectedCount != 1 || !groups.Any(group => group.Key == selectedId))
            return new([], 0, selectedCount > 1 ? Math.Max(1, ambiguous) : ambiguous, partial);
        var copies = groups.Where(group => counts[group.Key] == 1).Select(group => group.Single())
            .OrderByDescending(row => row.Id == selectedId)
            .ThenBy(row => row.Skill.ResolvedPath, StringComparer.Ordinal).Select(row => row.Skill).ToArray();
        return new(copies.Take(MaxCopies).ToArray(), Math.Max(0, copies.Length - MaxCopies),
            ambiguous, partial);
    }

    internal static string Consumers(InstalledSkill skill) => skill.Agents.IsEmpty ? "none recorded"
        : string.Join(", ", skill.Agents.Select(agent => agent.AgentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    internal static string? UpdateUnavailableReason(InstalledSkill skill) =>
        skill.AllOwnershipClaims.Any(owner => owner.InstallationMode == "managed-read-only")
            ? "Managed copy: use its owning tool to update it." : null;

    internal static string? CleanupUnavailableReason(CopiesGroup group, InstalledSkill skill) =>
        group.CleanupCandidates.ContainsKey(skill.InstallationId) ? null : "No unambiguous matching cleanup issue at this location.";

    internal static string Render(CopiesGroup group, InstalledSkill reference, InstalledSkill selected,
        SkillComparisonResult? comparison = null)
    {
        var text = new StringBuilder();
        var omitted = 0;
        void Line(string label, string? value)
        {
            var clean = (TerminalEscapeSanitizer.Sanitize(value ?? "unknown") ?? "unknown")
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
            // Display is bounded; actions retain the original captured objects.
            if (clean.Length > 2048 || text.Length + clean.Length + label.Length > 16 * 1024) { omitted++; return; }
            text.Append(label).Append(": ").AppendLine(clean);
        }
        if (comparison is not null)
        {
            Line("Source relationship", Source(comparison.SourceRelationship));
            Line("Revision", Revision(comparison.RevisionRelationship));
            Line("Files", comparison.PayloadRelationship switch
            {
                "byte-identical" => "Same captured file bytes; permissions/metadata can still differ.",
                "installer-metadata-only" => "Payload files match; installation metadata differs.",
                "different-payloads" => "Different payload files.",
                _ => "Comparison incomplete; differences remain unknown."
            });
            Line("Differing files", comparison.DifferingFileCount.ToString());
            foreach (var path in comparison.DifferingFiles.Take(12)) Line("Changed", path);
            if (comparison.DifferingFileCount > 12) Line("More changes", "Only the first 12 paths are shown.");
            foreach (var diagnostic in comparison.Diagnostics.Take(8)) Line("Notice", diagnostic);
            if (comparison.Diagnostics.Length > 8) Line("More notices", (comparison.Diagnostics.Length - 8).ToString());
            text.AppendLine();
        }
        else if (selected.InstallationId != reference.InstallationId)
        {
            Line("Source relationship", Source(SkillComparisonService.SourceRelationship(reference, selected)));
            Line("Revision", Revision(SkillComparisonService.RevisionRelationship(reference, selected)));
            text.AppendLine("Select Compare to inspect local files. No preferred version is inferred.");
        }
        Line("Selected copy", selected.Name);
        Line("Location", selected.ResolvedPath);
        Line("Scope", selected.Scope.ToString());
        Line("Recorded consumers", Consumers(selected));
        if (selected.Agents.Length > 1) text.AppendLine("These recorded consumers share this installation's files.");
        Line("Recorded ref", selected.Revision.RepositoryRef);
        Line("Authored version", selected.FrontMatter.Version);
        Line("Pin", selected.Revision.PinnedRef ?? (selected.Pinned ? "pinned; ref unknown" : "no pin recorded"));
        Line("Recorded source", selected.SourceIdentity.Origin);
        Line("Skill subpath", selected.SourceIdentity.Subpath is "" ? "repository root" : selected.SourceIdentity.Subpath);
        Line("Reference location", reference.ResolvedPath);
        if (UpdateUnavailableReason(selected) is { } updateReason) Line("Update unavailable", updateReason);
        if (CleanupUnavailableReason(group, selected) is { } cleanupReason) Line("Cleanup unavailable", cleanupReason);
        if (group.Partial) text.AppendLine("Partial discovery: other installations or consumers may exist.");
        if (group.Omitted > 0) Line("Additional copies not shown", group.Omitted.ToString());
        if (group.Ambiguous > 0) Line("Ambiguous installation addresses excluded", group.Ambiguous.ToString());
        if (omitted > 0) text.AppendLine($"{omitted} long fields omitted from this bounded view; use CLI inspect/compare for full evidence.");
        return text.ToString();
    }

    private static string Source(string value) => value switch
    {
        "same-source" => "Same recorded source and skill subpath.",
        "different-origins" => "Different sources; matching names do not make these interchangeable.",
        "different-source-subpaths" => "Different skills/subpaths within the source.",
        _ => "Source relationship unknown or conflicting; matching names prove no common origin."
    };

    private static string Revision(string value) => value switch
    {
        "same-recorded-revision" => "Same recorded revision; local files have not necessarily been compared.",
        "different-recorded-revisions" => "Different recorded revisions; neither is assumed newer or preferred.",
        "different-pins" => "Different pin policies; review before changing either copy.",
        _ => "Recorded versions cannot be reliably compared."
    };
}
