using System.Collections.Immutable;
using System.Collections.Concurrent;
using SkillView.Gh;
using SkillView.Gh.Models;
using Xunit;

namespace SkillView.Tests.Gh;

public sealed class GhSkillListCacheTests
{
    [Fact]
    public void TryGet_ReturnsStoredEntry_BeforeExpiry()
    {
        var now = DateTimeOffset.Parse("2026-05-01T00:00:00Z");
        var cache = new GhSkillListCache(() => now, ttl: TimeSpan.FromSeconds(10));
        var records = ImmutableArray.Create(new GhSkillListRecord { Name = "demo" });

        cache.Store("/usr/bin/gh", scope: "user", agent: "claude-code", records);

        var hit = cache.TryGet("/usr/bin/gh", "user", "claude-code", out var cached);

        Assert.True(hit);
        Assert.Equal(records, cached);
    }

    [Fact]
    public void TryGet_MissesAfterExpiry()
    {
        var now = DateTimeOffset.Parse("2026-05-01T00:00:00Z");
        var cache = new GhSkillListCache(() => now, ttl: TimeSpan.FromSeconds(10));
        cache.Store("/usr/bin/gh", scope: null, agent: null, ImmutableArray<GhSkillListRecord>.Empty);

        now = now.AddSeconds(11);

        Assert.False(cache.TryGet("/usr/bin/gh", null, null, out _));
    }

    [Fact]
    public void Invalidate_RemovesStoredEntries()
    {
        var cache = new GhSkillListCache(() => DateTimeOffset.UtcNow, ttl: TimeSpan.FromSeconds(10));
        cache.Store("/usr/bin/gh", scope: null, agent: null, ImmutableArray.Create(new GhSkillListRecord { Name = "demo" }));

        cache.Invalidate();

        Assert.False(cache.TryGet("/usr/bin/gh", null, null, out _));
    }

    [Fact]
    public async Task GetOrLoadAsync_ConcurrentMissesShareOneLoad()
    {
        var cache = new GhSkillListCache(ttl: TimeSpan.FromSeconds(10));
        var records = ImmutableArray.Create(new GhSkillListRecord { Name = "shared" });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaderCalls = 0;

        async Task<GhSkillListCache.LoadResult> Load(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loaderCalls);
            await release.Task.WaitAsync(cancellationToken);
            return new GhSkillListCache.LoadResult(records);
        }

