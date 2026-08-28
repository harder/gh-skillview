using SkillView.Logging;
using Xunit;

namespace SkillView.Tests.Logging;

public class LoggerTests
{
    [Fact]
    public void FiltersBelowMinimumLevel()
    {
        var logger = new Logger(LogLevel.Info);
        logger.Debug("t", "invisible");
        logger.Info("t", "visible");
        var entries = logger.Snapshot();
        Assert.Single(entries);
        Assert.Equal("visible", entries[0].Message);
    }

    [Fact]
    public void AppliesRedactionBeforePersistence()
    {
        var logger = new Logger(LogLevel.Info);
        logger.Info("t", "token ghp_AAAAAAAAAAAAAAAAAAAA1234567890 oops");
        var entry = Assert.Single(logger.Snapshot());
        Assert.DoesNotContain("ghp_", entry.Message);
    }

    [Fact]
    public void RingBufferHonoursCapacity()
    {
        var logger = new Logger(LogLevel.Info, capacity: 3);
        for (var i = 0; i < 10; i++)
        {
            logger.Info("t", $"msg-{i}");
        }
        var entries = logger.Snapshot();
        Assert.Equal(3, entries.Count);
        Assert.Equal("msg-7", entries[0].Message);
        Assert.Equal("msg-9", entries[^1].Message);
    }

