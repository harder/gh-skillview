using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InstalledFilterRenderingTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void VisibleControlsKeepListBudgetAndFilterSelectionCoverageFocusAndExactActionsAcrossRefresh(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var skills = Enumerable.Range(0, 40).Select(i => InstalledOverviewTests.Copy("/skills/skill-" + i.ToString("D2"), "skill-" + i.ToString("D2")) with
        { Agents = [new("claude", "/claude/" + i, false, Scope.Project, "/work/Alpha"), new("codex", "/codex/" + i, true, Scope.User, "/home/alex")] }).ToImmutableArray();
        var snapshot = InventorySnapshot.Empty with { Skills = skills, Diagnostics = new() { InaccessiblePaths = ["/work/Gamma"] } };
        var availability = new UpdateAvailabilitySession();
        InstalledSkill? reviewed = null;
        var loads = 0; var checks = 0;
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => { loads++; return Task.FromResult(snapshot); }, (_, _, _) => Task.CompletedTask, () => { },
            availability: availability, onReviewUpdate: s => reviewed = s, onCheckUpdates: () => checks++)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            Draw();
            var buttons = view.SubViews.OfType<Button>().ToArray();
            var facets = new[] { "Agent:", "Project/location:", "Source/owner:", "Issue:" }.Select(title => buttons.Single(b => b.Text.ToString().StartsWith(title, StringComparison.Ordinal))).ToArray();
            for (var i = 0; i < 4; i++)
            {
                Assert.True(facets[i].Visible); Assert.True(facets[i].CanFocus);
                Assert.InRange(facets[i].Frame.Right, 1, view.Frame.Width);
                if (i > 0) Assert.True(facets[i - 1].Frame.Right <= facets[i].Frame.X);
            }
            var table = view.SubViews.OfType<TableView>().Single();
            Assert.True(table.Frame.Height >= height - 8); // three header rows, two footer rows, window borders
            view.FocusFilterForTests();
            foreach (var facet in facets)
            { Assert.True(view.HandleLocalNavigation(Key.Tab)); Assert.True(facet.HasFocus); }
            var agent = view.FilterChoices(0).Single(c => c.Label == "claude");
            view.SelectFilter(0, agent.Key);
            view.SelectFilter(1, view.FilterChoices(1).Single(c => c.Label.Contains("Alpha", StringComparison.Ordinal)).Key);
            view.FocusList(); view.SetSelectedRowForTests(41); table.RowOffset = 30; Draw();
            var key = view.SelectedKeyForTests;
            var top = view.RowsForTests[table.RowOffset].Key;
            view.NewKeyDownEvent(Key.Enter); Draw();
            var detail = view.SubViews.OfType<Markdown>().Single();
            detail.ScrollVertical(2); Draw(); var offset = detail.Viewport.Y;
            view.RefreshAvailability(); Draw();
            Assert.True(detail.HasFocus); Assert.Equal(key, view.SelectedKeyForTests);
            Assert.Equal(top, view.RowsForTests[table.RowOffset].Key); Assert.Equal(offset, detail.Viewport.Y);
            view.LoadSeeded(snapshot with { Skills = [.. skills.Reverse()] }); Draw();
            Assert.Equal(key, view.SelectedKeyForTests); Assert.True(detail.HasFocus);
            view.ReviewSelectedUpdate(); Assert.Equal(skills[20].ResolvedPath, reviewed?.ResolvedPath);
            view.ToggleCoverage(); Draw();
            Assert.Contains("Captured coverage", view.DetailTextForTests);
            Assert.Contains("Could not be scanned", view.DetailTextForTests);
            Assert.Contains("Issues not loaded", view.DetailTextForTests);
            Assert.Null(view.GetSelectedSkill());
            foreach (var size in new[] { (80, 24), (120, 36) })
            { app.Driver.SetScreenSize(size.Item1, size.Item2); Draw(); Assert.Contains("Captured coverage", view.DetailTextForTests); Assert.Equal(key, view.SelectedKeyForTests); }
            view.NewKeyDownEvent(Key.Esc); Draw();
            Assert.False(view.DetailOpenForTests); Assert.Equal(key, view.SelectedKeyForTests);
            Assert.Equal(top, view.RowsForTests[table.RowOffset].Key);
            view.ReviewSelectedUpdate(); Assert.Equal(skills[20].ResolvedPath, reviewed?.ResolvedPath);
            // The selected Agent disappears; its choice is retained and cannot
            // quietly broaden to Codex or retarget a sibling operation.
            view.LoadSeeded(snapshot with { Skills = [.. skills.Select(s => s with { Agents = [s.Agents[1]] })] }); Draw();
            Assert.Empty(view.RowsForTests); Assert.Null(view.GetSelectedSkill());
            Assert.Contains("unavailable; 0 matches", view.DetailTextForTests);
            Assert.Equal(key, view.SelectedKeyForTests);
            reviewed = null; view.ReviewSelectedUpdate(); Assert.Null(reviewed);
            view.LoadSeeded(snapshot); Draw(); Assert.Equal(key, view.SelectedKeyForTests);
            Assert.Equal(skills[20].ResolvedPath, view.GetSelectedSkill()?.ResolvedPath);
            view.FocusList(); view.NewKeyDownEvent(new Key('C')); Assert.Equal(1, checks);
            view.FocusFilterForTests(); view.SendFilterKeyForTests(new Key('C')); Assert.Equal(1, checks);
            Assert.Equal(0, loads); // local filtering/drawing never calls the capture loader
        }
        finally { app.End(session!); }
        void Draw() { window.Layout(); window.Draw(new DrawContext()); }
    }

    [Fact]
    public void ScopeCycleAloneIncludesPhysicalUserAndCustomCopiesWithOnlyProjectConsumers()
    {
        var user = InstalledOverviewTests.Copy("/physical/user", "user") with
        { Agents = [new("claude", "/Beta/user", true, Scope.Project, "/work/Beta")] };
        var custom = user with { Name = "custom", ResolvedPath = "/physical/custom", Scope = Scope.Custom };
        var snapshot = InventorySnapshot.Empty with { Skills = [user, custom] };
        using var view = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException("No capture allowed"),
            (_, _, _) => Task.CompletedTask, () => { });
        view.LoadSeeded(snapshot);
        view.FocusList();
        view.NewKeyDownEvent(new Key('G')); Assert.Equal(["user"], view.VisibleSkillNamesForTests);
        view.NewKeyDownEvent(new Key('G')); Assert.Equal(["custom", "user"], view.VisibleSkillNamesForTests);
        view.NewKeyDownEvent(new Key('G')); Assert.Equal(["custom"], view.VisibleSkillNamesForTests);
        view.SelectFilter(0, Assert.Single(view.FilterChoices(0)).Key);
        Assert.Empty(view.RowsForTests); // Custom payload scope cannot fill a project-consumer match.
        view.SelectFilter(0, null); Assert.Equal(["custom"], view.VisibleSkillNamesForTests);
        view.SelectFilter(1, Assert.Single(view.FilterChoices(1)).Key); Assert.Empty(view.RowsForTests);
        view.SelectFilter(1, null); Assert.Equal(["custom"], view.VisibleSkillNamesForTests);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void EachRefreshUsesOneIssuesReferenceAndGenerationForAllRowsChoicesCountsAndStatus(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var snapshot = InventorySnapshot.Empty with
        { Skills = [.. Enumerable.Range(0, 6).Select(i => InstalledOverviewTests.Copy("/skills/" + i, "skill-" + i))] };
        var evidence = InstalledIssueEvidence.Capture(snapshot, 1, new(snapshot.CapturedAt, false, [], []));
        var changing = false; var issueReads = 0; var generationReads = 0;
        using var view = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException("No capture allowed"),
            (_, _, _) => Task.CompletedTask, () => { }, availability: new(),
            capturedIssues: () => !changing || ++issueReads == 1 ? evidence : null,
            catalogGeneration: () => !changing || ++generationReads == 1 ? 1 : 2)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); view.LoadSeeded(snapshot); view.SelectFilter(3, "none-observed");
        var session = app.Begin(window);
        try
        {
            changing = true;
            view.SetFilterTextForTests("skill-"); Draw(); // Full refresh, including nested availability rendering.

            Assert.Equal(1, issueReads); Assert.Equal(1, generationReads);
            Assert.Equal(6, view.VisibleSkillNamesForTests.Count);
            Assert.Contains("6 observed copies", view.FooterTextForTests);
            Assert.DoesNotContain("Issues not loaded", view.DetailTextForTests);
            Assert.Contains(view.FilterChoices(3), c => c.Key == "none-observed");
            view.ToggleCoverage(); Draw();
            Assert.DoesNotContain("Issues not loaded", view.DetailTextForTests);
            Assert.Equal(1, issueReads); Assert.Equal(1, generationReads);
            // The next refresh observes invalidation as one coherent unknown view.
            view.RefreshAvailability(); Draw();
            Assert.Equal(2, issueReads); Assert.Equal(2, generationReads);
            Assert.Empty(view.RowsForTests);
            Assert.Contains("0 of 6 observed copies", view.FooterTextForTests);
            Assert.DoesNotContain(view.FilterChoices(3), c => c.Key == "none-observed");
            Assert.Contains("Issues not loaded", view.DetailTextForTests);
            Assert.Contains("unavailable; 0 matches", view.DetailTextForTests);
            var issueButton = view.SubViews.OfType<Button>().Single(b => b.Text.ToString().StartsWith("Issue:", StringComparison.Ordinal));
            Assert.Contains("!", issueButton.Text.ToString());
            Assert.Equal(2, issueReads); Assert.Equal(2, generationReads);
        }
        finally { app.End(session!); }
        void Draw() { window.Layout(); window.Draw(new DrawContext()); }
    }

    [Fact]
    public async Task ClosingSelectorAfterLifetimeCancellationDoesNotRestoreFocusToItsButton()
    {
        await Task.Run(() =>
        {
            using var app = Application.Create().Init(); app.Driver!.SetScreenSize(80, 24);
            using var window = new Window();
            using var cancellation = new CancellationTokenSource();
            using var view = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException(),
                (_, _, _) => Task.CompletedTask, () => { }, lifetimeToken: cancellation.Token)
            { Width = Dim.Fill(), Height = Dim.Fill() };
            window.Add(view); view.LoadSeeded(InstalledOverviewTests.Fixture());
            var session = app.Begin(window);
            var canceledInSelector = false; var focusedAfterCancellation = false; var timedOut = false;
            try
            {
                window.Layout();
                var button = view.SubViews.OfType<Button>().Single(b => b.Text.ToString().StartsWith("Agent:", StringComparison.Ordinal));
                view.FocusList();
                button.HasFocusChanged += (_, _) => { if (cancellation.IsCancellationRequested && button.HasFocus) focusedAfterCancellation = true; };
                app.AddTimeout(TimeSpan.FromSeconds(5), () => { timedOut = true; app.RequestStop(); return false; });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (app.TopRunnableView is not Dialog) return true;
                    canceledInSelector = true; cancellation.Cancel(); app.RequestStop(); return false;
                });
                button.InvokeCommand(Command.Accept);
                Assert.True(canceledInSelector); Assert.False(timedOut); Assert.False(focusedAfterCancellation);
            }
            finally { app.End(session!); }
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task IssuesHandoffReusesExistingResultAndRejectsCatalogGenerationChanges()
    {
        var snapshot = InventorySnapshot.Empty;
        long generation = 1;
        using var issues = new ChangesTabView(a => { a(); return Task.CompletedTask; }, _ => Task.FromResult(snapshot), () => { }, catalogGeneration: () => generation);
        Assert.Null(issues.CapturedIssues);
        await issues.LoadAsync();
        var captured = issues.CapturedIssues;
        Assert.NotNull(captured); Assert.True(captured.IsCurrent(snapshot, generation));
        await issues.LoadAsync(); Assert.Same(captured, issues.CapturedIssues);
        generation++;
        Assert.False(captured.IsCurrent(snapshot, generation));
        await issues.LoadAsync(); Assert.NotSame(captured, issues.CapturedIssues);
        Assert.True(issues.CapturedIssues!.IsCurrent(snapshot, generation));
        using var installed = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException("No capture allowed"),
            (_, _, _) => Task.CompletedTask, () => { }, capturedIssues: () => issues.CapturedIssues, catalogGeneration: () => generation);
        installed.LoadSeeded(snapshot);
        Assert.Contains(installed.FilterChoices(3), c => c.Key == "none-observed");
        generation++;
        installed.LoadSeeded(snapshot); // A new view refresh captures the invalidation.
        Assert.DoesNotContain(installed.FilterChoices(3), c => c.Key == "none-observed");
    }
    [Fact]
    public async Task InvalidatedOrCanceledIssuesLoadCannotPublishFindingsAndDoesNotStrandLoading()
    {
        long generation = 1;
        var pending = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var issues = new ChangesTabView(a => { a(); return Task.CompletedTask; }, token => pending.Task.WaitAsync(token),
            () => { }, catalogGeneration: () => generation);
        var loading = issues.LoadAsync();
        generation++;
        pending.SetResult(InventorySnapshot.Empty);
        await loading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(issues.CapturedIssues);
        Assert.Contains("Inventory changed", issues.StatusTextForTests);
        Assert.All(issues.SubViews.OfType<SpinnerView>(), spinner => Assert.False(spinner.Visible));
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        loading = issues.LoadAsync();
        issues.CancelPendingLoad();
        pending.SetResult(InventorySnapshot.Empty);
        await loading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(issues.CapturedIssues);
    }

    [Fact]
    public void RefreshedChoiceLabelFollowsItsStableKeyWhenSanitizationCollisionDisappears()
    {
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo") with
        { Agents = [new("claude\u001b[31m", "/a", false), new("claude", "/b", false)] };
        using var view = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException(),
            (_, _, _) => Task.CompletedTask, () => { });
        view.LoadSeeded(InventorySnapshot.Empty with { Skills = [skill] });
        view.SelectFilter(0, InstalledFilters.Key("agent", skill.Agents[0].AgentId));
        Assert.Contains("claude #", view.DetailTextForTests);
        view.LoadSeeded(InventorySnapshot.Empty with { Skills = [skill with { Agents = [skill.Agents[0]] }] });
        Assert.Contains("Agent: `claude`", view.DetailTextForTests);
        Assert.DoesNotContain("claude #", view.DetailTextForTests);
        Assert.Equal(2, view.RowsForTests.Count);
    }

}
