using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class NativePlacementTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-placement-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temp.Delete(true);

    [Theory]
    [InlineData("missing")]
    [InlineData("missing/deeper/leaf")]
    public void MissingBoundaryIsNativeAndRevalidatedBeforeUse(string suffix)
    {
        var path = Path.Combine(temp.FullName, suffix.Replace('/', Path.DirectorySeparatorChar));
        using var lease = NativePlacementLease.Open(path, Token);
        Assert.Equal(suffix.Split('/'), lease.Evidence.MissingSuffix);
        Assert.Equal(OwnershipPath.CanonicalCandidate(temp.FullName), lease.Evidence.ExistingAncestor.CanonicalPath);
        lease.Revalidate(Token);
        Directory.CreateDirectory(Path.Combine(temp.FullName, "missing"));
        Assert.Throws<IOException>(() => lease.Revalidate(Token));
    }

    [Fact]
    public void ContentChangesDoNotInventOldSourceFreshnessButIdentityReplacementRefuses()
    {
        var old = Directory.CreateDirectory(Path.Combine(temp.FullName, "old")).FullName;
        using var lease = NativePlacementLease.Open(old, Token);
        Assert.Empty(lease.Evidence.MissingSuffix);
        File.WriteAllText(Path.Combine(old, "ignored-old-content"), "not a historical base");
        lease.Revalidate(Token);
        Directory.Move(old, old + "-moved"); Directory.CreateDirectory(old);
        Assert.Throws<IOException>(() => lease.Revalidate(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AliasedAncestorsResolveAndBrokenOrFinalLinksCannotMasqueradeAsMissing(bool relative)
    {
        if (OperatingSystem.IsWindows()) return;
        var actual = Directory.CreateDirectory(Path.Combine(temp.FullName, "actual")).FullName;
        var alias = Path.Combine(temp.FullName, "alias"); Directory.CreateSymbolicLink(alias, relative ? "actual" : actual);
        var requested = alias + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar + "missing" + Path.DirectorySeparatorChar + "leaf";
        using var lease = NativePlacementLease.Open(requested, Token);
        Assert.Equal(requested, lease.Evidence.Path);
        Assert.Equal(Path.Combine(actual, "missing", "leaf"), lease.Evidence.CanonicalPath);
        lease.Revalidate(Token);
        var other = Directory.CreateDirectory(Path.Combine(temp.FullName, "other")).FullName;
        Directory.Delete(alias); Directory.CreateSymbolicLink(alias, other);
        Assert.Throws<IOException>(() => lease.Revalidate(Token)); // original address, not only the expanded target
        Assert.Throws<IOException>(() => NativePlacementLease.Open(alias, Token));
        var broken = Path.Combine(temp.FullName, "broken"); Directory.CreateSymbolicLink(broken, Path.Combine(temp.FullName, "not-here"));
        Assert.Throws<IOException>(() => NativePlacementLease.Open(broken, Token));
        Assert.Throws<IOException>(() => NativePlacementLease.Open(Path.Combine(broken, "leaf"), Token));
    }

    [Fact]
    public void AliasCyclesAndDotTargetsAreBoundedAndRefuseRatherThanNormalizeAcrossLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        var first = Path.Combine(temp.FullName, "first"); var second = Path.Combine(temp.FullName, "second");
        Directory.CreateSymbolicLink(first, "second"); Directory.CreateSymbolicLink(second, "first");
        Assert.Throws<IOException>(() => NativePlacementLease.Open(Path.Combine(first, "leaf"), Token));
        var dot = Path.Combine(temp.FullName, "dot"); Directory.CreateSymbolicLink(dot, "../different");
        var error = Assert.Throws<IOException>(() => NativePlacementLease.Open(Path.Combine(dot, "leaf"), Token));
        Assert.Contains("dot components", error.Message);
        Assert.Throws<IOException>(() => NativePlacementLease.Open(Path.Combine(temp.FullName, "actual", "..", "leaf"), Token));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativePlacementLease.Open(Path.Combine(first, "leaf"), cancellation.Token));
    }

    [Fact]
    public void RecursiveAliasFailuresAndLeaseDisposalReleaseOwnedNativeAnchors()
    {
        if (!OperatingSystem.IsLinux()) return;
        var first = Path.Combine(temp.FullName, "one"); var second = Path.Combine(temp.FullName, "two");
        Directory.CreateSymbolicLink(first, "two"); Directory.CreateSymbolicLink(second, "one");
        for (var index = 0; index < 3; index++) Assert.Throws<IOException>(() => NativePlacementLease.Open(Path.Combine(first, "leaf"), Token));
        var before = OwnedAnchors();
        using (var held = NativePlacementLease.Open(Path.Combine(temp.FullName, "absent"), Token))
        {
            Assert.True(OwnedAnchors() > before);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => held.Revalidate(cancellation.Token));
        }
        for (var index = 0; index < 20; index++) Assert.Throws<IOException>(() => NativePlacementLease.Open(Path.Combine(first, "leaf"), Token));
        Assert.Equal(before, OwnedAnchors());
        int OwnedAnchors()
        {
            var count = 0;
            foreach (var descriptor in Directory.EnumerateFileSystemEntries("/proc/thread-self/fd").Take(4097))
            {
                Token.ThrowIfCancellationRequested();
                var target = new FileInfo(descriptor).LinkTarget;
                if (target == temp.FullName || target?.StartsWith(temp.FullName + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true) count++;
            }
            return count;
        }
    }

    [Fact]
    public void LinuxMountCrossingCannotBecomeCanonicalExclusionOrMissingEvidence()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Read-only existing procfs mount: no host mount, capability or unshare.
        var existing = Assert.Throws<IOException>(() => NativePlacementLease.Open("/proc/self", Token));
        Assert.Contains("crosses a Linux mount boundary", existing.Message);
        var absent = Assert.Throws<IOException>(() => NativePlacementLease.Open("/proc/skillview-nonexistent-placement/leaf", Token));
        Assert.Contains("crosses a Linux mount boundary", absent.Message);
    }

    [Fact]
    public void InaccessibleParentIsUnknownRatherThanProvenMissing()
    {
        if (OperatingSystem.IsWindows()) return;
        var secret = Directory.CreateDirectory(Path.Combine(temp.FullName, "secret")).FullName;
        File.SetUnixFileMode(secret, UnixFileMode.None);
        try { Assert.Throws<IOException>(() => NativePlacementLease.Open(Path.Combine(secret, "missing"), Token)); }
        finally { File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void NonDirectoryAncestorAndCanceledObservationRefuse()
    {
        var file = Path.Combine(temp.FullName, "file"); File.WriteAllText(file, "x");
        Assert.Throws<IOException>(() => NativePlacementLease.Open(Path.Combine(file, "leaf"), Token));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativePlacementLease.Open(temp.FullName, cancellation.Token));
    }
}
