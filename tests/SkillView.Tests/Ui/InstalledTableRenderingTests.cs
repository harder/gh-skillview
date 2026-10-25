using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using SkillView.Maintenance;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InstalledTableRenderingTests
{
    [Fact]
    public async Task RefreshTemporarilyPausesCopyActionsAndRestoresActionFocusUnlessUserMovesToSearch()
    {
        var testContext = SynchronizationContext.Current;
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(80, 24);
        using var window = new Window();
        var snapshot = InstalledOverviewTests.Fixture();
        var pending = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            token => pending.Task.WaitAsync(token), (_, _, _) => Task.CompletedTask, () => { },
            availability: new(), onReviewUpdate: _ => { })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        // This fixture dispatches inline without pumping Terminal.Gui's event
        // loop. Keep xUnit's context for its bounded asynchronous assertions.
        SynchronizationContext.SetSynchronizationContext(testContext);
        try
        {
            window.Layout(); window.Draw(new DrawContext());
            view.SetSelectedRowForTests(1);
            var review = view.SubViews.OfType<Button>().Single(b => b.Text.ToString().StartsWith("Review update", StringComparison.Ordinal));
            review.SetFocus(); Assert.True(review.HasFocus);
            var refresh = view.LoadAsync();
            Assert.False(review.Enabled); Assert.False(view.FilterHasFocus);
            pending.SetResult(snapshot with { CapturedAt = DateTimeOffset.Now });
            await refresh.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(review.HasFocus); Assert.True(review.Enabled);
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            refresh = view.LoadAsync();
            view.FocusFilterForTests();
            pending.SetResult(snapshot);
            await refresh.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(view.FilterHasFocus);
        }
        finally { app.End(session!); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void AvailabilityRefreshSnapshotRefreshSortAndResizePreserveExactSelectionAndTopKey(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var skills = Enumerable.Range(0, 60).Select(index => InstalledOverviewTests.Copy("/skills/skill-" + index.ToString("D2"), "skill-" + index.ToString("D2"))).ToImmutableArray();
        var snapshot = InventorySnapshot.Empty with { Skills = skills };
        var availability = new UpdateAvailabilitySession();
        var request = availability.Begin(snapshot, 1, "context");
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(snapshot), (_, _, _) => Task.CompletedTask, () => { }, availability: availability)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            view.SetFilterTextForTests("skill"); view.FocusList();
            window.Layout(); window.Draw(new DrawContext());
            var table = view.SubViews.OfType<TableView>().Single();
            view.SetSelectedRowForTests(61); // exact skill-30, after its heading
            table.RowOffset = 50;
            window.Draw(new DrawContext());
            var topKey = view.RowsForTests[table.RowOffset].Key;
            var selectedKey = view.SelectedKeyForTests;
            table.RowOffset = 61;
            view.NewKeyDownEvent(new Key(KeyCode.CursorLeft)); Assert.Null(view.GetSelectedSkill());
            view.NewKeyDownEvent(new Key(KeyCode.CursorRight));
            Assert.Equal(selectedKey, view.RowsForTests[table.RowOffset].Key);
            table.RowOffset = 50;
            view.NewKeyDownEvent(Key.Enter);
            Assert.True(view.DetailOpenForTests);
            Assert.Equal(skills[30].ResolvedPath, view.GetSelectedSkill()!.ResolvedPath);
            var detail = view.SubViews.OfType<Markdown>().Single();
            detail.ScrollVertical(3); window.Draw(new DrawContext());
            var detailOffset = detail.Viewport.Y;
            availability.Publish(request, 1, "context", skills[30], new(UpdateAvailabilityStatus.Available, "new tree", DateTimeOffset.Now));
            view.RefreshAvailability(); window.Layout(); window.Draw(new DrawContext());
            Assert.Equal(topKey, view.RowsForTests[table.RowOffset].Key);
            Assert.Equal(selectedKey, view.SelectedKeyForTests);
            Assert.Equal(detailOffset, detail.Viewport.Y);
            Assert.Equal("skill", view.GetFilterText());
            Assert.Contains("Available", view.RowsForTests[61].Summary);
            view.LoadSeeded(snapshot with { Skills = [.. skills.Reverse()] });
            Assert.Equal(selectedKey, view.SelectedKeyForTests);
            Assert.Equal(topKey, view.RowsForTests[table.RowOffset].Key);
            Assert.True(view.DetailOpenForTests);
            foreach (var size in new[] { (80, 24), (120, 36), (80, 24) })
            {
                app.Driver.SetScreenSize(size.Item1, size.Item2); window.Layout(); window.Draw(new DrawContext());
                Assert.Equal(selectedKey, view.SelectedKeyForTests);
                Assert.Equal(topKey, view.RowsForTests[table.RowOffset].Key);
                Assert.Equal(size.Item1 >= 110, table.Visible);
                Assert.True(detail.Visible);
            }
            view.NewKeyDownEvent(Key.Esc); window.Layout(); window.Draw(new DrawContext());
            Assert.False(view.DetailOpenForTests); Assert.True(table.Visible); Assert.True(table.HasFocus);
            Assert.Equal(selectedKey, view.SelectedKeyForTests);
        }
        finally { app.End(session!); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void HierarchyBrowseReviewFilterAndKeyboardFocusDoNotChooseSiblings(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var snapshot = InstalledOverviewTests.Fixture();
        InstalledSkill? reviewed = null;
        var checks = 0; var left = 0;
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(snapshot), (_, _, _) => Task.CompletedTask, () => left++,
            availability: new(), onCheckUpdates: () => checks++, onReviewUpdate: s => reviewed = s)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            window.Layout(); window.Draw(new DrawContext()); view.FocusList();
            Assert.Null(view.GetSelectedSkill());
            Type('u'); Assert.Null(reviewed);
            var groupKey = view.SelectedKeyForTests;
            Press(KeyCode.CursorLeft); Assert.Equal(groupKey, view.SelectedKeyForTests); Assert.Equal(8, view.RowsForTests.Count);
            Press(KeyCode.CursorRight); Assert.Equal(9, view.RowsForTests.Count);
            var exact = view.RowsForTests.ToList().FindIndex(r => r.Copy?.Target?.ResolvedPath == snapshot.Skills[0].ResolvedPath);
            view.SetSelectedRowForTests(exact); var copyKey = view.SelectedKeyForTests;
            Press(KeyCode.CursorLeft); Assert.Null(view.GetSelectedSkill()); Assert.Equal(copyKey, view.SelectedKeyForTests);
            Press(KeyCode.CursorRight); Assert.Equal(snapshot.Skills[0].ResolvedPath, view.GetSelectedSkill()!.ResolvedPath);
            Press(KeyCode.Enter); Assert.Null(reviewed); Assert.True(view.DetailOpenForTests);
            Type('u'); Assert.Equal(snapshot.Skills[0].ResolvedPath, reviewed!.ResolvedPath);
            Type('C'); Assert.Equal(1, checks);
            Press(KeyCode.Esc); Assert.False(view.DetailOpenForTests); Assert.Equal(0, left);
            Type('/'); Assert.True(view.FilterHasFocus);
            Type('C'); Assert.Equal(1, checks); // uppercase text stays in search
            Press(KeyCode.Esc); view.SetFilterTextForTests("not-present");
            Assert.Null(view.GetSelectedSkill()); Assert.Contains("filtered out", view.DetailTextForTests);
            reviewed = null; Type('u'); Assert.Null(reviewed);
            view.SetFilterTextForTests(""); Assert.Equal(copyKey, view.SelectedKeyForTests);
            Assert.Equal(snapshot.Skills[0].ResolvedPath, view.GetSelectedSkill()!.ResolvedPath);
            view.LoadSeeded(snapshot with { Skills = [.. snapshot.Skills.Skip(1)] });
            Assert.Null(view.GetSelectedSkill()); reviewed = null; Type('u'); Assert.Null(reviewed);
            Press(KeyCode.Esc); Assert.Equal(1, left);
        }
        finally { app.End(session!); }

        void Type(char value) { app.Keyboard.RaiseKeyDownEvent(new Key(value)); window.Draw(new DrawContext()); }
        void Press(KeyCode code) { app.Keyboard.RaiseKeyDownEvent(new Key(code)); window.Draw(new DrawContext()); }
    }
}
