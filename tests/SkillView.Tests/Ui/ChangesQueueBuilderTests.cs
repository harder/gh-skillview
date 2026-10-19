using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class ChangesQueueBuilderTests
{
    [Fact]
    public void Build_OrdersUpdateBeforeCleanupBeforeDiagnostics()
    {
        var rows = ChangesQueueBuilder.BuildForTests(
            updates: ["skillA", "skillB"],
            cleanup: ["orphaned.md"],
            diagnostics: ["Doctor"]);

        Assert.Equal(4, rows.Count);
        Assert.Equal("Update", rows[0].Kind);
        Assert.Equal("skillA", rows[0].Title);
        Assert.Equal("Update", rows[1].Kind);
        Assert.Equal("skillB", rows[1].Title);
        Assert.Equal("Cleanup", rows[2].Kind);
        Assert.Equal("orphaned.md", rows[2].Title);
        Assert.Equal("Diagnostics", rows[3].Kind);
        Assert.Equal("Doctor", rows[3].Title);
    }

    [Fact]
    public void Build_EmptyInputs_ReturnsEmpty()
    {
        var rows = ChangesQueueBuilder.BuildForTests([], [], []);
        Assert.Empty(rows);
    }

    [Fact]
    public void SameNameCopies_ShowSkillNameFirstAndDistinctLocations()
    {
        var queue = SkillView.Maintenance.MaintenanceQueueBuilder.Build(
            SkillView.Inventory.Models.InventorySnapshot.Empty with
            {
                Skills = [MaintenanceQueueTests.Skill("/skills/one", "https://github.com/one/repo"),
                    MaintenanceQueueTests.Skill("/skills/two", "https://github.com/two/repo")]
            }, TestContext.Current.CancellationToken);
        var rows = ChangesQueueBuilder.ProjectRows(queue.Entries.Where(entry => entry.Issue == "NameCollision"),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("Copies", row.Kind);
            Assert.StartsWith("same-name · ", row.Title);
        });
        Assert.NotEqual(rows[0].Title, rows[1].Title);
        Assert.EndsWith(" · one", rows[0].Title);
        Assert.EndsWith(" · two", rows[1].Title);
    }

    [Fact]
    public void Build_OnlyUpdateItems_ReturnsOnlyUpdateRows()
    {
        var rows = ChangesQueueBuilder.BuildForTests(["skill1", "skill2"], [], []);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("Update", r.Kind));
    }

    [Fact]
    public void Build_OnlyCleanupItems_ReturnsOnlyCleanupRows()
    {
        var rows = ChangesQueueBuilder.BuildForTests([], ["broken-link", "empty-dir"], []);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("Cleanup", r.Kind));
    }

    [Fact]
    public void Build_OnlyDiagnosticsItems_ReturnsOnlyDiagnosticsRows()
    {
        var rows = ChangesQueueBuilder.BuildForTests([], [], ["Run Doctor"]);

        Assert.Single(rows);
        Assert.Equal("Diagnostics", rows[0].Kind);
        Assert.Equal("Run Doctor", rows[0].Title);
    }

    [Fact]
    public void Build_PreservesInsertionOrderWithinKind()
    {
        var rows = ChangesQueueBuilder.BuildForTests(
            updates: ["first", "second", "third"],
            cleanup: [],
            diagnostics: []);

        Assert.Equal(["first", "second", "third"], rows.Select(r => r.Title).ToArray());
    }
}
