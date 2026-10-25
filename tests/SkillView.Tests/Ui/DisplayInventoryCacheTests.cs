using SkillView.Inventory.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class DisplayInventoryCacheTests
{
    [Fact]
    public async Task TabCancellationDoesNotCancelSharedCaptureAndCompletedResultIsReused()
    {
        var completion = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? worker = null;
        var calls = 0;
        var cache = new DisplayInventoryCache(() => 0, token =>
        {
            Assert.False(token.IsCancellationRequested);
            calls++;
            return completion.Task;
        }, (work, _) => { worker = work(CancellationToken.None); return true; });
        using var firstTab = new CancellationTokenSource();
        var first = cache.GetAsync(firstTab.Token);
        firstTab.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = cache.GetAsync(TestContext.Current.CancellationToken);
        var result = InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UtcNow };
        completion.SetResult(result);
        Assert.Same(result, await second);
        await worker!;
        Assert.Same(result, await cache.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidationStartsFreshCaptureBeforeObsoleteWorkerFinishes(bool obsoleteFails)
    {
        long generation = 0;
        var obsolete = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var fresh = InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UtcNow };
        var cache = new DisplayInventoryCache(() => Interlocked.Read(ref generation), _ =>
            ++calls == 1 ? obsolete.Task : current.Task,
            (work, operation) =>
            {
                var task = work(CancellationToken.None);
                if (calls == 1) _ = task.ContinueWith(_ => oldWorker.SetResult(), TaskScheduler.Default);
                return true;
            });
        var first = cache.GetAsync(TestContext.Current.CancellationToken);
        Interlocked.Increment(ref generation);
        var second = cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.False(second.IsCompleted);
        if (obsoleteFails) obsolete.SetException(new IOException("obsolete scan failed"));
        else obsolete.SetResult(InventorySnapshot.Empty);
        await oldWorker.Task.WaitAsync(TestContext.Current.CancellationToken);
        var third = cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls); // Old completion cannot clear the current flight.
        current.SetResult(fresh);
        Assert.Same(fresh, await first);
        Assert.Same(fresh, await second);
        Assert.Same(fresh, await third);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExpiryAndInvalidationRequireNewCaptures()
    {
        var now = DateTimeOffset.UtcNow;
        long generation = 0;
        var calls = 0;
        var cache = new DisplayInventoryCache(() => generation, _ =>
        {
            calls++;
            return Task.FromResult(InventorySnapshot.Empty);
        }, Start, () => now);
        await cache.GetAsync(TestContext.Current.CancellationToken);
        now += TimeSpan.FromSeconds(59);
        await cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        now += TimeSpan.FromSeconds(1);
        await cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        generation++;
        await cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task FailedCaptureIsNotCached()
    {
        var calls = 0;
        var cache = new DisplayInventoryCache(() => 0, _ =>
            ++calls == 1 ? Task.FromException<InventorySnapshot>(new IOException("scan failed"))
                : Task.FromResult(InventorySnapshot.Empty), Start);
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(TestContext.Current.CancellationToken));
        Assert.Empty((await cache.GetAsync(TestContext.Current.CancellationToken)).Skills);
        Assert.Equal(2, calls);
    }
    [Fact]
    public async Task SchedulingFailureDoesNotStrandFutureWaiters()
    {
        var schedules = 0;
        var cache = new DisplayInventoryCache(() => 0, _ => Task.FromResult(InventorySnapshot.Empty),
            (work, operation) =>
            {
                if (++schedules == 1) throw new InvalidOperationException("scheduler refused");
                return Start(work, operation);
            });
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(TestContext.Current.CancellationToken));
        Assert.Empty((await cache.GetAsync(TestContext.Current.CancellationToken)).Skills);
        Assert.Equal(2, schedules);
    }

    [Fact]
    public async Task AppCancellationSettlesSharedWaitersAndDoesNotCacheTheFailure()
    {
        using var app = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? worker = null;
        var cache = new DisplayInventoryCache(() => 0, async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return InventorySnapshot.Empty;
        }, (work, operation) => { worker = work(app.Token); return true; });
        var first = cache.GetAsync(TestContext.Current.CancellationToken);
        var second = cache.GetAsync(TestContext.Current.CancellationToken);
        await started.Task;
        app.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await worker!;
    }

    [Fact]
    public async Task FreshOperationCaptureCanSeedDisplayWithoutRepeatingItsScan()
    {
        long generation = 1;
        var cache = new DisplayInventoryCache(() => generation,
            _ => throw new InvalidOperationException("Display must reuse the completed fresh capture"),
            Start);
        var fresh = InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UtcNow };
        cache.PublishFresh(fresh, generation);
        Assert.Same(fresh, await cache.GetAsync(TestContext.Current.CancellationToken));
        cache.PublishFresh(InventorySnapshot.Empty, generation);
        Assert.Same(fresh, await cache.GetAsync(TestContext.Current.CancellationToken));
        generation++;
        cache.PublishFresh(fresh, generation - 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OlderInFlightCaptureCannotOverwriteNewerFreshObservation()
    {
        var pending = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new DisplayInventoryCache(() => 0, _ => pending.Task,
            Start);
        var loading = cache.GetAsync(TestContext.Current.CancellationToken);
        var fresh = InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UtcNow };
        cache.PublishFresh(fresh, 0);
        pending.SetResult(InventorySnapshot.Empty);
        Assert.Same(fresh, await loading);
        Assert.Same(fresh, await cache.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefusedBackgroundWorkSettlesUncancelableWaitersAndAllowsRetry()
    {
        var tracker = new BackgroundTaskTracker(exception => Assert.Fail(exception.ToString()));
        tracker.StopAccepting();
        var refused = true;
        var cache = new DisplayInventoryCache(() => 0, _ => Task.FromResult(InventorySnapshot.Empty),
            (work, operation) => refused
                ? tracker.TryRun(() => work(CancellationToken.None)) : Start(work, operation));
        // CancellationToken.None reproduces the shutdown waiter that cannot self-cancel.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await tracker.DrainAsync();
        refused = false;
        Assert.Same(InventorySnapshot.Empty, await cache.GetAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("roots")]
    [InlineData("scan")]
    public async Task IncompleteCaptureIsSharedWithCurrentWaitersButRetriedOnNextVisit(string kind)
    {
        var pending = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var partial = kind switch
        {
            "provider" => InventorySnapshot.Empty with { Providers = [new("gh", false, [], DateTimeOffset.UtcNow, "transient")] },
            "roots" => InventorySnapshot.Empty with { ConfiguredRootsComplete = false },
            _ => InventorySnapshot.Empty with { Diagnostics = new() { InaccessiblePaths = ["denied"] } },
        };
        var calls = 0;
        var cache = new DisplayInventoryCache(() => 0, _ => ++calls == 1 ? pending.Task : Task.FromResult(InventorySnapshot.Empty), Start);
        var first = cache.GetAsync(TestContext.Current.CancellationToken);
        var second = cache.GetAsync(TestContext.Current.CancellationToken);
        pending.SetResult(partial);
        Assert.Same(partial, await first);
        Assert.Same(partial, await second);
        Assert.Equal(1, calls);
        Assert.Same(InventorySnapshot.Empty, await cache.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
        Assert.Same(InventorySnapshot.Empty, await cache.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NewerIncompleteOperationRetiresOlderCompletedDisplayData()
    {
        var calls = 0;
        var fresh = InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        var cache = new DisplayInventoryCache(() => 0, _ =>
        {
            calls++;
            return Task.FromResult(fresh);
        }, Start);
        cache.PublishFresh(InventorySnapshot.Empty, 0);
        cache.PublishFresh(InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UtcNow, ConfiguredRootsComplete = false }, 0);
        Assert.Same(fresh, await cache.GetAsync(TestContext.Current.CancellationToken));
        Assert.Same(fresh, await cache.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OlderCompletedFlightCannotHideNewerIncompleteOperationEvidence()
    {
        var pending = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var fresh = InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UnixEpoch.AddSeconds(30) };
        var cache = new DisplayInventoryCache(() => 0, _ => ++calls == 1 ? pending.Task : Task.FromResult(fresh), Start);
        var loading = cache.GetAsync(TestContext.Current.CancellationToken);
        var partial = InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UnixEpoch.AddSeconds(20), ConfiguredRootsComplete = false };
        cache.PublishFresh(partial, 0);
        pending.SetResult(InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UnixEpoch.AddSeconds(10) });
        Assert.Same(partial, await loading);
        Assert.Same(fresh, await cache.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
        Assert.Same(fresh, await cache.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    private static bool Start(Func<CancellationToken, Task> work, string operation)
    {
        _ = work(CancellationToken.None);
        return true;
    }

}
