using SkillView.Maintenance;

namespace SkillView.Ui;

/// One finding or same-name review family. Entries always retain exact frozen
/// addresses; the first entry anchors the existing explicit copy chooser.
internal sealed record ChangesQueueRow(string Kind, string Title, IReadOnlyList<MaintenanceQueueEntry> Entries)
{
    internal MaintenanceQueueEntry Entry => Entries[0];
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
            var paths = group.Select(row => row.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var parts = paths.Select(path =>
            {
                var components = Clean(path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                return components.Length > 1 && components[^1].Equals(group.Key, StringComparison.OrdinalIgnoreCase) ? components[..^1] : components;
            }).ToArray();
            var depths = Enumerable.Repeat(1, paths.Length).ToArray();
            var suffixes = new string[paths.Length];
            bool expanded;
            do
            {
                token.ThrowIfCancellationRequested();
                for (var i = 0; i < paths.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    suffixes[i] = string.Join(Path.DirectorySeparatorChar, parts[i].TakeLast(depths[i]));
                }
                var counts = suffixes.GroupBy(value => value, StringComparer.Ordinal)
                    .ToDictionary(values => values.Key, values => values.Count(), StringComparer.Ordinal);
                expanded = false;
                for (var i = 0; i < paths.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (counts[suffixes[i]] <= 1 || depths[i] >= parts[i].Length) continue;
                    depths[i]++;
                    expanded = true;
                }
            } while (expanded);

            // Reserve the issue label too, so a later title bound cannot
            // erase the location markers that keep long paths distinct.
            var prefixWidth = group.Max(row => row.Name.Length + 3
                + (row.Entry.IsCopyReview ? 0 : MaintenanceQueueFormatter.DescribeIssue(row.Entry).Length + 3));
            var width = MaxTitleCharacters - prefixWidth;
            var bounded = suffixes.Select(value => Bound(value, width)).ToArray();
            // Sanitization, omitted skill names or very long common components can
            // erase distinctions. Number this group rather than pretending the
            // shortened labels still identify a unique location. Full paths stay
            // in the detail pane and exact frozen routing entries.
            var needsMarkers = bounded.Distinct(StringComparer.Ordinal).Count() != paths.Length;
            var pathLabels = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < paths.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var marker = needsMarkers ? $"#{i + 1} " : "";
                pathLabels[paths[i]] = marker + Bound(suffixes[i], width - marker.Length);
            }
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

    private static string Clean(string value) => (TerminalEscapeSanitizer.Sanitize(value) ?? string.Empty)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    private static string Bound(string value, int width)
    {
        if (value.Length <= width) return value;
        var left = (width - 1) / 2;
        return value[..left] + "…" + value[^(width - left - 1)..];
    }
}
