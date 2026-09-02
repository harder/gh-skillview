using System.Runtime.CompilerServices;
using SkillView.Tests.Ui;
using SkillView.Threading;
using Xunit;

namespace SkillView.Tests.Threading;

[Collection(TestCollections.ResourceStress)]
public sealed class CancellationSourceResourceTests
{
    [Fact]
    public void AlreadyCanceledParent_DoesNotRetainAbandonedOwnerUntilDeadline()
    {
        using var parent = new CancellationTokenSource();
        parent.Cancel();

        var probe = CreateAbandonedDeadlineOwner(parent.Token);
        ForceCollection();

        Assert.False(probe.IsAlive);
        GC.KeepAlive(parent);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAbandonedDeadlineOwner(CancellationToken parent)
    {
        var marker = new object();
        _ = new CancellationSource(
            parent,
            TimeSpan.FromMinutes(5),
            _ => GC.KeepAlive(marker));
        return new WeakReference(marker);
    }

    private static void ForceCollection()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
