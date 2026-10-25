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
    public void RepeatedAgentAndSkillDirectoriesUseTheShortestDistinctProjectSuffix()
    {
        var first = CopyEntry(Path.Combine("projects", "alpha", ".agents", "skills", "demo"));
        var second = CopyEntry(Path.Combine("projects", "beta", ".agents", "skills", "demo"));
        var third = CopyEntry(Path.Combine("elsewhere", "custom", "demo"));
        var rows = ChangesQueueBuilder.ProjectRows([first, second, third], TestContext.Current.CancellationToken);
        Assert.Equal("demo · " + Path.Combine("alpha", ".agents", "skills"), rows[0].Title);
        Assert.Equal("demo · " + Path.Combine("beta", ".agents", "skills"), rows[1].Title);
        Assert.Equal("demo · custom", rows[2].Title);
        Assert.Same(first, rows[0].Entry);
        Assert.Same(second, rows[1].Entry);
        Assert.Same(third, rows[2].Entry);
    }

    [Fact]
    public void CaseOnlyVisibleLocationDifferencesNeedNoMarkersOrExtraAncestors()
    {
        var first = CopyEntry(Path.Combine("projects", "Alpha", "skills", "demo"));
        var second = CopyEntry(Path.Combine("projects", "alpha", "skills", "demo"));
        var rows = ChangesQueueBuilder.ProjectRows([first, second], TestContext.Current.CancellationToken);
        Assert.Equal("demo · " + Path.Combine("Alpha", "skills"), rows[0].Title);
        Assert.Equal("demo · " + Path.Combine("alpha", "skills"), rows[1].Title);
        Assert.Same(first, rows[0].Entry);
        Assert.Same(second, rows[1].Entry);
    }

    [Fact]
    public void OmittingTheSkillLeafCannotCollapseTwoDifferentRoutingPaths()
    {
        var first = CopyEntry(Path.Combine("projects", "alpha", "demo"));
        var second = CopyEntry(Path.Combine("projects", "alpha"));
        var rows = ChangesQueueBuilder.ProjectRows([first, second], TestContext.Current.CancellationToken);
        Assert.All(rows, row => Assert.Contains(" · #", row.Title));
        Assert.NotEqual(rows[0].Title, rows[1].Title);
        Assert.Same(first, rows[0].Entry);
        Assert.Same(second, rows[1].Entry);
    }

    [Fact]
    public void LongLocationDistinctionsLostToBoundingGetStableVisibleMarkers()
    {
        var common = new string('x', 200);
        var first = CopyEntry(Path.Combine("projects", common + "alpha" + common, "skills", "demo"));
        var second = CopyEntry(Path.Combine("projects", common + "beta" + common, "skills", "demo"));
        var rows = ChangesQueueBuilder.ProjectRows([first, second, first], TestContext.Current.CancellationToken);
        Assert.StartsWith("demo · #1 ", rows[0].Title);
        Assert.StartsWith("demo · #2 ", rows[1].Title);
        Assert.NotEqual(rows[0].Title, rows[1].Title);
        Assert.Equal(rows[0].Title, rows[2].Title);
        Assert.All(rows, row => Assert.InRange(row.Title.Length, 1, ChangesQueueBuilder.MaxTitleCharacters));
        var reversed = ChangesQueueBuilder.ProjectRows([second, first], TestContext.Current.CancellationToken);
        Assert.Equal(rows[1].Title, reversed[0].Title);
        Assert.Same(first, rows[0].Entry);
        Assert.Equal(first.Subject.Path, rows[0].Entry!.Subject.Path);
    }

    [Fact]
    public void SanitizedLocationCollisionsAndLongNamesStayBoundedAndRetainExactRouting()
    {
        var name = new string('n', 300) + "\n\u001b[31m";
        var first = CopyEntry(Path.Combine("projects", "\u001b[31malpha", "demo")) with { Title = name };
        var second = CopyEntry(Path.Combine("projects", "alpha", "demo")) with { Title = name };
        var rows = ChangesQueueBuilder.ProjectRows([first, second], TestContext.Current.CancellationToken);
        Assert.NotEqual(rows[0].Title, rows[1].Title);
        Assert.All(rows, row =>
        {
            Assert.InRange(row.Title.Length, 1, ChangesQueueBuilder.MaxTitleCharacters);
            Assert.DoesNotContain('\u001b', row.Title);
            Assert.DoesNotContain('\n', row.Title);
            Assert.Contains(" · #", row.Title);
        });
        Assert.Same(first, rows[0].Entry);
        Assert.Same(second, rows[1].Entry);
    }

    [Fact]
    public void CancellationDuringEntryCaptureDoesNotPublishRows()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<SkillView.Maintenance.MaintenanceQueueEntry> Entries()
        {
            yield return CopyEntry("one");
            cancellation.Cancel();
            yield return CopyEntry("two");
        }
        Assert.Throws<OperationCanceledException>(() => ChangesQueueBuilder.ProjectRows(Entries(), cancellation.Token));
    }

    private static SkillView.Maintenance.MaintenanceQueueEntry CopyEntry(string path)
    {
        var subject = SkillView.Inventory.Models.OperationSubject.From(CopiesPresentationTests.Copy(Path.GetFullPath(path)));
        return new("copy:" + subject.InstallationId, "NameCollision", "demo", subject, [subject.Path],
            "review-required", "same name", "review-cleanup", false, false);
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
