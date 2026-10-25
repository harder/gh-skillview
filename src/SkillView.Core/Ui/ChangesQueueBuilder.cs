namespace SkillView.Ui;

/// Data row for the Changes workspace maintenance queue.
internal readonly record struct ChangesQueueRow(string Kind, string Title, string? Detail = null)
{
    internal SkillView.Maintenance.MaintenanceQueueEntry? Entry { get; init; }
}

internal sealed record ChangesQueueProjection(IReadOnlyList<ChangesQueueRow> Rows, int OmittedRows,
    string CoverageDetail);

/// Pure presentation over the shared maintenance queue projection.
/// Has no side effects and no dependency on Terminal.Gui.
internal static class ChangesQueueBuilder
{
    internal const int MaxRows = 8192;
    internal const int MaxTitleCharacters = 160;

    internal static ChangesQueueProjection Build(SkillView.Maintenance.MaintenanceQueue queue,
        CancellationToken token)
    {
        var rows = ProjectRows(queue.Entries, token);
        return new(rows, queue.Entries.Length - rows.Count, MaintenanceQueueFormatter.RenderDiagnostics(queue, token));
    }

    internal static IReadOnlyList<ChangesQueueRow> ProjectRows(
        IEnumerable<SkillView.Maintenance.MaintenanceQueueEntry> entries, CancellationToken token)
    {
        var captured = new List<(SkillView.Maintenance.MaintenanceQueueEntry Entry, string Name, string Path)>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (captured.Count == MaxRows) break;
            captured.Add((entry, Bound(Clean(entry.Title), 64), entry.Cleanup?.Path ?? entry.Subject.Path));
        }

        var labels = new Dictionary<(string Name, string Path), string>();
        foreach (var group in captured.GroupBy(row => row.Name, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var paths = group.Select(row => row.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var parts = paths.Select(path =>
            {
                var components = Clean(path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                return components.Length > 1 && components[^1].Equals(group.Key, StringComparison.Ordinal) ? components[..^1] : components;
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

            var width = MaxTitleCharacters - group.Key.Length - 3;
            var bounded = suffixes.Select(value => Bound(value, width)).ToArray();
            // Sanitization, omitted skill names or very long common components can
            // erase distinctions. Number this group rather than pretending the
            // shortened labels still identify a unique location. Full paths stay
            // in the detail pane and exact frozen routing entries.
            var needsMarkers = bounded.Distinct(StringComparer.Ordinal).Count() != paths.Length;
            for (var i = 0; i < paths.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var marker = needsMarkers ? $"#{i + 1} " : "";
                labels[(group.Key, paths[i])] = marker + Bound(suffixes[i], width - marker.Length);
            }
        }

        var rows = new List<ChangesQueueRow>(captured.Count);
        foreach (var (entry, name, path) in captured)
        {
            token.ThrowIfCancellationRequested();
            var kind = entry.Issue switch
            {
                "NameCollision" or "Duplicate" => "Copies",
                "Malformed" or "MetadataConflict" => "Metadata",
                "BrokenSymlink" or "BrokenSharedMapping" => "Link",
                "EmptyDirectory" => "Empty",
                _ => entry.Cleanup is null ? "Review" : "Inspect"
            };
            rows.Add(new(kind, name + " · " + labels[(name, path)])
            { Entry = entry });
        }
        token.ThrowIfCancellationRequested();
        return rows;
    }

    private static string Clean(string value) => (TerminalEscapeSanitizer.Sanitize(value) ?? string.Empty)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    private static string Bound(string value, int width)
    {
        if (value.Length <= width) return value;
        var left = (width - 1) / 2;
        return value[..left] + "…" + value[^(width - left - 1)..];
    }

    internal static IReadOnlyList<ChangesQueueRow> BuildForTests(
        IEnumerable<string> updates,
        IEnumerable<string> cleanup,
        IEnumerable<string> diagnostics)
    {
        var rows = new List<ChangesQueueRow>();
        foreach (var u in updates) rows.Add(new ChangesQueueRow("Update", u));
        foreach (var c in cleanup) rows.Add(new ChangesQueueRow("Cleanup", c));
        foreach (var d in diagnostics) rows.Add(new ChangesQueueRow("Diagnostics", d));
        return rows;
    }
}
