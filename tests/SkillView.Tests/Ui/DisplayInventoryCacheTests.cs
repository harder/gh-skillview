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
        }, (work, _) => worker = work(CancellationToken.None));
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

    [Fact]
    public async Task InvalidationDuringCaptureDiscardsStaleResultWithoutOverlappingCaptures()
    {
        long generation = 0;
        var firstResult = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var fresh = InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UtcNow };
        var cache = new DisplayInventoryCache(() => Interlocked.Read(ref generation), _ =>
            ++calls == 1 ? firstResult.Task : Task.FromResult(fresh),
            (work, operation) => { _ = work(CancellationToken.None); });
        var first = cache.GetAsync(TestContext.Current.CancellationToken);
        Interlocked.Increment(ref generation);
        var second = cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        firstResult.SetResult(InventorySnapshot.Empty);
        Assert.Same(fresh, await first);
        Assert.Same(fresh, await second);
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
        }, (work, operation) => { _ = work(CancellationToken.None); }, () => now);
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
                : Task.FromResult(InventorySnapshot.Empty), (work, operation) => { _ = work(CancellationToken.None); });
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
                _ = work(CancellationToken.None);
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
        }, (work, operation) => worker = work(app.Token));
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
            (work, operation) => { _ = work(CancellationToken.None); });
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
            (work, operation) => { _ = work(CancellationToken.None); });
        var loading = cache.GetAsync(TestContext.Current.CancellationToken);
        var fresh = InventorySnapshot.Empty with { CapturedAt = DateTimeOffset.UtcNow };
        cache.PublishFresh(fresh, 0);
        pending.SetResult(InventorySnapshot.Empty);
        Assert.Same(fresh, await loading);
        Assert.Same(fresh, await cache.GetAsync(TestContext.Current.CancellationToken));
    }

}
