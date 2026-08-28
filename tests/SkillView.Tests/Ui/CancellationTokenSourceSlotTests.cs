using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class CancellationTokenSourceSlotTests
{
    [Fact]
    public void Replace_CancelsPreviousLeaseAndKeepsReplacementActive()
    {
        var slot = new CancellationTokenSourceSlot();
        using var first = slot.Replace(CancellationToken.None);

        using var second = slot.Replace(CancellationToken.None);

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);
        Assert.True(slot.HasActive);
    }

    [Fact]
    public void Cancel_CanRaceLeaseReleaseWithoutThrowing()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var slot = new CancellationTokenSourceSlot();
            var lease = slot.Replace(CancellationToken.None);

            Parallel.Invoke(() => slot.Cancel(), lease.Dispose);

            Assert.False(slot.HasActive);
        }
    }

    [Fact]
    public void Replace_CanRaceSupersededLeaseReleaseWithoutThrowing()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var slot = new CancellationTokenSourceSlot();
            var first = slot.Replace(CancellationToken.None);

            Parallel.Invoke(
                first.Dispose,
                () =>
                {
                    using var replacement = slot.Replace(CancellationToken.None);
                });

            Assert.False(slot.HasActive);
        }
    }

    [Fact]
    public void TryBegin_AllowsOnlyOneActiveOperation()
    {
        var slot = new CancellationTokenSourceSlot();
        using var first = slot.TryBegin(CancellationToken.None);

        using var rejected = slot.TryBegin(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(rejected);
    }

    [Fact]
    public void Cancel_DoesNotHoldSlotLockWhileRunningCallbacks()
    {
        var slot = new CancellationTokenSourceSlot();
        var lease = slot.Replace(CancellationToken.None);
        var releasedInsideCallback = false;
        using var registration = lease.Token.Register(() =>
        {
            var release = Task.Run(
                lease.Dispose,
                TestContext.Current.CancellationToken);
            releasedInsideCallback = release.Wait(TimeSpan.FromSeconds(2));
        });

        Assert.True(slot.Cancel());

        Assert.True(releasedInsideCallback);
        Assert.False(slot.HasActive);
    }
}
