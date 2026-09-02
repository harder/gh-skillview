using SkillView.Threading;
using Xunit;

namespace SkillView.Tests.Threading;

public sealed class CancellationSourceTests
{
    [Fact]
    public void ParentCancellation_CallbackFailureIsReportedWithoutEscaping()
    {
        using var parent = new CancellationTokenSource();
        AggregateException? reported = null;
        using var source = new CancellationSource(parent.Token, ex => reported = ex);
        using var registration = source.Token.Register(() =>
            throw new InvalidOperationException("callback failed"));

        var exception = Record.Exception(parent.Cancel);

        Assert.Null(exception);
        Assert.True(source.Token.IsCancellationRequested);
        var failure = Assert.IsType<InvalidOperationException>(
            Assert.Single(Assert.IsType<AggregateException>(reported).InnerExceptions));
        Assert.Equal("callback failed", failure.Message);
    }

    [Fact]
    public async Task Deadline_CallbackFailureIsContainedOnTimerThread()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<AggregateException>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new CancellationSource(
            CancellationToken.None,
            TimeSpan.FromMilliseconds(10),
            ex => reported.TrySetResult(ex));
        using var canceledRegistration = source.Token.Register(canceled.SetResult);
        using var failingRegistration = source.Token.Register(() =>
            throw new InvalidOperationException("deadline callback failed"));

        await canceled.Task.WaitAsync(TestContext.Current.CancellationToken);
        var failure = await reported.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(source.Token.IsCancellationRequested);
        Assert.IsType<InvalidOperationException>(Assert.Single(failure.InnerExceptions));
    }

    [Fact]
    public void DisposeFromCancellationCallback_DefersResourceDisposal()
    {
        var source = new CancellationSource();
        var token = source.Token;
        using var registration = token.Register(source.Dispose);

        source.TryCancel();
        source.TryCancel();

        Assert.True(token.IsCancellationRequested);
        Assert.False(source.TryGetActiveToken(out var rejected));
        Assert.True(rejected.IsCancellationRequested);
    }

    [Fact]
    public void ReporterFailure_DoesNotEscapeCancellation()
    {
        using var source = new CancellationSource(_ =>
            throw new InvalidOperationException("reporter failed"));
        using var registration = source.Token.Register(() =>
            throw new InvalidOperationException("callback failed"));

        var exception = Record.Exception(() => source.TryCancel());

        Assert.Null(exception);
        Assert.True(source.Token.IsCancellationRequested);
    }

    [Fact]
    public void CancelAfterDispose_IsNoOpAndLeavesStableTokenUncanceled()
    {
        var source = new CancellationSource();
        var token = source.Token;

        source.Dispose();
        var canceled = source.TryCancel();

        Assert.False(canceled);
        Assert.False(token.IsCancellationRequested);
        Assert.False(source.TryGetActiveToken(out var rejected));
        Assert.False(rejected.IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_RejectsCancellationWhileEarlierCallbackIsStillRunning()
    {
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new CancellationSource();
        var testCancellation = TestContext.Current.CancellationToken;
        using var registration = source.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });
        var firstCancellation = Task.Factory.StartNew(
            source.TryCancel,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            await callbackEntered.Task.WaitAsync(testCancellation);

            source.Dispose();

            Assert.False(source.TryCancel());
        }
        finally
        {
            releaseCallback.TrySetResult();
        }

        Assert.True(await firstCancellation.WaitAsync(testCancellation));
    }

    [Fact]
    public void Deadline_RejectsValuesBeyondTimerRange()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CancellationSource(TimeSpan.FromMilliseconds(uint.MaxValue)));

        Assert.Equal("timeout", exception.ParamName);
    }

    [Fact]
    public async Task Deadline_DoesNotCaptureAmbientExecutionContext()
    {
        var ambient = new AsyncLocal<string?>();
        ambient.Value = "caller context";
        var observed = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new CancellationSource(
            TimeSpan.FromMilliseconds(10),
            _ => observed.TrySetResult(ambient.Value));
        using var registration = source.Token.UnsafeRegister(
            static _ => throw new InvalidOperationException("callback failed"),
            state: null);

        ambient.Value = null;
        var deadlineContext = await observed.Task.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.Null(deadlineContext);
        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public void CancelAndDispose_AreSafeUnderContention()
    {
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var source = new CancellationSource();
            var token = source.Token;

            Parallel.Invoke(() => source.TryCancel(), source.Dispose);

            _ = token.IsCancellationRequested;
            source.TryCancel();
            source.Dispose();
        }
    }
}
