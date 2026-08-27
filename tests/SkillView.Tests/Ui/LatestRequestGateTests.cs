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
}
