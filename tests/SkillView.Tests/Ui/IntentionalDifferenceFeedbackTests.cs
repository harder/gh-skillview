using System.Collections.Concurrent;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class IntentionalDifferenceFeedbackTests
{
    private static InventorySnapshot Snapshot() => InventorySnapshot.Empty with
    {
        Skills = [CapturedFindingTests.Copy("/fixtures/one/demo", "v1"), CapturedFindingTests.Copy("/fixtures/two/demo", "v2"),
            CapturedFindingTests.Copy("/fixtures/problem", "v1") with { Name = "problem", Validity = ValidityState.InvalidMetadata }],
        OwnershipRecords = [new("fixture/record", "github", null, null, null) { Provider = "gh-vercel-global", SchemaVersion = 3,
            Selector = "registry-only", LockFilePath = "/fixtures/home/.agents/.skill-lock.json", ContextRoot = "/fixtures/home",
            EntryFingerprint = new('a', 64), RecordFingerprint = new('b', 64) }]
    };

    [Theory]
    [InlineData(80, 24, false)]
    [InlineData(80, 24, true)]
    [InlineData(120, 36, false)]
    [InlineData(120, 36, true)]
    public Task FullLiteralFeedbackBelongsOnlyToItsFamilyAndClearsOnSelectionRefreshAndReturn(int width, int height, bool oversized) =>
        Task.Run(() => FullLiteralFeedbackBelongsOnlyToItsFamilyAndClearsOnSelectionRefreshAndReturnCore(width, height, oversized), TestContext.Current.CancellationToken);

    // Manual Begin/End has no running message loop. Keep async continuations off
    // Terminal.Gui's context; the outer test still awaits this owned task.
    private static async Task FullLiteralFeedbackBelongsOnlyToItsFamilyAndClearsOnSelectionRefreshAndReturnCore(int width, int height, bool oversized)
    {
        using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var work = new ConcurrentQueue<Func<Task>>(); var message = "Capacity reached: " + new string('x', oversized ? 300_000 : 25_000) + " /exact/preferences/location. Reset saved intentional differences; refreshing cannot free capacity.";
        var expected = oversized ? "Preference operation details exceed the 256 KiB display bound" : message;
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; }, _ => Task.FromResult(Snapshot()), () => { },
            runTask: (task, _) => work.Enqueue(task), onCheckUpdates: () => { }, setIntentional: (_, _) => throw new IOException(message))
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); await view.LoadAsync().ConfigureAwait(false); var session = app.Begin(window);
        try
        {
            var table = view.SubViews.OfType<TableView>().Single(); var detail = view.SubViews.OfType<Markdown>().Single();
            int Family() => view.RowsForTests.Select((row, index) => (row, index)).Single(item => item.row.Finding?.Difference is not null).index;
            void Select(int row) { table.SetSelectedRow(row); table.SetFocus(); }
            Select(Family()); app.Keyboard.RaiseKeyDownEvent(new Key('k')); await Drain(work).ConfigureAwait(false);
            Assert.Contains(expected, detail.Text.ToString()); Assert.DoesNotContain("Saving", view.StatusTextForTests);
            app.Keyboard.RaiseKeyDownEvent(new Key('C')); Assert.DoesNotContain(expected, detail.Text.ToString());
            app.Keyboard.RaiseKeyDownEvent(new Key('k')); await Drain(work).ConfigureAwait(false); Assert.Contains(expected, detail.Text.ToString());
            foreach (var index in view.RowsForTests.Select((row, index) => (row, index)).Where(item => item.row.Kind == "Problem" || item.row.Finding?.Subject is FindingRegistry).Select(item => item.index))
            { Select(index); Assert.DoesNotContain(expected, detail.Text.ToString()); }
            Select(Family()); Assert.DoesNotContain(expected, detail.Text.ToString());
            app.Keyboard.RaiseKeyDownEvent(new Key('k')); await Drain(work).ConfigureAwait(false); Assert.Contains(expected, detail.Text.ToString());
            await view.LoadAsync().ConfigureAwait(false); Assert.DoesNotContain(expected, detail.Text.ToString());
            Select(Family()); app.Keyboard.RaiseKeyDownEvent(new Key('k')); await Drain(work).ConfigureAwait(false); Assert.Contains(expected, detail.Text.ToString());
            view.CancelPendingLoad(); view.Visible = false; await view.LoadAsync().ConfigureAwait(false); Assert.DoesNotContain(expected, detail.Text.ToString());
        }
        finally { app.End(session!); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public Task SelectionCancelsInFlightSaveWithoutLeavingSavingOrLateNoticeOnOtherRows(int width, int height) =>
        Task.Run(() => SelectionCancelsInFlightSaveWithoutLeavingSavingOrLateNoticeOnOtherRowsCore(width, height), TestContext.Current.CancellationToken);

    // Manual Begin/End has no running message loop. Keep async continuations off
    // Terminal.Gui's context; the outer test still awaits this owned task.
    private static async Task SelectionCancelsInFlightSaveWithoutLeavingSavingOrLateNoticeOnOtherRowsCore(int width, int height)
    {
        using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var work = new ConcurrentQueue<Func<Task>>(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken owned = default;
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; }, _ => Task.FromResult(Snapshot()), () => { },
            runTask: (task, _) => work.Enqueue(task), setIntentional: (_, lease) => { owned = lease.Token; entered.SetResult(); return release.Task; })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); await view.LoadAsync().ConfigureAwait(false); var session = app.Begin(window);
        try
        {
            var table = view.SubViews.OfType<TableView>().Single(); var detail = view.SubViews.OfType<Markdown>().Single();
            var family = view.RowsForTests.Select((row, index) => (row, index)).Single(item => item.row.Finding?.Difference is not null).index;
            table.SetSelectedRow(family); table.SetFocus(); app.Keyboard.RaiseKeyDownEvent(new Key('k'));
            Assert.True(work.TryDequeue(out var start)); var pendingSave = start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(false); Assert.Contains("Saving", view.StatusTextForTests);
            table.SetSelectedRow(0); Assert.True(owned.IsCancellationRequested); Assert.DoesNotContain("Saving", view.StatusTextForTests);
            release.SetResult("Committed but undelivered outcome"); await pendingSave.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(false); await Drain(work).ConfigureAwait(false);
            Assert.DoesNotContain("Committed but undelivered", detail.Text.ToString()); Assert.DoesNotContain("Saving", view.StatusTextForTests);
            await view.LoadAsync().ConfigureAwait(false); Assert.DoesNotContain("Committed but undelivered", detail.Text.ToString());
        }
        finally { app.End(session!); }
    }

    private static async Task Drain(ConcurrentQueue<Func<Task>> tasks)
    {
        while (tasks.TryDequeue(out var work)) await work().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(false);
    }
}
