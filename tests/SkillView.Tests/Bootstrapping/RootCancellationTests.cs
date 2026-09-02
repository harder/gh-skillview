using SkillView.Bootstrapping;
using Xunit;

namespace SkillView.Tests.Bootstrapping;

public sealed class RootCancellationTests
{
    [Fact]
    public void RequestCancellation_IsIdempotentAndCancelsStableToken()
    {
        using var root = new RootCancellation(CancellationToken.None, static _ => { });
        var token = root.Token;

        root.RequestCancellation();
        root.RequestCancellation();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void ParentCancellation_Propagates()
    {
        using var parent = new CancellationTokenSource();
        using var root = new RootCancellation(parent.Token, static _ => { });

        parent.Cancel();

        Assert.True(root.Token.IsCancellationRequested);
    }

    [Fact]
    public void ParentCancellation_CallbackFailureDoesNotEscapeRootBoundary()
    {
        using var parent = new CancellationTokenSource();
        AggregateException? reported = null;
        using var root = new RootCancellation(parent.Token, ex => reported = ex);
        using var registration = root.Token.Register(() =>
            throw new InvalidOperationException("callback failed"));

        var exception = Record.Exception(parent.Cancel);

        Assert.Null(exception);
        Assert.True(root.Token.IsCancellationRequested);
        var failure = Assert.IsType<InvalidOperationException>(
            Assert.Single(Assert.IsType<AggregateException>(reported).InnerExceptions));
        Assert.Equal("callback failed", failure.Message);
    }

    [Fact]
    public async Task EntryPoint_PreCanceledInvocation_ReturnsCancelledBeforeStartup()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var startupResourceCreated = false;

        var exitCode = await EntryPoint.RunAsync(
            ["--help"],
            cancellation.Token,
            (token, reporter) =>
            {
                startupResourceCreated = true;
                return new RootCancellation(token, reporter);
            });

        Assert.Equal(ExitCodes.Cancelled, exitCode);
        Assert.False(startupResourceCreated);
    }
}
