using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class PayloadNativeMetadataTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-native-metadata-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Target => Path.Combine(temp.FullName, "skills", "demo");
    public void Dispose() => temp.Delete(true);
    private NativeDirectoryMoveLease CreateOriginal()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "SKILL.md"), "---\nname: demo\ndescription: Metadata fixture\nmetadata:\n  github-repo: https://github.com/fixture/source\n  github-ref: refs/heads/main\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n  github-path: skills/demo\n---\nBody.\n");
        File.WriteAllText(Path.Combine(Target, "extra"), "Local content.");
        return NativeDirectoryMoveLease.Open(Path.GetDirectoryName(Target)!, Target, Token);
    }

    [Fact]
    public void MetadataIsBoundToTheCapturedNativeObjectsAndUnknownCoverageIsExplicit()
    {
        using var original = CreateOriginal();
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [Target], Token);
        var archive = CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        archive.Validate();
        Assert.Equal(archive.Snapshot.RootIdentity, archive.RootMetadata!.Identity);
        foreach (var entry in archive.Entries)
            Assert.Equal(archive.Snapshot.Objects.Single(item => item.Path == entry.Entry.Path).Identity, entry.Metadata!.Identity);
        if (OperatingSystem.IsWindows())
        {
            if (archive.RootMetadata.Supported)
            {
                Assert.Equal("windows-full-security-and-backup-streams-v1", archive.RootMetadata.EnumerationCoverage);
                Assert.NotNull(archive.RootMetadata.Windows);
            }
            else Assert.NotEmpty(archive.RootMetadata.Refusals);
        }
        else if (OperatingSystem.IsLinux() && archive.RootMetadata.EnumerationCoverage == "caller-visible-xattr-names")
        {
            Assert.False(archive.RootMetadata.Supported);
            Assert.Contains(archive.RootMetadata.Refusals, refusal => refusal.Contains("may be hidden", StringComparison.Ordinal));
        }
        var first = archive.Entries[0];
        var forged = archive with { Entries = archive.Entries.SetItem(0, first with { Metadata = first.Metadata! with { Identity = archive.RootMetadata.Identity } }) };
        Assert.Throws<IOException>(forged.Validate);
    }

    [Fact]
    public void MacMergePreservesBinaryAndEmptyExtendedAttributesOnFilesAndRoot()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var original = CreateOriginal();
        SetAttribute(Target, "com.harder.skillview.root", [7, 0, 255]);
        SetAttribute(Path.Combine(Target, "extra"), "com.harder.skillview.binary", [0, 255, 0, 9]);
        SetAttribute(Path.Combine(Target, "extra"), "com.harder.skillview.empty", []);
        File.SetUnixFileMode(Path.Combine(Target, "extra"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [Target], Token);
        var archive = CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        Assert.True(archive.RootMetadata!.Supported, string.Join(" ", archive.RootMetadata.Refusals));
        Assert.All(archive.Entries, entry => Assert.True(entry.Metadata!.Supported, string.Join(" ", entry.Metadata.Refusals)));
        using var files = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "installed-evidence"), [Target], Token, allowCreate: false);
        var before = original.Capture(Token);
        var merged = ReviewedPayloadMaterializer.StagePreservingMetadata(state, "merged", ThreeWayPayloadReview.Analyze(archive.Snapshot, archive.Snapshot, archive.Snapshot),
            new Dictionary<string, CapturedPayloadArchive.Reader> { ["installed"] = archive.OpenReader(files) }, archive.Snapshot.FrontMatter!, Token);
        Assert.Contains("unsupported/unknown metadata refused", merged.MetadataPolicy);
        using var candidate = NativeDirectoryMoveLease.Open(state.CanonicalPath, merged.Snapshot.Path, Token);
        var copied = CapturedPayloadArchive.Capture(state, "merged-evidence", candidate, Token);
        Assert.Equal(new byte[] { 7, 0, 255 }, copied.RootMetadata!.Attributes.Single(item => item.Name == "com.harder.skillview.root").Value);
        var attributes = copied.Entries.Single(entry => entry.Entry.Path == "extra").Metadata!.Attributes;
        Assert.Equal(new byte[] { 0, 255, 0, 9 }, attributes.Single(item => item.Name == "com.harder.skillview.binary").Value);
        Assert.Empty(attributes.Single(item => item.Name == "com.harder.skillview.empty").Value);
        Assert.Equal(before.ObjectStateFingerprint, original.Capture(Token).ObjectStateFingerprint);
    }

    [Fact]
    public void MacLinkMetadataNeverTouchesItsExternalTarget()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var original = CreateOriginal();
        var outside = Path.Combine(temp.FullName, "outside"); Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "target"); File.WriteAllText(file, "External bytes.");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        SetAttribute(file, "com.harder.skillview.test", [8]);
        var link = Path.Combine(Target, "external"); File.CreateSymbolicLink(link, file);
        SetAttribute(link, "com.harder.skillview.test", [1, 0, 2], noFollow: true);
        var before = PayloadSnapshot.CaptureEvidence(outside, Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [Target], Token);
        var archive = CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        var metadata = archive.Entries.Single(entry => entry.Entry.Path == "external").Metadata!;
        Assert.True(metadata.Supported, string.Join(" ", metadata.Refusals));
        Assert.Equal(new byte[] { 1, 0, 2 }, metadata.Attributes.Single(item => item.Name == "com.harder.skillview.test").Value);
        using var files = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "installed-evidence"), [Target], Token, allowCreate: false);
        var merged = ReviewedPayloadMaterializer.StagePreservingMetadata(state, "merged", ThreeWayPayloadReview.Analyze(archive.Snapshot, archive.Snapshot, archive.Snapshot),
            new Dictionary<string, CapturedPayloadArchive.Reader> { ["installed"] = archive.OpenReader(files) }, archive.Snapshot.FrontMatter!, Token);
        using var candidate = NativeDirectoryMoveLease.Open(state.CanonicalPath, merged.Snapshot.Path, Token);
        var copied = CapturedPayloadArchive.Capture(state, "merged-evidence", candidate, Token);
        Assert.Equal(new byte[] { 1, 0, 2 }, copied.Entries.Single(entry => entry.Entry.Path == "external").Metadata!.Attributes.Single(item => item.Name == "com.harder.skillview.test").Value);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.CaptureEvidence(outside, Token).ObjectStateFingerprint);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void HardLinkedSourceIsAnExplicitRefusalBeforeDraftCreation()
    {
        if (OperatingSystem.IsWindows()) return;
        using var original = CreateOriginal();
        Assert.Equal(0, Native.link(Path.Combine(Target, "extra"), Path.Combine(temp.FullName, "external-alias")));
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [Target], Token);
        var archive = CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        var metadata = archive.Entries.Single(entry => entry.Entry.Path == "extra").Metadata!;
        Assert.Equal(2UL, metadata.LinkCount); Assert.False(metadata.Supported);
        Assert.Contains(metadata.Refusals, refusal => refusal.Contains("Hard-linked", StringComparison.Ordinal));
        using var files = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "installed-evidence"), [Target], Token, allowCreate: false);
        Assert.Throws<IOException>(() => ReviewedPayloadMaterializer.StagePreservingMetadata(state, "not-created", ThreeWayPayloadReview.Analyze(archive.Snapshot, archive.Snapshot, archive.Snapshot),
            new Dictionary<string, CapturedPayloadArchive.Reader> { ["installed"] = archive.OpenReader(files) }, archive.Snapshot.FrontMatter!, Token));
        Assert.False(Directory.Exists(Path.Combine(state.CanonicalPath, "not-created")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisposedOrHardLinkedDestinationIsRefusedBeforeMetadataMutation(bool disposed)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var original = CreateOriginal();
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [Target], Token);
        var archive = CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        var metadata = archive.Entries.Single(entry => entry.Entry.Path == "extra").Metadata!;
        using var target = state.OpenFile("destination", false, Token);
        var handle = target.SafeFileHandle;
        if (disposed) handle.Dispose();
        else Assert.Equal(0, Native.link(Path.Combine(state.CanonicalPath, "destination"), Path.Combine(temp.FullName, "outside-alias")));
        var before = PayloadSnapshot.CaptureEvidence(state.CanonicalPath, Token);
        Assert.Throws<IOException>(() => PayloadNativeMetadata.Apply(handle, metadata, 511, Token));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.CaptureEvidence(state.CanonicalPath, Token).ObjectStateFingerprint);
    }

    [Fact]
    public void FifoCannotBeTreatedAsARegularMetadataDestination()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var original = CreateOriginal();
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [Target], Token);
        var archive = CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        var metadata = archive.Entries.Single(entry => entry.Entry.Path == "extra").Metadata!;
        var fifo = Path.Combine(temp.FullName, "fifo"); Assert.Equal(0, Native.mkfifo(fifo, 384));
        using var handle = new SafeFileHandle((IntPtr)Native.open(fifo, 2 | 4), true);
        Assert.False(handle.IsInvalid);
        Assert.Throws<IOException>(() => PayloadNativeMetadata.Apply(handle, metadata, 511, Token));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fifo));
    }

    [Fact]
    public async Task NonemptyMacAclRemainsExplicitlyUnsupportedAndOriginalIsUntouched()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var original = CreateOriginal();
        using var command = Process.Start(new ProcessStartInfo("/bin/chmod")
        {
            UseShellExecute = false,
            ArgumentList = { "+a", "everyone allow read", Path.Combine(Target, "extra") }
        })!;
        await command.WaitForExitAsync(Token); Assert.Equal(0, command.ExitCode);
        var before = original.Capture(Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [Target], Token);
        var archive = CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        var metadata = archive.Entries.Single(entry => entry.Entry.Path == "extra").Metadata!;
        Assert.False(metadata.Supported); Assert.Contains(metadata.Refusals, refusal => refusal.Contains("ACL", StringComparison.Ordinal));
        using var files = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "installed-evidence"), [Target], Token, allowCreate: false);
        Assert.Throws<IOException>(() => ReviewedPayloadMaterializer.StagePreservingMetadata(state, "not-created", ThreeWayPayloadReview.Analyze(archive.Snapshot, archive.Snapshot, archive.Snapshot),
            new Dictionary<string, CapturedPayloadArchive.Reader> { ["installed"] = archive.OpenReader(files) }, archive.Snapshot.FrontMatter!, Token));
        Assert.False(Directory.Exists(Path.Combine(state.CanonicalPath, "not-created")));
        Assert.Equal(before.ObjectStateFingerprint, original.Capture(Token).ObjectStateFingerprint);
    }

    private static void SetAttribute(string path, string name, byte[] bytes, bool noFollow = false) =>
        Assert.Equal(0, Native.setxattr(path, name, bytes, (nuint)bytes.Length, 0, noFollow ? 1 : 0));

    [Fact]
    public void LinuxWitnessIsAnonymousUnlinkableAndAnInstalledHandleCannotBeUsedAsAProbe()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var original = CreateOriginal();
        var before = original.Capture(Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [Target], Token);
        using var witness = state.CreateAnonymousMetadataWitness(Token);
        var linked = Path.Combine(temp.FullName, "must-not-link");
        Assert.NotEqual(0, Native.linkat(witness.SafeFileHandle, "", -100, linked, 0x1000));
        Assert.False(File.Exists(linked));
        Assert.Empty(state.EnumerateEntries(Token));
        using var installed = File.OpenRead(Path.Combine(Target, "extra"));
        Assert.False(UnixSecureRemovalBackend.ProbeTrustedMetadataAuthority(installed.SafeFileHandle, Token));
        Assert.Equal(before.ObjectStateFingerprint, original.Capture(Token).ObjectStateFingerprint);
    }

    private static class Native
    {
        [DllImport("libc", SetLastError = true)] internal static extern int link(string source, string destination);
        [DllImport("libc", SetLastError = true)] internal static extern int linkat(Microsoft.Win32.SafeHandles.SafeFileHandle descriptor, string source, int destinationDirectory, string destination, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int mkfifo(string path, uint mode);
        [DllImport("libc", SetLastError = true)] internal static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int setxattr(string path, string name, byte[] value, nuint bytes, uint position, int options);
    }
}
