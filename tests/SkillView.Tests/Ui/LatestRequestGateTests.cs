using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class LatestRequestGateTests
{
    [Fact]
    public void Begin_CancelsOverlappingPreviewAndRejectsItsStaleResult()
    {
        using var gate = new LatestRequestGate();
        using var first = gate.Begin(CancellationToken.None, TimeSpan.FromMinutes(1));

        using var second = gate.Begin(CancellationToken.None, TimeSpan.FromMinutes(1));

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        Assert.False(second.Token.IsCancellationRequested);
        Assert.True(second.IsCurrent);
    }

    [Fact]
    public void Cancel_CancelsCurrentPreviewLease()
    {
        using var gate = new LatestRequestGate();
        using var request = gate.Begin(CancellationToken.None, TimeSpan.FromMinutes(1));

        gate.Cancel();

        Assert.True(request.Token.IsCancellationRequested);
        Assert.False(request.IsCurrent);
    }

    [Fact]
    public void BeginAndCancel_CanRaceSupersededLeaseReleaseWithoutThrowing()
    {
        for (var i = 0; i < 1_000; i++)
        {
            using var gate = new LatestRequestGate();
            var first = gate.Begin(CancellationToken.None, TimeSpan.FromMinutes(1));

            Parallel.Invoke(
                first.Dispose,
                () =>
                {
                    using var replacement = gate.Begin(CancellationToken.None, TimeSpan.FromMinutes(1));
                    gate.Cancel();
            });
        }
    }

    [Fact]
    public void Cancel_DoesNotHoldGateLockWhileRunningCallbacks()
    {
        using var gate = new LatestRequestGate();
        var request = gate.Begin(CancellationToken.None, TimeSpan.FromMinutes(1));
        var releasedInsideCallback = false;
        using var registration = request.Token.Register(() =>
        {
            var release = Task.Run(
                request.Dispose,
                TestContext.Current.CancellationToken);
            releasedInsideCallback = release.Wait(TimeSpan.FromSeconds(2));
        });

        Assert.True(gate.Cancel());

        Assert.True(releasedInsideCallback);
        Assert.True(request.Token.IsCancellationRequested);
        Assert.False(request.IsCurrent);
    }
}
