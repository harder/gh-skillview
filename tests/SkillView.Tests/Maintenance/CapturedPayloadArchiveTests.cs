using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class CapturedPayloadArchiveTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-captured-archive-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Target => Path.Combine(temp.FullName, "skills", "demo");
    private string State => Path.Combine(temp.FullName, "state");
    public void Dispose() => temp.Delete(true);

    private NativeDirectoryMoveLease CreateOriginal()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "SKILL.md"), "---\nname: demo\ndescription: Archive fixture\n---\nBody.\n");
        Directory.CreateDirectory(Path.Combine(Target, "empty"));
        File.WriteAllBytes(Path.Combine(Target, "binary"), [0, 255, 0, 13, 10]);
        return NativeDirectoryMoveLease.Open(Path.GetDirectoryName(Target)!, Target, Token);
    }

    [Fact]
    public void RetainsExactIndependentBytesAndEmptyDirectoriesWithoutCreatingPayloadLinks()
    {
        using var original = CreateOriginal();
        File.CreateSymbolicLink(Path.Combine(Target, "dangling"), "absent-target");
        using var state = NativeStateDirectory.OpenOrCreate(State, [Target], Token);
        var capture = CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        capture.Validate();
        Assert.Equal(original.CaptureInto((_, _) => { }, null, Token).Fingerprint, capture.Snapshot.Fingerprint);
        Assert.Contains(capture.Entries, item => item.Entry.Path == "empty" && item.Blob is null);
        var archivePath = Path.Combine(State, "installed-evidence");
        Assert.All(Directory.EnumerateFileSystemEntries(archivePath), path =>
        {
            Assert.False(PathResolver.IsSymlink(path)); Assert.True(File.Exists(path));
        });
        // A later edit cannot change the saved bytes. Apply must separately
        // refuse a stale original; retaining a draft never mutates that original.
        File.WriteAllBytes(Path.Combine(Target, "binary"), [1, 2, 3]);
        using var archive = NativeStateDirectory.OpenOrCreate(archivePath, [Target], Token, allowCreate: false);
        using var bytes = new MemoryStream();
        capture.CopyEntry(archive, "binary", bytes, Token);
        Assert.Equal(new byte[] { 0, 255, 0, 13, 10 }, bytes.ToArray());
        using var link = new MemoryStream();
        capture.CopyEntry(archive, "dangling", link, Token);
        Assert.NotEmpty(link.ToArray());
        Assert.Equal("link", capture.Entries.Single(item => item.Entry.Path == "dangling").Entry.Kind);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(archivePath));
    }

    [Fact]
    public void TamperedBlobAndManifestAreRefused()
    {
        using var original = CreateOriginal();
        using var state = NativeStateDirectory.OpenOrCreate(State, [Target], Token);
        var capture = CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        var entry = capture.Entries.Single(item => item.Entry.Path == "binary");
        var archivePath = Path.Combine(State, "installed-evidence");
        File.WriteAllBytes(Path.Combine(archivePath, entry.Blob!), [0, 255, 0, 13, 11]);
        using var archive = NativeStateDirectory.OpenOrCreate(archivePath, [Target], Token, allowCreate: false);
        using var bytes = new MemoryStream();
        Assert.Throws<IOException>(() => capture.CopyEntry(archive, "binary", bytes, Token));
        var changed = capture with { Entries = capture.Entries.SetItem(capture.Entries.IndexOf(entry), entry with { Blob = "../escape" }) };
        Assert.Throws<IOException>(changed.Validate);
        Assert.Throws<IOException>(() => (capture with { Entries = capture.Entries.Add(entry) }).Validate());
    }

    [Fact]
    public void CanceledOrFailedCaptureReturnsNoArchiveAndLeavesOriginalUntouched()
    {
        using var original = CreateOriginal();
        var before = original.Capture(Token);
        using var state = NativeStateDirectory.OpenOrCreate(State, [Target], Token);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CapturedPayloadArchive.Capture(state, "never-created", original, canceled.Token));
        Assert.False(Directory.Exists(Path.Combine(State, "never-created")));
        Assert.Throws<IOException>(() => CapturedPayloadArchive.Capture(state, "failed-evidence", original, Token,
            item => { if (item.RelativePath == "binary") throw new IOException("Metadata policy refusal"); }));
        Assert.Equal(before.Fingerprint, original.Capture(Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, original.Capture(Token).ObjectStateFingerprint);
        Assert.True(Directory.Exists(Path.Combine(State, "failed-evidence")));
    }
}
