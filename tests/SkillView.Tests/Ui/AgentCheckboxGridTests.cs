using System.Collections.Immutable;
using SkillView.Ui;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class AgentCheckboxGridTests
{
    private static InstallAgentCatalog.Entry Entry(string ghId, string label) =>
        new(ghId, label, ghId, null);

    [Fact]
    public void Build_EmptyEntries_ReturnsSingleRowLayout()
    {
        var layout = AgentCheckboxGrid.Build([], new HashSet<string>(), perRow: 4);

        Assert.Empty(layout.Boxes);
        Assert.Equal(1, layout.RowCount);
    }

    [Fact]
    public void Build_ColWidth_SizedFromLongestLabel()
    {
        ImmutableArray<InstallAgentCatalog.Entry> entries =
        [
            Entry("a", "Short"),
            Entry("b", "A Much Longer Label"),
        ];

        var layout = AgentCheckboxGrid.Build(entries, new HashSet<string>(), perRow: 2);

        Assert.Equal(("A Much Longer Label".Length + 4) * 2, layout.ContentWidth);
    }

    [Fact]
    public void Build_RowCount_WrapsAtPerRow()
    {
        ImmutableArray<InstallAgentCatalog.Entry> entries =
        [
            Entry("a", "One"),
            Entry("b", "Two"),
            Entry("c", "Three"),
            Entry("d", "Four"),
            Entry("e", "Five"),
        ];

        var layout = AgentCheckboxGrid.Build(entries, new HashSet<string>(), perRow: 2);

        Assert.Equal(3, layout.RowCount);
        Assert.Equal(5, layout.Boxes.Length);
        // Row/col positions wrap: box 2 (index 2, "Three") starts a new row.
        Assert.Equal(0, layout.Boxes[2].X);
        Assert.Equal(1, layout.Boxes[2].Y);
    }

    [Fact]
    public void Build_PreCheckedGhIds_AreChecked()
    {
        ImmutableArray<InstallAgentCatalog.Entry> entries =
        [
            Entry("claude-code", "Claude"),
            Entry("codex", "Codex"),
        ];
        var preChecked = new HashSet<string>(StringComparer.Ordinal) { "codex" };

        var layout = AgentCheckboxGrid.Build(entries, preChecked, perRow: 4);

        Assert.Equal(CheckState.UnChecked, layout.Boxes[0].Value);
        Assert.Equal(CheckState.Checked, layout.Boxes[1].Value);
    }
}
