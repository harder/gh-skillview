using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class SearchAgentMetadataLoaderTests
{
    [Fact]
    public void Constructor_RejectsPreviewTimeoutBeyondTimerRange()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SearchAgentMetadataLoader(
                new SearchAgentMetadataCache(),
                new Logger(),
                previewTimeout: TimeSpan.FromMilliseconds(uint.MaxValue)));

        Assert.Equal("previewTimeout", exception.ParamName);
    }

    [Fact]
    public async Task FilterAsync_BoundsTwoHundredMetadataPreviews()
    {
        const int maxConcurrency = 4;
        var cache = new SearchAgentMetadataCache();
        var loader = new SearchAgentMetadataLoader(
            cache,
            new Logger(LogLevel.Debug),
            maxConcurrency,
            TimeSpan.FromSeconds(5));
        var results = Enumerable.Range(0, 200)
            .Select(i => Skill($"owner/repo-{i}", $"skill-{i}"))
            .ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allSlotsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maxObserved = 0;
        var calls = 0;

        var filtering = loader.FilterAsync(
            results,
            "claude-code",
            async (result, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                var nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maxObserved, nowActive);
                if (nowActive == maxConcurrency)
                {
                    allSlotsEntered.TrySetResult();
                }

                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return Success(result, "claude-code");
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            TestContext.Current.CancellationToken);

        await allSlotsEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(maxConcurrency, Volatile.Read(ref calls));
        Assert.False(filtering.IsCompleted);

        release.SetResult();
        var filtered = await filtering.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(200, calls);
        Assert.Equal(maxConcurrency, maxObserved);
        Assert.Equal(200, filtered.Count);
        Assert.Equal(200, cache.CountForTests);
    }

    [Fact]
    public async Task FilterAsync_BoundsConcurrencyAcrossOverlappingSearches()
    {
        const int maxConcurrency = 4;
        var loader = new SearchAgentMetadataLoader(
            new SearchAgentMetadataCache(),
            new Logger(LogLevel.Debug),
            maxConcurrency,
            TimeSpan.FromSeconds(5));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allSlotsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maxObserved = 0;
        var calls = 0;

        async Task<PreviewResult> LoadAsync(SearchResultSkill result, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            var nowActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maxObserved, nowActive);
            if (nowActive == maxConcurrency)
            {
                allSlotsEntered.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return Success(result, "claude-code");
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        var first = loader.FilterAsync(
            Enumerable.Range(0, 20).Select(i => Skill($"one/repo-{i}", $"one-{i}")).ToArray(),
            "claude-code",
            LoadAsync,
            TestContext.Current.CancellationToken);
        var second = loader.FilterAsync(
            Enumerable.Range(0, 20).Select(i => Skill($"two/repo-{i}", $"two-{i}")).ToArray(),
            "claude-code",
            LoadAsync,
            TestContext.Current.CancellationToken);

        await allSlotsEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(maxConcurrency, Volatile.Read(ref calls));

        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(40, calls);
        Assert.Equal(maxConcurrency, maxObserved);
    }

    [Fact]
    public async Task FilterAsync_TimesOutOnePreviewWithoutBlockingOtherResults()
    {
        var logger = new Logger(LogLevel.Debug);
        var cache = new SearchAgentMetadataCache();
        var loader = new SearchAgentMetadataLoader(
            cache,
            logger,
            maxConcurrency: 2,
            previewTimeout: TimeSpan.FromMilliseconds(50));
        var stalled = Skill("owner/stalled", "stalled");
        var healthy = Skill("owner/healthy", "healthy");

        var filtered = await loader.FilterAsync(
            [stalled, healthy],
            "claude-code",
            async (result, cancellationToken) =>
            {
                if (ReferenceEquals(result, stalled))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return Success(result, "claude-code");
            },
            TestContext.Current.CancellationToken);

        Assert.Collection(filtered, result => Assert.Same(healthy, result));
        Assert.Equal(1, cache.CountForTests);
        Assert.Contains(
            logger.Snapshot(),
            entry => entry.Category == "search.agent"
                && entry.Message.Contains("timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FilterAsync_TimeoutContainsCallbackFailure()
    {
        var logger = new Logger(LogLevel.Debug);
        var callbackFailureLogged = new TaskCompletionSource<LogEntry>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = logger.Subscribe(entry =>
        {
            if (entry.Category == "search.agent"
                && entry.Message.Contains("cancellation callback failed", StringComparison.Ordinal))
            {
                callbackFailureLogged.TrySetResult(entry);
            }
        });
        var loader = new SearchAgentMetadataLoader(
            new SearchAgentMetadataCache(),
            logger,
            maxConcurrency: 1,
            previewTimeout: TimeSpan.FromMilliseconds(20));
        var result = Skill("owner/repo", "demo");

        var filtered = await loader.FilterAsync(
            [result],
            "claude-code",
            async (_, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(() =>
                    throw new InvalidOperationException("callback failed"));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            },
            TestContext.Current.CancellationToken);

        Assert.Empty(filtered);
        var callbackFailure = await callbackFailureLogged.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(
            "System.InvalidOperationException: callback failed",
            callbackFailure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterAsync_DoesNotCacheTransientPreviewFailure()
    {
        var cache = new SearchAgentMetadataCache();
        var loader = new SearchAgentMetadataLoader(cache, new Logger(LogLevel.Debug));
        var result = Skill("owner/repo", "demo");
        var calls = 0;

        var first = await loader.FilterAsync(
            [result],
            "claude-code",
            (item, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(PreviewResult.Failure(
                    item.Repo!, item.SkillName, version: null, exit: 1, err: "temporary failure"));
            },
            TestContext.Current.CancellationToken);

        Assert.Empty(first);
        Assert.Equal(0, cache.CountForTests);

        var second = await loader.FilterAsync(
            [result],
            "claude-code",
            (item, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Success(item, "claude-code"));
            },
            TestContext.Current.CancellationToken);

        Assert.Single(second);
        Assert.Equal(2, calls);
        Assert.Equal(1, cache.CountForTests);
    }

    [Fact]
    public async Task FilterAsync_PropagatesRequestCancellationToEveryActivePreview()
    {
        const int maxConcurrency = 3;
        var cache = new SearchAgentMetadataCache();
        var loader = new SearchAgentMetadataLoader(
            cache,
            new Logger(LogLevel.Debug),
            maxConcurrency,
            TimeSpan.FromSeconds(5));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var allSlotsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var canceled = 0;

        var filtering = loader.FilterAsync(
            Enumerable.Range(0, 20)
                .Select(i => Skill($"owner/repo-{i}", $"skill-{i}"))
                .ToArray(),
            "claude-code",
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref started) == maxConcurrency)
                {
                    allSlotsEntered.TrySetResult();
                }

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("The held preview should have been canceled.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref canceled);
                    throw;
                }
            },
            cancellation.Token);

        await allSlotsEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => filtering.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(maxConcurrency, canceled);
        Assert.Equal(0, cache.CountForTests);
    }

    private static PreviewResult Success(SearchResultSkill result, string agent)
    {
        var markdown = $"---\nname: {result.SkillName}\nagents:\n  - {agent}\n---\nbody";
        return new PreviewResult
        {
            Repo = result.Repo!,
            SkillName = result.SkillName,
            Version = null,
            Body = markdown,
            MarkdownBody = markdown,
            AssociatedFiles = ImmutableArray<string>.Empty,
            Succeeded = true,
            ExitCode = 0,
            ErrorMessage = null,
        };
    }

    private static SearchResultSkill Skill(string repo, string skillName) =>
        new(
            Description: null,
            Namespace: "demo",
            Path: "skills/" + skillName,
            Repo: repo,
            SkillName: skillName,
            Stars: 1);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (candidate <= observed
                || Interlocked.CompareExchange(ref maximum, candidate, observed) == observed)
            {
                return;
            }
        }
    }
}