    [Fact]
    public void Constructor_RejectsNegativeCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Logger(capacity: -1));
    }

    [Fact]
    public void Log_TruncatesIndividualMessagesBeforeRetention()
    {
        var logger = new Logger(maxMessageChars: 32, maxRetainedChars: 128);

        logger.Info("test", new string('x', 1_000_000));

        var entry = Assert.Single(logger.Snapshot());
        Assert.Equal(32, entry.Message.Length);
        Assert.EndsWith("… truncated", entry.Message);
    }

    [Fact]
    public void RingBuffer_AlsoHonorsTotalCharacterBudget()
    {
        var logger = new Logger(capacity: 10, maxMessageChars: 100, maxRetainedChars: 150);

        logger.Info("test", "first-" + new string('a', 74));
        logger.Info("test", "second-" + new string('b', 73));
        logger.Info("test", "third-" + new string('c', 74));

        var entry = Assert.Single(logger.Snapshot());
        Assert.StartsWith("third-", entry.Message);
    }

    [Fact]
    public void ErrorSnippet_IsSingleLineAndBounded()
    {
        var snippet = Logger.ErrorSnippet("  first line\r\nsecond\tline " + new string('x', 1000), 40);

        Assert.True(snippet.Length <= 40);
        Assert.DoesNotContain('\r', snippet);
        Assert.DoesNotContain('\n', snippet);
        Assert.DoesNotContain('\t', snippet);
    }

    [Fact]
    public void SubscriberReceivesEntries()
    {
        var logger = new Logger();
        var received = new List<LogEntry>();
        logger.Subscribe(received.Add);
        logger.Info("cat", "hello");
        Assert.Single(received);
        Assert.Equal("hello", received[0].Message);
    }

    [Fact]
    public void DisposedSubscriptionStopsReceivingEntries()
    {
        var logger = new Logger();
        var received = new List<LogEntry>();
        var subscription = logger.Subscribe(received.Add);

        logger.Info("cat", "before");
        subscription.Dispose();
        logger.Info("cat", "after");

        var entry = Assert.Single(received);
        Assert.Equal("before", entry.Message);
    }

    [Fact]
    public async Task DisposedSubscriptionIsSkippedWhenAConcurrentLogAlreadySnapshottedIt()
    {
        var logger = new Logger();
        var firstObserverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetCallCount = 0;
        using var blockingSubscription = logger.Subscribe(_ =>
        {
            firstObserverStarted.SetResult();
            releaseFirstObserver.Task.GetAwaiter().GetResult();
        });
        var targetSubscription = logger.Subscribe(_ => Interlocked.Increment(ref targetCallCount));

        var cancellationToken = TestContext.Current.CancellationToken;
        var logTask = Task.Run(() => logger.Info("cat", "concurrent"), cancellationToken);
        await firstObserverStarted.Task.WaitAsync(cancellationToken);

        try
        {
            targetSubscription.Dispose();
        }
        finally
        {
            releaseFirstObserver.SetResult();
        }
        await logTask;

        Assert.Equal(0, targetCallCount);
    }

    [Fact]
    public async Task SubscribeWithReplay_DeliversConcurrentHandoffExactlyOnceInOrder()
    {
        var logger = new Logger(capacity: 16);
        logger.Info("test", "before-1");
        logger.Info("test", "before-2");
        var received = new List<string>();
        var replayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        IDisposable? subscription = null;
        var subscribe = Task.Run(() =>
        {
            subscription = logger.SubscribeWithReplay(entry =>
            {
                received.Add(entry.Message);
                if (entry.Message == "before-1")
                {
                    replayStarted.SetResult();
                    releaseReplay.Task.GetAwaiter().GetResult();
                }
            });
        }, TestContext.Current.CancellationToken);

        await replayStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var concurrentLog = Task.Run(
            () => logger.Info("test", "during-handoff"),
            TestContext.Current.CancellationToken);
        releaseReplay.SetResult();
        await Task.WhenAll(subscribe, concurrentLog);
        logger.Info("test", "after");
        subscription!.Dispose();

        Assert.Equal(["before-1", "before-2", "during-handoff", "after"], received);
    }

    [Fact]
    public async Task Subscriber_OutOfOrderGapAppliesBackpressureWithoutRetainingPendingEntries()
    {
        var firstInvokeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstInvoke = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaitingForSequence = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new Logger(
            LogLevel.Info,
            capacity: 16,
            maxMessageChars: Logger.DefaultMaxMessageChars,
            maxRetainedChars: Logger.DefaultMaxRetainedChars,
            beforeObserverInvokeForTests: sequence =>
            {
                if (sequence != 1) return;
                firstInvokeStarted.SetResult();
                releaseFirstInvoke.Task.GetAwaiter().GetResult();
            },
            observerBackpressureForTests: sequence =>
            {
                if (sequence == 2) secondWaitingForSequence.SetResult();
            });
        var received = new List<string>();
        using var subscription = logger.Subscribe(entry => received.Add(entry.Message));

        var first = Task.Run(
            () => logger.Info("test", "first"),
            TestContext.Current.CancellationToken);
        await firstInvokeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = Task.Run(
            () => logger.Info("test", "second"),
            TestContext.Current.CancellationToken);
        await secondWaitingForSequence.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(second.IsCompleted);

        releaseFirstInvoke.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(["first", "second"], received);
    }

    [Fact]
    public void Subscriber_RecursiveWriteToSameLoggerIsRejectedWithoutMutatingRing()
    {
        var logger = new Logger();
        using var subscription = logger.Subscribe(_ => logger.Info("test", "nested"));

        logger.Info("test", "outer");

        var entry = Assert.Single(logger.Snapshot());
        Assert.Equal("outer", entry.Message);
    }

    [Fact]
    public async Task Subscriber_IndirectRecursiveWriteAcrossLoggersIsRejectedWithoutDeadlock()
    {
        var first = new Logger();
        var second = new Logger();
        using var firstSubscription = first.Subscribe(_ => second.Info("test", "from-first"));
        using var secondSubscription = second.Subscribe(_ => first.Info("test", "from-second"));

        var write = Task.Run(
            () => first.Info("test", "outer"),
            TestContext.Current.CancellationToken);
        await write.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var firstEntry = Assert.Single(first.Snapshot());
        var secondEntry = Assert.Single(second.Snapshot());
        Assert.Equal("outer", firstEntry.Message);
        Assert.Equal("from-first", secondEntry.Message);
    }

    [Fact]
    public void SubscribeWithReplay_UsesOnlyRetainedRingEntries()
    {
        var logger = new Logger(capacity: 2);
        logger.Info("test", "evicted");
        logger.Info("test", "retained-1");
        logger.Info("test", "retained-2");
        var received = new List<string>();

        using var subscription = logger.SubscribeWithReplay(entry => received.Add(entry.Message));
        logger.Info("test", "live");

        Assert.Equal(["retained-1", "retained-2", "live"], received);
    }
}
