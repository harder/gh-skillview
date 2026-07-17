using System.Collections.Immutable;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Lays out `InstallAgentCatalog.Entries` as a wrapping grid of `CheckBox`
/// rows, relative to (0,0). Callers add the boxes to their own scrollable
/// container and position that container — this only computes the grid.
internal static class AgentCheckboxGrid
{
    internal readonly record struct Layout(CheckBox[] Boxes, int ContentWidth, int RowCount);

    internal static Layout Build(
        ImmutableArray<InstallAgentCatalog.Entry> entries,
        IReadOnlySet<string> preChecked,
        int perRow)
    {
        if (entries.Length == 0)
        {
            return new Layout([], 1, 1);
        }

        var colWidth = entries.Max(e => e.Label.Length) + 4;
        var boxes = new CheckBox[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            boxes[i] = new CheckBox
            {
                X = (i % perRow) * colWidth,
                Y = i / perRow,
                Text = entry.Label,
                Value = preChecked.Contains(entry.GhId) ? CheckState.Checked : CheckState.UnChecked,
            };
        }

        var rowCount = (int)Math.Ceiling(entries.Length / (double)perRow);
        return new Layout(boxes, colWidth * perRow, rowCount);
    }
}
