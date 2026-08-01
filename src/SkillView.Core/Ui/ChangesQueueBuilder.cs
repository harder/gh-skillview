namespace SkillView.Ui;

/// Data row for the Changes workspace maintenance queue.
internal readonly record struct ChangesQueueRow(string Kind, string Title, string? Detail = null);

/// Pure builder for the Changes workspace maintenance queue.
/// Concatenates pending work in fixed priority order:
/// Update rows → Cleanup rows → Diagnostics rows.
/// Has no side effects and no dependency on Terminal.Gui.
internal static class ChangesQueueBuilder
{
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
