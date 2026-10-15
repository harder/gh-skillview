using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class PayloadSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillview-payload-" + Guid.NewGuid().ToString("N"));
    public PayloadSnapshotTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Normalize_RemovesOnlyInstallerKeys_PreservesAuthoredMetadataAndBody()
    {
        const string source = "---\nname: demo\ndescription: Demo\nmetadata:\n  github-ref: main\n  version: '1.0'\n  github-tree-sha: abc\ncompatibility: Linux\n---\nRun scripts/demo.py\n";
        var normalized = InstallerMetadataNormalizer.Normalize(source);
        Assert.Equal("---\nname: demo\ndescription: Demo\nmetadata:\n  version: '1.0'\ncompatibility: Linux\n---\nRun scripts/demo.py\n", normalized);
    }

    [Fact]
    public void Normalize_RemovesTrackingOnlyMappingWithoutRemovingNextAuthoredField()
    {
        var normalized = InstallerMetadataNormalizer.Normalize("---\nname: demo\nmetadata:\n  github-ref: main\ndescription: Demo\n---\nBody\n");
        Assert.Equal("---\nname: demo\ndescription: Demo\n---\nBody\n", normalized);
    }

    [Theory]
    [InlineData("# KEEP COMMENT\ncompatibility: Linux\n")]
    [InlineData("# KEEP COMMENT\n")]
    public void Normalize_PreservesStandaloneCommentsAfterTrackingMap(string suffix)
    {
        var text = "---\nname: demo\nmetadata:\n  github-ref: main\n" + suffix + "---\nBody\n";
        Assert.Equal("---\nname: demo\n" + suffix + "---\nBody\n", InstallerMetadataNormalizer.Normalize(text));
    }

    [Fact]
    public void Snapshot_ScriptExtraAndDeletionRemainVisibleEvenWithSameMarkdown()
    {
        WriteSkill();
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        var script = Path.Combine(_root, "scripts", "run.py");
        File.WriteAllText(script, "old");
        var before = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        File.WriteAllText(script, "new");
        File.WriteAllText(Path.Combine(_root, "extra.txt"), "custom");
        var after = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "extra.txt", "scripts/run.py" }, PayloadSnapshot.Differences(before, after));
        File.Delete(script);
        var deleted = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        Assert.Contains("scripts/run.py", PayloadSnapshot.Differences(after, deleted));
    }

    [Fact]
    public void Snapshot_InstallerRefAndPinIgnoredOnlyForNormalizedComparison()
    {
        WriteSkill("metadata:\n  github-ref: v1\n  github-pinned: v1\n");
        var before = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        WriteSkill("metadata:\n  github-ref: v2\n  github-pinned: v2\n");
        var after = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        Assert.Equal(before.ComparisonFingerprint, after.ComparisonFingerprint);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.Contains("SKILL.md", PayloadSnapshot.Differences(before, after, false));
    }

    [Fact]
    public void PresentationFingerprintIncludesEveryOtherEntryAndItsMode()
    {
        WriteSkill();
        var before = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        Assert.NotNull(before.GhPresentationFingerprint);
        var extra = Path.Combine(_root, "extra");
        File.WriteAllText(extra, "extra");
        var added = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        Assert.NotEqual(before.GhPresentationFingerprint, added.GhPresentationFingerprint);
        File.WriteAllText(extra, "changed");
        var changed = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        Assert.NotEqual(added.GhPresentationFingerprint, changed.GhPresentationFingerprint);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(extra, (UnixFileMode)448);
            Assert.NotEqual(changed.GhPresentationFingerprint, PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken).GhPresentationFingerprint);
        }
        File.Delete(extra);
        Assert.Equal(before.GhPresentationFingerprint, PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken).GhPresentationFingerprint);
    }

    [Fact]
    public void Snapshot_DirectoryLinkIsALeafAndRetargetingIsADifference()
    {
        if (OperatingSystem.IsWindows()) return;
        WriteSkill();
        var external = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(external, "keep.txt"), "keep");
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, external);
        var before = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(before.Entries, e => e.Path.StartsWith("link/", StringComparison.Ordinal));
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, "missing");
        var after = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "link" }, PayloadSnapshot.Differences(before, after));
    }

    [Fact]
    public void Snapshot_CancellationClosesAdmission()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PayloadSnapshot.Capture("missing", cancellation.Token));
    }

    [Fact]
    public void Snapshot_AncestorExchangeDoesNotReadReplacementTree()
    {
        WriteSkill();
        var nested = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "own.txt"), "own");
        var outside = Directory.CreateTempSubdirectory("skillview-outside-snapshot-");
        try
        {
            File.WriteAllText(Path.Combine(outside.FullName, "secret.txt"), "secret");
            var observed = new List<string>();
            Assert.Throws<IOException>(() => PayloadSnapshot.CaptureWithObservationForTests(_root, (path, kind) =>
            {
                observed.Add(path);
                if (path == "nested" && kind == "directory")
                {
                    Directory.Move(nested, nested + "-moved");
                    Directory.CreateSymbolicLink(nested, outside.FullName);
                }
            }, TestContext.Current.CancellationToken));
            Assert.DoesNotContain("nested/secret.txt", observed);
            Assert.Equal("secret", File.ReadAllText(Path.Combine(outside.FullName, "secret.txt")));
        }
        finally { outside.Delete(true); }
    }

    [Fact]
    public void Snapshot_SameLengthWriteAfterRead_RefusesChangedGeneration()
    {
        WriteSkill();
        var file = Path.Combine(_root, "data.txt");
        File.WriteAllText(file, "before");
        Assert.Throws<IOException>(() => PayloadSnapshot.CaptureWithObservationForTests(_root, (path, kind) =>
        {
            if (path == "data.txt" && kind == "file") File.WriteAllText(file, "after!");
        }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Snapshot_LaterEntryRewritesAlreadyReadFile_Refuses()
    {
        WriteSkill();
        var first = Path.Combine(_root, "first.txt");
        var second = Path.Combine(_root, "second.txt");
        File.WriteAllText(first, "before");
        File.WriteAllText(second, "second");
        string? alreadyRead = null;
        Assert.Throws<IOException>(() => PayloadSnapshot.CaptureWithObservationForTests(_root, (path, kind) =>
        {
            if (kind != "file" || path == "SKILL.md") return;
            if (alreadyRead is null) alreadyRead = path;
            else File.WriteAllText(Path.Combine(_root, alreadyRead), "after!");
        }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OwnedStagingLease_ReplacementIsRefused()
    {
        var owned = Directory.CreateDirectory(Path.Combine(_root, "owned")).FullName;
        using var lease = SkillView.Inventory.OwnedDirectoryLease.Open(owned);
        Directory.Move(owned, owned + "-moved");
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, "keep"), "unrelated");
        Assert.Throws<IOException>(() => lease.Capture(TestContext.Current.CancellationToken));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(owned, "keep")));
    }

    [Fact]
    public void NativeObjectState_IsStableAcrossReadOnlyCaptures()
    {
        WriteSkill();
        var first = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        var second = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        Assert.NotNull(first.ObjectStateFingerprint);
        Assert.Equal(64, first.ObjectStateFingerprint.Length);
        Assert.Equal(first.ObjectStateFingerprint, second.ObjectStateFingerprint);
        Assert.Equal(first.RootIdentity, second.RootIdentity);
        Assert.Equal(first.Entries.Length, first.Objects.Length);
        Assert.All(first.Objects, item => Assert.NotEqual(0UL, item.Identity.FileIdLow | item.Identity.FileIdHigh));
    }

    [Fact]
    public void SameBytesInSeparateCopies_DoNotImplySameNativeObjects()
    {
        WriteSkill();
        var other = Directory.CreateTempSubdirectory("skillview-object-copy-");
        try
        {
            File.Copy(Path.Combine(_root, "SKILL.md"), Path.Combine(other.FullName, "SKILL.md"));
            var first = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
            var second = PayloadSnapshot.Capture(other.FullName, TestContext.Current.CancellationToken);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.NotEqual(first.ObjectStateFingerprint, second.ObjectStateFingerprint);
        }
        finally { other.Delete(true); }
    }

    [Fact]
    public void SameContentFileReplacement_ChangesNativeObjectState()
    {
        WriteSkill();
        var file = Path.Combine(_root, "resource");
        File.WriteAllText(file, "same bytes");
        var first = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        var replacement = Path.Combine(_root, "replacement");
        File.WriteAllText(replacement, "same bytes");
        File.Move(replacement, file, overwrite: true);
        var second = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.ObjectStateFingerprint, second.ObjectStateFingerprint);
        Assert.NotEqual(Assert.Single(first.Objects, item => item.Path == "resource").Identity,
            Assert.Single(second.Objects, item => item.Path == "resource").Identity);
    }

    [Fact]
    public void UnixRootMode_IsObservedSeparatelyFromArchivePayloadHash()
    {
        if (OperatingSystem.IsWindows()) return;
        WriteSkill();
        var original = File.GetUnixFileMode(_root);
        try
        {
            var first = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
            File.SetUnixFileMode(_root, original ^ UnixFileMode.OtherExecute);
            var second = PayloadSnapshot.Capture(_root, TestContext.Current.CancellationToken);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.NotEqual(first.RootUnixMode, second.RootUnixMode);
            Assert.NotEqual(first.ObjectStateFingerprint, second.ObjectStateFingerprint);
        }
        finally { File.SetUnixFileMode(_root, original); }
    }

    private void WriteSkill(string metadata = "") => File.WriteAllText(Path.Combine(_root, "SKILL.md"),
        "---\nname: demo\ndescription: Demo\n" + metadata + "---\nBody\n");
}
