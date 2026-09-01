using SkillView.Logging;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class ModalOperationTrackerTests
{
    [Fact]
    public async Task CompletedWorker_RemainsOwnedUntilUiCommitReleasesIt()
    {
        using var tracker = NewTracker();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(tracker.TryStart(_ =>
        {
            completed.TrySetResult();
            return Task.CompletedTask;
        }));

        await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => tracker.CurrentOwnership == ModalOperationTracker.Ownership.AwaitingUiCompletion,
            TestContext.Current.CancellationToken);

        tracker.Release();

        Assert.Equal(ModalOperationTracker.Ownership.None, tracker.CurrentOwnership);
    }

    [Fact]
    public async Task Dispose_CancelsAndDrainsActiveWorker()
    {
        var tracker = NewTracker();
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(tracker.TryStart(async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        }));

        tracker.Dispose();

        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(tracker.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_CallbackFailureDoesNotSkipWorkerDrain()
    {
        var logger = new Logger();
        var tracker = new ModalOperationTracker(action => action(), logger, "test.ui");
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = tracker.Token.Register(() =>
            throw new InvalidOperationException("callback failed"));

        Assert.True(tracker.TryStart(async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        }));

        var exception = Record.Exception(tracker.Dispose);

        Assert.Null(exception);
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            logger.Snapshot(),
            entry => entry.Category == "test.ui"
                && entry.Message.Contains("cancellation callback failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReleaseInsideWorker_DoesNotClearOwnershipUntilWorkerReturns()
    {
        using var tracker = NewTracker();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(tracker.TryStart(async _ =>
        {
            tracker.Release();
            released.TrySetResult();
            await finish.Task;
        }));

        await released.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ModalOperationTracker.Ownership.Running, tracker.CurrentOwnership);

        finish.TrySetResult();
        await WaitUntilAsync(
            () => tracker.CurrentOwnership == ModalOperationTracker.Ownership.None,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void InvokeAfterDispose_DoesNotTouchApplication()
    {
        var invokeCount = 0;
        var tracker = new ModalOperationTracker(
            action =>
            {
                invokeCount++;
                action();
            },
            new Logger(),
            "test.ui");
        tracker.Dispose();

        tracker.InvokeIfActive(() => throw new InvalidOperationException("must not run"));

        Assert.Equal(0, invokeCount);
    }

    [Fact]
    public async Task TerminalCallbackFailure_ReleasesOwnershipAfterWorkerReturns()
    {
        using var tracker = NewTracker();

        Assert.True(tracker.TryStart(_ =>
        {
            tracker.InvokeTerminalIfActive(() =>
                throw new InvalidOperationException("terminal callback failed"));
            return Task.CompletedTask;
        }));

        await WaitUntilAsync(
            () => tracker.CurrentOwnership == ModalOperationTracker.Ownership.None,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TerminalDispatchFailure_ReleasesOwnershipAfterWorkerReturns()
    {
        using var tracker = new ModalOperationTracker(
            _ => throw new InvalidOperationException("dispatch failed"),
            new Logger(),
            "test.ui");

        Assert.True(tracker.TryStart(_ =>
        {
            tracker.InvokeTerminalIfActive(() => { });
            return Task.CompletedTask;
        }));

        await WaitUntilAsync(
            () => tracker.CurrentOwnership == ModalOperationTracker.Ownership.None,
            TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static ModalOperationTracker NewTracker() =>
        new(action => action(), new Logger(), "test.ui");
}
