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

    internal static ChangesQueueProjection Build(SkillView.Maintenance.MaintenanceQueue queue,
        CancellationToken token)
    {
        var rows = ProjectRows(queue.Entries, token);
        return new(rows, queue.Entries.Length - rows.Count, MaintenanceQueueFormatter.RenderDiagnostics(queue, token));
    }

    internal static IReadOnlyList<ChangesQueueRow> ProjectRows(
        IEnumerable<SkillView.Maintenance.MaintenanceQueueEntry> entries, CancellationToken token)
    {
        var rows = new List<ChangesQueueRow>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (rows.Count == MaxRows) break;
            // Display titles are bounded labels. Exact IDs and paths remain in
            // the frozen entry and are never shortened into routing selectors.
            var path = entry.Cleanup?.Path ?? entry.Subject.Path;
            var leaf = Path.GetFileName(path);
            // A short display hint keeps same-name copies distinguishable in a
            // narrow list. Routing always uses the full frozen entry below.
            var location = string.Equals(entry.Title, leaf, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(Path.GetDirectoryName(path)) : leaf;
            var label = entry.Title + " · " + location;
            var title = label.Length <= 160 ? label : label[..160] + "…";
            var kind = entry.Issue switch
            {
                "NameCollision" or "Duplicate" => "Copies",
                "Malformed" or "MetadataConflict" => "Metadata",
                "BrokenSymlink" or "BrokenSharedMapping" => "Link",
                "EmptyDirectory" => "Empty",
                _ => entry.Cleanup is null ? "Review" : "Inspect"
            };
            rows.Add(new(kind,
                TerminalEscapeSanitizer.Sanitize(title) ?? string.Empty)
            { Entry = entry });
        }
        token.ThrowIfCancellationRequested();
        return rows;
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
