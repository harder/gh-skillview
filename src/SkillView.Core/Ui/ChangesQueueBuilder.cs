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
            var title = entry.Title.Length <= 160 ? entry.Title : entry.Title[..160] + "…";
            rows.Add(new(entry.Cleanup is null ? "Review" : "Cleanup",
                TerminalEscapeSanitizer.Sanitize(MaintenanceQueueFormatter.DescribeIssue(entry) + " · " + title) ?? string.Empty)
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
