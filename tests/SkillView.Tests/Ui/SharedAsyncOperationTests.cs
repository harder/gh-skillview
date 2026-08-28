using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class SharedAsyncOperationTests
{
    [Fact]
    public async Task ConcurrentWaitersShareOneOperation()
    {
        var shared = new SharedAsyncOperation<int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<int> Operation(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(cancellationToken);
            return 42;
        }

        var waiters = Enumerable.Range(0, 32)
            .Select(_ => shared.GetAsync(Operation, TestContext.Current.CancellationToken))
            .ToArray();
        release.SetResult();

        Assert.All(await Task.WhenAll(waiters), value => Assert.Equal(42, value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FinalCanceledWaiterWaitsForUnderlyingCancellationCleanup()
    {
        var shared = new SharedAsyncOperation<int>();
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var waiter = shared.GetAsync(async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.SetResult();
                await releaseCleanup.Task.WaitAsync(TestContext.Current.CancellationToken);
                throw;
            }
        }, callerCancellation.Token);

        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        callerCancellation.Cancel();
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);

        releaseCleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }
}
