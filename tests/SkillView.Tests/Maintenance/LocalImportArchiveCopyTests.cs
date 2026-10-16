using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class LocalImportArchiveCopyTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-local-archive-copy-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temp.Delete(true);

    [Fact]
    public void HeldCopyRetainsExactManifestAndAllBlobBytesAtNewStateAddress()
    {
        var sourcePath = MakeSource();
        using var lease = NativeDirectoryMoveLease.Open(temp.FullName, sourcePath, Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [sourcePath], Token);
        _ = CapturedPayloadArchive.Capture(state, "source", lease, Token);
        using var source = Open(state, "source");
        var before = CapturedPayloadArchive.Load(source, null, Token);
        CapturedPayloadArchive.CopyInto(source, state, "copy", before.Fingerprint, Token);
        using var copy = Open(state, "copy");
        var after = CapturedPayloadArchive.Load(copy, before.Fingerprint, Token);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(sourcePath, after.Archive.Snapshot.Path);
        Assert.Equal(before.Archive.Snapshot.RootIdentity, after.Archive.Snapshot.RootIdentity);
        foreach (var name in source.EnumerateEntries(Token).Select(entry => entry.Name))
        {
            using var left = source.OpenExistingFile(name, false, Token);
            using var right = copy.OpenExistingFile(name, false, Token);
            using var a = new MemoryStream(); left.CopyTo(a);
            using var b = new MemoryStream(); right.CopyTo(b);
            Assert.Equal(a.ToArray(), b.ToArray());
        }
    }

    [Fact]
    public void ChangedSameLengthBlobRefusesAndLeavesPartialCopyAsPrivateEvidence()
    {
        var sourcePath = MakeSource();
        using var lease = NativeDirectoryMoveLease.Open(temp.FullName, sourcePath, Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [sourcePath], Token);
        _ = CapturedPayloadArchive.Capture(state, "source", lease, Token);
        using var source = Open(state, "source");
        var captured = CapturedPayloadArchive.Load(source, null, Token);
        var blob = captured.Archive.Entries.First(entry => entry.Entry.Path == "extra").Blob!;
        File.WriteAllBytes(Path.Combine(source.CanonicalPath, blob), [3, 2, 1]);
        Assert.Throws<IOException>(() => CapturedPayloadArchive.CopyInto(source, state, "copy", captured.Fingerprint, Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(sourcePath, "extra")));
    }

    [Fact]
    public void WrongManifestFingerprintAndPreCancellationCreateNoDestination()
    {
        var sourcePath = MakeSource();
        using var lease = NativeDirectoryMoveLease.Open(temp.FullName, sourcePath, Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [sourcePath], Token);
        _ = CapturedPayloadArchive.Capture(state, "source", lease, Token);
        using var source = Open(state, "source");
        Assert.Throws<IOException>(() => CapturedPayloadArchive.CopyInto(source, state, "wrong", new('f', 64), Token));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var fingerprint = CapturedPayloadArchive.Load(source, null, Token).Fingerprint;
        Assert.ThrowsAny<OperationCanceledException>(() => CapturedPayloadArchive.CopyInto(source, state, "canceled", fingerprint, canceled.Token));
        Assert.DoesNotContain(state.EnumerateEntries(Token), entry => entry.Name is "wrong" or "canceled");
    }

    [Theory]
    [InlineData("added")]
    [InlineData("deleted")]
    [InlineData("rewritten")]
    public void NestedSourceChangesRefuseWithHandledIoError(string change)
    {
        var sourcePath = MakeSource();
        var nested = Directory.CreateDirectory(Path.Combine(sourcePath, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "kept"), "before");
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [sourcePath], Token);
        CapturedPayloadArchive captured;
        using (var initial = NativeDirectoryMoveLease.Open(temp.FullName, sourcePath, Token))
            captured = CapturedPayloadArchive.Capture(state, "source", initial, Token);
        if (change == "added") File.WriteAllText(Path.Combine(nested, "new-child"), "new");
        else if (change == "deleted") File.Delete(Path.Combine(nested, "kept"));
        else File.WriteAllText(Path.Combine(nested, "kept"), "after!");
        using var current = NativeDirectoryMoveLease.Open(temp.FullName, sourcePath, Token);
        // Ordinary Linux may already refuse unknown trusted-xattr coverage.
        // Every admitted host must also translate added paths to IOException,
        // never leak KeyNotFoundException from the held-walk callback.
        Assert.Throws<IOException>(() => ReplacementPlanStore.VerifyLocalSourceCapture(current, captured, state, Token));
        Assert.Equal(change == "deleted" ? false : true, File.Exists(Path.Combine(nested, "kept")));
    }

    private string MakeSource()
    {
        var path = Directory.CreateDirectory(Path.Combine(temp.FullName, "source")).FullName;
        File.WriteAllText(Path.Combine(path, "SKILL.md"), "---\nname: local\ndescription: copy\n---\nbody\n");
        File.WriteAllBytes(Path.Combine(path, "extra"), [1, 2, 3]);
        return path;
    }

    private NativeStateDirectory Open(NativeStateDirectory state, string name) =>
        NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, name), [], Token, allowCreate: false);
}
