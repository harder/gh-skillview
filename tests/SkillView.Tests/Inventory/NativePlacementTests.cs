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
        Assert.True(SecureRemovalBackend.TryCaptureIdentity(actual, out var actualIdentity, out var identityError), identityError);
        using var lease = NativePlacementLease.Open(requested, Token);
        Assert.Equal(requested, lease.Evidence.Path);
        Assert.Equal(Path.Combine(actualIdentity.CanonicalPath, "missing", "leaf"), lease.Evidence.CanonicalPath);
        Assert.Equal(actualIdentity, lease.Evidence.ExistingAncestor);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsInitialCaptureRestartsFromFreshRootAfterParentNamespaceChanges(bool missing)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows held-parent generation checks require native Windows execution."); return; }
        var parent = Directory.CreateDirectory(Path.Combine(temp.FullName, "parent")).FullName;
        var child = Path.Combine(parent, "child");
        if (!missing) Directory.CreateDirectory(child);
        var creation = Directory.GetCreationTimeUtc(parent);
        var observedAttempts = new List<int>();
        using var lease = WindowsSecureRemovalBackend.OpenPlacement(child, Token, (attempt, capturedParent, name) =>
        {
            if (name != "child" || !PathIdentity.Equals(capturedParent, parent)) return;
            observedAttempts.Add(attempt);
            if (observedAttempts.Count == 1)
            {
                File.WriteAllText(Path.Combine(parent, "namespace-change"), "changed");
                // Namespace timestamps can coalesce on Windows. Force a distinct
                // real native generation instead of depending on clock progress.
                Directory.SetCreationTimeUtc(parent, creation.AddSeconds(1));
                Assert.NotEqual(creation, Directory.GetCreationTimeUtc(parent));
            }
        });
        Assert.Equal(2, observedAttempts.Count);
        Assert.True(observedAttempts[1] > observedAttempts[0]);
        Assert.Equal(missing ? new[] { "child" } : [], lease.Evidence.MissingSuffix);
        lease.Revalidate(Token);
        // A completed lease is still stale after a real target/absence change.
        if (missing) Directory.CreateDirectory(child);
        else { Directory.Move(child, child + "-retained"); Directory.CreateDirectory(child); }
        Assert.Throws<IOException>(() => lease.Revalidate(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsContinuousParentChurnIsBoundedAndCancellationStopsRestart(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows held-parent generation checks require native Windows execution."); return; }
        var parent = Directory.CreateDirectory(Path.Combine(temp.FullName, "parent")).FullName;
        var child = Path.Combine(parent, "missing");
        var creation = Directory.GetCreationTimeUtc(parent);
        using var cancellation = new CancellationTokenSource();
        var changes = 0;
        var attempts = new HashSet<int>();
        void Change(int attempt, string capturedParent, string name)
        {
            attempts.Add(attempt);
            if (name != "missing" || !PathIdentity.Equals(capturedParent, parent)) return;
            File.WriteAllText(Path.Combine(parent, "namespace-change-" + changes++), "changed");
            Directory.SetCreationTimeUtc(parent, creation.AddSeconds(changes));
            Assert.Equal(creation.AddSeconds(changes), Directory.GetCreationTimeUtc(parent));
            if (cancel) cancellation.Cancel();
        }
        if (cancel)
        {
            var error = Assert.Throws<OperationCanceledException>(() => WindowsSecureRemovalBackend.OpenPlacement(child, cancellation.Token, Change));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(1, changes);
        }
        else
        {
            var error = Assert.ThrowsAny<IOException>(() => WindowsSecureRemovalBackend.OpenPlacement(child, Token, Change));
            Assert.Contains("parent changed", error.Message);
            Assert.InRange(changes, 1, WindowsSecureRemovalBackend.PlacementCaptureAttempts);
            Assert.Equal(Enumerable.Range(0, WindowsSecureRemovalBackend.PlacementCaptureAttempts), attempts.Order());
        }
    }
}
