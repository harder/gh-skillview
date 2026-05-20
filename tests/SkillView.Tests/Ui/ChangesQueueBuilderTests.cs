using SkillView.Ui.Tabs;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class ChangesQueueBuilderTests
{
    [Fact]
    public void Build_OrdersUpdateBeforeCleanupBeforeDiagnostics()
    {
        var rows = ChangesQueueBuilder.Build(
            updates: ["skillA", "skillB"],
            cleanup: ["orphaned.md"],
            diagnostics: ["Doctor"]);

        Assert.Equal(4, rows.Count);
        Assert.Equal("Update",      rows[0].Kind);
        Assert.Equal("skillA",      rows[0].Title);
        Assert.Equal("Update",      rows[1].Kind);
        Assert.Equal("skillB",      rows[1].Title);
        Assert.Equal("Cleanup",     rows[2].Kind);
        Assert.Equal("orphaned.md", rows[2].Title);
        Assert.Equal("Diagnostics", rows[3].Kind);
        Assert.Equal("Doctor",      rows[3].Title);
    }

    [Fact]
    public void Build_EmptyInputs_ReturnsEmpty()
    {
        var rows = ChangesQueueBuilder.Build([], [], []);
        Assert.Empty(rows);
    }

    [Fact]
    public void Build_OnlyUpdateItems_ReturnsOnlyUpdateRows()
    {
        var rows = ChangesQueueBuilder.Build(["skill1", "skill2"], [], []);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("Update", r.Kind));
    }

    [Fact]
    public void Build_OnlyCleanupItems_ReturnsOnlyCleanupRows()
    {
        var rows = ChangesQueueBuilder.Build([], ["broken-link", "empty-dir"], []);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("Cleanup", r.Kind));
    }

    [Fact]
    public void Build_OnlyDiagnosticsItems_ReturnsOnlyDiagnosticsRows()
    {
        var rows = ChangesQueueBuilder.Build([], [], ["Run Doctor"]);

        Assert.Single(rows);
        Assert.Equal("Diagnostics", rows[0].Kind);
        Assert.Equal("Run Doctor",  rows[0].Title);
    }

    [Fact]
    public void Build_PreservesInsertionOrderWithinKind()
    {
        var rows = ChangesQueueBuilder.Build(
            updates: ["first", "second", "third"],
            cleanup: [],
            diagnostics: []);

        Assert.Equal(["first", "second", "third"], rows.Select(r => r.Title).ToArray());
    }
}
