using SkillView.Bootstrapping;
using Xunit;

namespace SkillView.Tests.Bootstrapping;

public sealed class RootCancellationTests
{
    [Fact]
    public void RequestCancellation_IsIdempotentAndCancelsStableToken()
    {
        using var root = new RootCancellation();
        var token = root.Token;

        root.RequestCancellation();
        root.RequestCancellation();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void ParentCancellation_Propagates()
    {
        using var parent = new CancellationTokenSource();
        using var root = new RootCancellation(parent.Token);

        parent.Cancel();

        Assert.True(root.Token.IsCancellationRequested);
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
            token =>
            {
                startupResourceCreated = true;
                return new RootCancellation(token);
            });

        Assert.Equal(ExitCodes.Cancelled, exitCode);
        Assert.False(startupResourceCreated);
    }
}
