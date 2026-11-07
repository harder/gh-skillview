using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InstalledRefreshReuseTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(160, 48)]
    public void OneTextChangeKeepsOneTableProjectionAndRestoresHiddenSelectionAndTopAnchor(int width, int height)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var snapshot = Snapshot();
        var captures = 0;
        using var view = Create(snapshot, captured: () => { captures++; return null; });
        window.Add(view); view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            Draw();
            var table = view.SubViews.OfType<TableView>().Single();
            var detail = view.SubViews.OfType<Markdown>().Single();
            view.SetSelectedRowForTests(41); table.RowOffset = 30; Draw();
            var selected = view.SelectedKeyForTests;
            var top = view.RowsForTests[table.RowOffset].Key;
            view.FocusFilterForTests();
            view.SetFilterTextForTests("absent");
            Assert.Empty(view.RowsForTests); Assert.Null(view.GetSelectedSkill());
            Assert.Contains("No matching installed skills", view.DetailTextForTests);
            Assert.Equal(selected, view.SelectedKeyForTests);
            object? firstTable = null;
            var changed = 0;
            detail.TextChanged += (_, _) => { firstTable ??= table.Table; changed++; };
            var before = captures;
            view.SetFilterTextForTests("item");
            Assert.Equal(before + 1, captures); // no second evidence capture in the nested status refresh
            Assert.True(changed > 0); Assert.NotNull(firstTable);
            Assert.Same(firstTable, table.Table); // a second projection replaces this public table source
            Assert.Equal(120, view.RowsForTests.Count);
            Assert.Equal(selected, view.SelectedKeyForTests);
            Assert.Equal(snapshot.Skills[20].ResolvedPath, view.GetSelectedSkill()!.ResolvedPath);
            Assert.Equal(top, view.RowsForTests[table.RowOffset].Key);
            Assert.True(view.FilterHasFocus);
            view.SetFilterTextForTests(""); Draw();
            Assert.Equal(selected, view.SelectedKeyForTests);
            Assert.Equal(top, view.RowsForTests[table.RowOffset].Key);
            Assert.Contains("60 observed copies", view.SubViews.OfType<Label>().Single(l => l.Text.StartsWith(" 60 observed", StringComparison.Ordinal)).Text);
            Assert.StartsWith("Captured:", view.CoverageForTests);
        }
        finally { app.End(session!); }
        void Draw() { window.Layout(); window.Draw(new DrawContext()); }
    }

    [Theory]
    [InlineData("availability")]
    [InlineData("selection")]
    [InlineData("scroll")]
    [InlineData("generation")]
    [InlineData("query")]
    [InlineData("evidence")]
    [InlineData("package")]
    public void ReentrantPublicationOrSelectionCannotReuseEarlierProjectionOrDetail(string change)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(160, 48);
        using var window = new Window();
        var snapshot = Snapshot();
        var availability = new UpdateAvailabilitySession();
        var request = availability.Begin(snapshot, 1, "context");
        long generation = 1;
        InstalledIssueEvidence? evidence = null;
        PackageCheckFact? package = null;
        var captures = 0;
        using var view = Create(snapshot, availability, () => { captures++; return evidence; }, () => generation, () => package);
        window.Add(view); view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            window.Layout(); window.Draw(new DrawContext());
            var table = view.SubViews.OfType<TableView>().Single();
            var detail = view.SubViews.OfType<Markdown>().Single();
            view.SetSelectedRowForTests(1);
            view.SetFilterTextForTests("absent");
            object? firstTable = null;
            var fired = false;
            detail.TextChanged += (_, _) =>
            {
                if (fired) return;
                fired = true; firstTable = table.Table;
                switch (change)
                {
                    case "availability":
                        Assert.True(availability.Publish(request, 1, "context", snapshot.Skills[0],
                            new(UpdateAvailabilityStatus.Available, "published during detail rendering")));
                        break;
                    case "selection": view.SetSelectedRowForTests(3); break;
                    case "scroll": table.RowOffset = 20; break;
                    case "generation": generation = 2; view.RefreshAvailability(); break;
                    case "query": view.SetFilterTextForTests("item-02"); break;
                    case "evidence": evidence = new(snapshot, 1, false, new Dictionary<(string, string), string[]>()); view.RefreshAvailability(); break;
                    case "package":
                        var target = snapshot.Skills[0];
                        var report = PackageCheckFactTests.Report() with
                        {
                            Findings = [new("reference", "reference-missing", "SKILL.md", 56, "references/refresh.txt", "m", "r")],
                            FindingCount = 1
                        };
                        package = new(new(snapshot, target, 1, "context", target.InstallationId), report, DateTimeOffset.UtcNow);
                        view.RefreshAvailability();
                        break;
                }
            };
            var before = captures;
            view.SetFilterTextForTests("item");
            Assert.True(fired); Assert.NotNull(firstTable);
            Assert.NotSame(firstTable, table.Table); // changed facts force the original projection path
            Assert.Equal(before + (change is "generation" or "query" or "evidence" or "package" ? 2 : 1), captures);
            if (change == "availability")
            {
                Assert.Contains("published during detail rendering", view.DetailTextForTests);
                Assert.Contains("available", view.RowsForTests[0].Summary, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(snapshot.Skills[0].ResolvedPath, view.GetSelectedSkill()!.ResolvedPath);
            }
            if (change == "selection")
                Assert.Equal(snapshot.Skills[1].ResolvedPath, view.GetSelectedSkill()!.ResolvedPath);
            if (change == "scroll")
            {
                Assert.Equal(20, table.RowOffset);
                Assert.Equal("item-010", view.RowsForTests[table.RowOffset].Group!.Name);
            }
            if (change == "generation")
            {
                Assert.Null(view.GetSelectedSkill());
                Assert.False(view.SubViews.OfType<Button>().Single(b => b.Text.StartsWith("Review update", StringComparison.Ordinal)).Enabled);
                Assert.Contains("Issues not loaded for this capture", view.DetailTextForTests);
            }
            if (change == "evidence") Assert.DoesNotContain("Issues not loaded for this capture", view.DetailTextForTests);
            if (change == "package") Assert.Contains("references/refresh.txt", view.DetailTextForTests);
            if (change == "query")
            {
                Assert.Equal("item-02", view.GetFilterText());
                Assert.Equal(10, view.VisibleSkillNamesForTests.Count);
                Assert.All(view.VisibleSkillNamesForTests, name => Assert.StartsWith("item-02", name));
                Assert.Null(view.GetSelectedSkill());
            }
        }
        finally { app.End(session!); }
    }

    [Fact]
    public void SeparateAvailabilityRefreshStillRebuildsAndPublishesFreshFacts()
    {
        var snapshot = Snapshot();
        var availability = new UpdateAvailabilitySession();
        var request = availability.Begin(snapshot, 1, "context");
        using var view = Create(snapshot, availability);
        view.LoadSeeded(snapshot); view.SetSelectedRowForTests(1);
        var table = view.SubViews.OfType<TableView>().Single();
        var before = table.Table;
        Assert.True(availability.Publish(request, 1, "context", snapshot.Skills[0], new(UpdateAvailabilityStatus.Available, "fresh explicit publication")));
        view.RefreshAvailability();
        Assert.NotSame(before, table.Table);
        Assert.Contains("fresh explicit publication", view.DetailTextForTests);
        Assert.Equal(snapshot.Skills[0].ResolvedPath, view.GetSelectedSkill()!.ResolvedPath);
    }

    [Fact]
    public void CoverageReuseNeverReenablesCopyActions()
    {
        var snapshot = Snapshot();
        using var view = Create(snapshot);
        view.LoadSeeded(snapshot); view.SetSelectedRowForTests(1);
        view.ToggleCoverage();
        view.SetFilterTextForTests("item");
        Assert.Contains("Captured coverage", view.DetailTextForTests);
        Assert.Null(view.GetSelectedSkill());
        foreach (var title in new[] { "Details", "Review update", "More actions" })
            Assert.False(view.SubViews.OfType<Button>().Single(b => b.Text.StartsWith(title, StringComparison.Ordinal)).Enabled);
    }

    private static InventorySnapshot Snapshot() => InventorySnapshot.Empty with
    {
        Skills = [.. Enumerable.Range(0, 60).Select(i => InstalledOverviewTests.Copy(Path.GetFullPath("skills/item-" + i.ToString("D3")), "item-" + i.ToString("D3")))]
    };

    private static InstalledTabView Create(InventorySnapshot snapshot, UpdateAvailabilitySession? availability = null,
        Func<InstalledIssueEvidence?>? captured = null, Func<long>? generation = null, Func<PackageCheckFact?>? package = null) => new(
            action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => throw new InvalidOperationException("Local filtering must not capture inventory"), (_, _, _) => Task.CompletedTask, () => { },
            capturedIssues: captured, catalogGeneration: generation ?? (() => 1), availability: availability, packageCheck: package)
        { Width = Dim.Fill(), Height = Dim.Fill() };
}
