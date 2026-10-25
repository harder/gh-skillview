using SkillView.Inventory.Models;
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
public sealed class SelectedCleanupHandoffKeyboardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingChangesDuringCaptureOrQueuedDispatch_PreventsStaleReview(bool queueBeforeLeaving)
    {
        var captured = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captureToken = default;
        using var fixture = new Fixture(token => { captureToken = token; return captured.Task; });
        await fixture.StartAsync();
        fixture.Press(Key.Enter);
        if (queueBeforeLeaving)
        {
            captured.SetResult(fixture.Snapshot);
            await fixture.DispatchQueued.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        fixture.Press(Key.Esc); // Actual focus/navigation path cancels the Changes activation.
        Assert.False(fixture.View.Visible);
        Assert.True(captureToken.IsCancellationRequested);
        if (!queueBeforeLeaving) captured.SetResult(fixture.Snapshot);
        else fixture.CompleteDispatch();
        await Task.WhenAll(fixture.Work).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Reviews);
    }

    [Fact]
    public async Task RepeatedEnter_SupersedesPendingCaptureAndOwnsStartedDispatchUntilCompletion()
    {
        var captures = new List<TaskCompletionSource<InventorySnapshot>>();
        var tokens = new List<CancellationToken>();
        using var fixture = new Fixture(token =>
        {
            tokens.Add(token);
            var completion = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            captures.Add(completion);
            return completion.Task;
        });
        await fixture.StartAsync();
        fixture.Press(Key.Enter);
        fixture.Press(Key.Enter);
        Assert.Equal(2, captures.Count);
        Assert.True(tokens[0].IsCancellationRequested);
        captures[0].SetResult(fixture.Snapshot);
        captures[1].SetResult(fixture.Snapshot);
        await fixture.DispatchQueued.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.DuringReview = () => fixture.Press(Key.Enter);
        fixture.RunQueuedCallback();
        Assert.Equal(1, fixture.Reviews);
        Assert.Equal(2, captures.Count); // Enter during a started modal cannot admit another capture.
        fixture.Press(Key.Enter); // Callback returned, but owned dispatch has not completed.
        Assert.Equal(2, captures.Count);
        Assert.Contains(fixture.Work, task => !task.IsCompleted);
        fixture.FinishDispatch();
        await Task.WhenAll(fixture.Work).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Reviews);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IApplication _app = Application.Create().Init("ansi");
        private readonly Window _window = new() { Width = Dim.Fill(), Height = Dim.Fill() };
        private readonly Label _destination = new() { Text = "Other workspace", Visible = false, CanFocus = true };
        private readonly TaskCompletionSource<bool> _dispatchDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _queued;
        private SessionToken? _session;
        internal readonly List<Task> Work = [];
        internal readonly TaskCompletionSource DispatchQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly InventorySnapshot Snapshot;
        internal ChangesTabView View { get; }
        internal int Reviews { get; private set; }
        internal Action? DuringReview { get; set; }

        internal Fixture(Func<CancellationToken, Task<InventorySnapshot>> capture)
        {
            _app.Driver!.SetScreenSize(100, 26);
            var skill = MaintenanceQueueTests.Skill("/skills/broken", "https://github.com/one/repo") with
            { Validity = ValidityState.MissingSkillMd };
            Snapshot = InventorySnapshot.Empty with { Skills = [skill] };
            View = new ChangesTabView(action => { action(); return Task.CompletedTask; },
                _ => Task.FromResult(Snapshot),
                () => { View!.CancelPendingLoad(); View.Visible = false; _destination.Visible = true; _destination.SetFocus(); },
                runTask: (work, _) => Work.Add(work()),
                activateEntryAsync: (entry, request) => SelectedCleanupHandoff.RunAsync(entry, request, capture,
                    (action, _) => { _queued = action; DispatchQueued.TrySetResult(); return _dispatchDone.Task; },
                    (_, candidates) => { Assert.Single(candidates); Reviews++; DuringReview?.Invoke(); },
                    () => throw new InvalidOperationException("The exact fixture candidate must remain present.")))
            { Width = Dim.Fill(), Height = Dim.Fill() };
            _window.Add(View, _destination);
        }

        internal async Task StartAsync()
        {
            await View.LoadAsync();
            _session = _app.Begin(_window);
            var table = View.SubViews.OfType<TableView>().Single();
            table.SetFocus();
            Assert.True(table.HasFocus);
        }

        internal void Press(Key key)
        {
            _app.Keyboard.RaiseKeyDownEvent(key);
            _window.Draw(new DrawContext());
        }
        internal void RunQueuedCallback() { Assert.NotNull(_queued); _queued(); }
        internal void FinishDispatch() => _dispatchDone.SetResult(true);
        internal void CompleteDispatch() { RunQueuedCallback(); FinishDispatch(); }
        public void Dispose()
        {
            if (_session is not null) _app.End(_session);
            _window.Dispose();
            _app.Dispose();
        }
    }
}
