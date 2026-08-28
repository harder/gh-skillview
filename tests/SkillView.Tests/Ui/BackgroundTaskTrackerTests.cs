using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class BackgroundTaskTrackerTests
{
    [Fact]
    public async Task DrainAsync_WaitsForEveryAdmittedOperation_AndRejectsNewWork()
    {
        var errors = new List<Exception>();
        var tracker = new BackgroundTaskTracker(errors.Add);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(tracker.TryRun(async () =>
        {
            started.SetResult();
            await release.Task;
        }));
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        tracker.StopAccepting();
        var drain = tracker.DrainAsync();

        Assert.False(drain.IsCompleted);
        Assert.False(tracker.TryRun(static () => Task.CompletedTask));
        release.SetResult();
        await drain.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task DrainAsync_ObservesAndReportsOperationFailure()
    {
        var errors = new List<Exception>();
        var tracker = new BackgroundTaskTracker(errors.Add);

        Assert.True(tracker.TryRun(static () =>
            Task.FromException(new InvalidOperationException("failure"))));
        tracker.StopAccepting();
        await tracker.DrainAsync().WaitAsync(TestContext.Current.CancellationToken);

        var error = Assert.Single(errors);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("failure", error.Message);
    }
}