        var callers = Enumerable.Range(0, 64)
            .Select(_ => cache.GetOrLoadAsync("/usr/bin/gh", "user", null, Load,
                TestContext.Current.CancellationToken))
            .ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref loaderCalls) == 1,
            TestContext.Current.CancellationToken);
        release.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, loaderCalls);
        Assert.All(results, result => Assert.Equal(records, result.Records));
        Assert.True(cache.TryGet("/usr/bin/gh", "user", null, out var cached));
        Assert.Equal(records, cached);
    }

    [Fact]
    public async Task GetOrLoadAsync_SimultaneousExpiryStartsOneRefresh()
    {
        var now = DateTimeOffset.Parse("2026-05-01T00:00:00Z");
        var cache = new GhSkillListCache(() => now, ttl: TimeSpan.FromSeconds(10));
        cache.Store("/usr/bin/gh", null, null,
            ImmutableArray.Create(new GhSkillListRecord { Name = "stale" }));
        now = now.AddSeconds(11);

        var loaderCalls = 0;
        var fresh = ImmutableArray.Create(new GhSkillListRecord { Name = "fresh" });
        var callers = Enumerable.Range(0, 64)
            .Select(_ => cache.GetOrLoadAsync(
                "/usr/bin/gh",
                null,
                null,
                _ =>
                {
                    Interlocked.Increment(ref loaderCalls);
                    return Task.FromResult(new GhSkillListCache.LoadResult(fresh));
                },
                TestContext.Current.CancellationToken))
            .ToArray();

        var results = await Task.WhenAll(callers);

        Assert.Equal(1, loaderCalls);
        Assert.All(results, result => Assert.Equal(fresh, result.Records));
    }

    [Fact]
    public async Task Invalidate_DuringLoadWaitsForCancellationCleanup_AndRejectsStaleCompletion()
    {
        var cache = new GhSkillListCache(ttl: TimeSpan.FromMinutes(1));
        var loaderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lookup = cache.GetOrLoadAsync(
            "/usr/bin/gh",
            null,
            null,
            async cancellationToken =>
            {
                loaderStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.SetResult();
                    await releaseCleanup.Task.WaitAsync(TestContext.Current.CancellationToken);
                    throw;
                }
            },
            TestContext.Current.CancellationToken);

        await loaderStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cache.Invalidate();
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(lookup.IsCompleted);

        releaseCleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.False(cache.TryGet("/usr/bin/gh", null, null, out _));
    }

    [Fact]
    public async Task Invalidate_CallbackFailureDoesNotEscapeOrReleaseBeforeCleanup()
    {
        var cache = new GhSkillListCache(ttl: TimeSpan.FromMinutes(1));
        var loaderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lookup = cache.GetOrLoadAsync(
            "/usr/bin/gh",
            null,
            null,
            async cancellationToken =>
            {
                using var registration = cancellationToken.Register(() =>
                    throw new InvalidOperationException("callback failed"));
                loaderStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            },
            TestContext.Current.CancellationToken);

        await loaderStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var exception = Record.Exception(cache.Invalidate);

        Assert.Null(exception);
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.False(cache.TryGet("/usr/bin/gh", null, null, out _));
    }

    [Fact]
    public async Task GetOrLoadAsync_WhenAllWaitersCancel_CancelsSharedLoad()
    {
        var cache = new GhSkillListCache(ttl: TimeSpan.FromMinutes(1));
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var loaderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaderCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaderCalls = 0;

        async Task<GhSkillListCache.LoadResult> Load(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loaderCalls);
            loaderStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
            catch (OperationCanceledException)
            {
                loaderCanceled.TrySetResult();
                throw;
            }
        }

        var first = cache.GetOrLoadAsync("/usr/bin/gh", null, null, Load, firstCancellation.Token);
        var second = cache.GetOrLoadAsync("/usr/bin/gh", null, null, Load, secondCancellation.Token);
        await loaderStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(loaderCanceled.Task.IsCompleted);

        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await loaderCanceled.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, loaderCalls);
    }

    [Fact]
    public void TryGetStoreInvalidate_AreSafeUnderHighContention()
    {
        var cache = new GhSkillListCache(ttl: TimeSpan.FromSeconds(10));
        var errors = new ConcurrentQueue<Exception>();
        var records = ImmutableArray.Create(new GhSkillListRecord { Name = "demo" });

        Parallel.For(0, 100_000, index =>
        {
            try
            {
                switch (index % 3)
                {
                    case 0:
                        cache.TryGet("/usr/bin/gh", (index % 7).ToString(), null, out _);
                        break;
                    case 1:
                        cache.Store("/usr/bin/gh", (index % 7).ToString(), null, records);
                        break;
                    default:
                        cache.Invalidate();
                        break;
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void ClockCallback_RunsOutsideCacheLock()
    {
        GhSkillListCache? cache = null;
        var firstCall = 1;
        var invalidatedInsideClock = false;
        cache = new GhSkillListCache(
            () =>
            {
                if (Interlocked.Exchange(ref firstCall, 0) == 1)
                {
                    var invalidation = Task.Run(
                        cache!.Invalidate,
                        TestContext.Current.CancellationToken);
                    invalidatedInsideClock = invalidation.Wait(TimeSpan.FromSeconds(2));
                }
                return DateTimeOffset.Parse("2026-05-01T00:00:00Z");
            },
            ttl: TimeSpan.FromSeconds(10));

        cache.Store(
            "/usr/bin/gh",
            scope: null,
            agent: null,
            ImmutableArray<GhSkillListRecord>.Empty);

        Assert.True(invalidatedInsideClock);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
