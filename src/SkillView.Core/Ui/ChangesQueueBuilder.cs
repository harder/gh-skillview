using static SkillView.Ui.DistinguishingPathLabels;
using SkillView.Maintenance;

namespace SkillView.Ui;

/// One finding or same-name review family. Entries always retain exact frozen
/// addresses; the first entry anchors the existing explicit copy chooser.
internal sealed record ChangesQueueRow(string Kind, string Title, IReadOnlyList<MaintenanceQueueEntry> Entries)
{
    internal MaintenanceQueueEntry Entry => Entries[0];
    internal UpdateAvailability? Availability { get; init; }
    internal PackageCheckFact? PackageCheck { get; init; }
    internal bool IsFamily => Entry.IsCopyReview;
    internal IReadOnlyList<string> LocationLabels { get; init; } = [];
}

internal sealed record ChangesQueueProjection(IReadOnlyList<ChangesQueueRow> Rows, int OmittedRows,
    string CoverageDetail, int Problems, int ReviewSuggestions)
{
    internal const string UpdatesNotChecked = "Updates not checked";

    // Queue captures do not check upstream availability or establish an update count.
    internal string Counts => $"{Problems} Problems · {UpdatesNotChecked} · {ReviewSuggestions} Review";
}

/// Pure UI policy over the captured queue. Never classifies paths or performs I/O.
internal static class ChangesQueueBuilder
{
    internal const int MaxRows = 8192;
    internal const int MaxTitleCharacters = 160;

