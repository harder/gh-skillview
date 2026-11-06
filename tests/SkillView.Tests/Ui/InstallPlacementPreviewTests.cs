using System.Collections.Concurrent;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstallPlacementPreviewTests
{
    [Fact]
    public async Task EditsCancelAndCoalescePendingWorkAndQueuedResultsCannotPublishAfterClose()
    {
        var logger = new Logger(LogLevel.Debug);
        var queued = new ConcurrentQueue<Action>();
        var published = new List<string>();
        var firstStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDone = new TaskCompletionSource<InstallPlacementObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var skipped = 0;
        var preview = new InstallPlacementPreview(new ProcessRunner(logger), logger,
            action => { queued.Enqueue(action); if (Interlocked.Increment(ref callbacks) == 2) queuedTwice.SetResult(); },
            observation => published.Add(observation.Detail), () => true);
        preview.Update(token => { firstStarted.SetResult(token); return firstDone.Task; });
        var token = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        preview.Update(_ => { skipped++; return Task.FromResult(new InstallPlacementObservation("obsolete pending", [], "")); });
        preview.Update(_ => { latestStarted.SetResult(); return Task.FromResult(new InstallPlacementObservation("latest", [], "")); });
        Assert.True(token.IsCancellationRequested);
        firstDone.SetResult(new("obsolete running", [], ""));
        await latestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await queuedTwice.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, skipped);
        Assert.True(queued.TryDequeue(out var old)); old();
        Assert.DoesNotContain("obsolete running", published);
        Assert.True(queued.TryDequeue(out var latest)); latest();
        Assert.Equal("latest", published[^1]);
        var before = published.Count;
        preview.Dispose();
        latest();
        Assert.Equal(before, published.Count);
    }
}