    internal static ChangesQueueProjection Build(MaintenanceQueue queue, CancellationToken token)
    {
        var rows = ProjectRows(queue.Entries, token);
        var shown = new List<ChangesQueueRow>(Math.Min(MaxRows, rows.Count));
        var problems = 0;
        var reviews = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.Kind == "Problem") problems++; else reviews++;
            if (shown.Count < MaxRows) shown.Add(row);
        }
        var limited = new HashSet<(string Id, string Path)>();
        foreach (var entry in queue.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!entry.Subject.ExposureCoverageComplete || entry.Cleanup?.Impact?.ExposureCoverageComplete == false)
                limited.Add((entry.Subject.InstallationId, entry.Subject.Path));
        }
        var coverage = limited.Count == 0 ? queue : queue with
        {
            Diagnostics = queue.Diagnostics.Add($"Consumer discovery incomplete for {limited.Count} captured locations; other consumers may exist.")
        };
        return new(shown, rows.Count - shown.Count, MaintenanceQueueFormatter.RenderDiagnostics(coverage, token), problems, reviews);
    }

    internal static IReadOnlyList<ChangesQueueRow> ProjectRows(IEnumerable<MaintenanceQueueEntry> entries, CancellationToken token)
    {
        var captured = new List<(MaintenanceQueueEntry Entry, string Name, string Path)>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            // Routine inspection remains in Installed. Absence from gh alone
            // says nothing about damage, use, ownership or available updates.
            if (entry.Issue is "installation-review" or "Untracked") continue;
            captured.Add((entry, Bound(Clean(entry.Title), 64), entry.Cleanup?.Path ?? entry.Subject.Path));
        }
        var labels = new Dictionary<(string Name, string Path), string>();
        foreach (var group in captured.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var prefixWidth = group.Max(row => row.Name.Length + 3
                + (row.Entry.IsCopyReview ? 0 : MaintenanceQueueFormatter.DescribeIssue(row.Entry).Length + 3));
            var pathLabels = DistinguishingPathLabels.Build(group.Select(row => row.Path), group.Key, MaxTitleCharacters - prefixWidth, token);
            foreach (var row in group)
            {
                token.ThrowIfCancellationRequested();
                labels[(row.Name, row.Path)] = pathLabels[row.Path];
            }
        }

        var rows = new List<ChangesQueueRow>();
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copyGroups = captured.Where(row => row.Entry.IsCopyReview)
            .GroupBy(row => row.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Path, StringComparer.Ordinal)
                .ThenBy(row => row.Entry.Id, StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, name, path) in captured)
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsCopyReview)
            {
                if (!families.Add(entry.Title)) continue;
                var members = copyGroups[entry.Title];
                var copies = members.Select(row => (row.Entry.Subject.InstallationId, row.Path)).Distinct().Count();
                rows.Add(new("Review", $"{members[0].Name} · {copies} captured {(copies == 1 ? "copy" : "copies")}", members.Select(row => row.Entry).ToArray())
                { LocationLabels = members.Select(row => labels[(row.Name, row.Path)]).ToArray() });
                continue;
            }
            var problem = entry.Issue is "Malformed" or "MetadataConflict" or "BrokenSymlink" or "BrokenSharedMapping" or "EmptyDirectory";
            var issue = MaintenanceQueueFormatter.DescribeIssue(entry);
            rows.Add(new(problem ? "Problem" : "Review", Bound(name + " · " + issue + " · " + labels[(name, path)], MaxTitleCharacters), [entry]));
        }
        token.ThrowIfCancellationRequested();
        return rows.OrderBy(row => row.Kind == "Problem" ? 0 : 1).ToArray();
    }

    internal static string Render(ChangesQueueRow row, CancellationToken token, bool showDetails, string? homeDirectory)
    {
        if (row.PackageCheck is { } package)
            return package.Detail + "\n\n**Enter:** open the retained full report. No repair action is supplied by this check.\n";
        if (row.Availability is { } observation)
            return "## Review update\n\n" + observation.Detail + "\nCopy: "
                + MarkdownTableFormatter.FormatCodeSpan(row.Entry.Subject.Path)
                + "\n\n**Enter / u:** prepare the saved update review for this exact copy.\n"
                + (showDetails ? "\n````\n" + (TerminalEscapeSanitizer.Sanitize(observation.Diagnostic) ?? "").Replace("`", "'") + "\n````\n" : "");
        if (!row.IsFamily) return MaintenanceQueueFormatter.Render(row.Entry, token, showDetails, homeDirectory);
        var rendered = MaintenanceQueueFormatter.RenderFields(FamilyFields(row, token, showDetails), token,
            (showDetails ? MaintenanceQueueFormatter.MaxDetailCharacters : MaintenanceQueueFormatter.MaxSummaryCharacters) - 768);
        return "## Same name, multiple copies\n\n" + rendered.Text
            + "\nReview suggestion: copies may be intentional project overrides or different sources. Same name alone does not establish damage or redundant content.\n"
            + (rendered.OmittedFields > 0
                ? $"\nFamily fields omitted: {rendered.OmittedFields}. F2 shows more evidence; queue --json makes a fresh full capture.\n" : string.Empty)
            + "\n**Enter:** open the existing copy chooser. Choose an exact copy there for comparison, update/version review or supported cleanup. No survivor or removal is selected by this family.\n";
    }

    private static IEnumerable<(string, string?)> FamilyFields(ChangesQueueRow row, CancellationToken token, bool showDetails)
    {
        yield return ("Skill", row.Entry.Title);
        yield return ("Affected copies (captured)", row.Entries.Select(entry => (entry.Subject.InstallationId, entry.Cleanup?.Path ?? entry.Subject.Path)).Distinct().Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var seen = new HashSet<(string Id, string Path)>();
        for (var index = 0; index < row.Entries.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = row.Entries[index];
            var path = entry.Cleanup?.Path ?? entry.Subject.Path;
            if (!showDetails && !seen.Add((entry.Subject.InstallationId, path))) continue;
            yield return ($"Copy {index + 1}", showDetails ? path : row.LocationLabels[index]);
            if (!showDetails) continue;
            yield return ("Installation address ID", entry.Subject.InstallationId);
            yield return ("Frozen issue ID", entry.Id);
            yield return ("Issue", entry.Issue);
            yield return ("Reason", entry.Reason);
            yield return ("Exposure coverage", entry.Subject.ExposureCoverage);
        }
    }

}
