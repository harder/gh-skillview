using System.Runtime.InteropServices;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class CapturedResolutionArchiveTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-resolution-capture-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Source => Path.Combine(temporary.FullName, "input.txt");
    private string State => Path.Combine(temporary.FullName, "state");
    public void Dispose() => temporary.Delete(true);

    [Fact]
    public void FrozenRegularBytesModeAndOriginalNativeMetadataSurviveInputChangeAndDeletion()
    {
        File.WriteAllBytes(Source, [0, 255, 0, 10]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Source, (UnixFileMode)416);
        using var state = NativeStateDirectory.OpenOrCreate(State, [], Token);
        var hash = CapturedResolutionArchive.Capture(state, "resolutions-00", [new("resource", Source)],
            CapturedResolutionArchive.MaxRetainedBytes, CapturedResolutionArchive.MaxRetainedManifestBytes, Token);
        using var archive = NativeStateDirectory.OpenOrCreate(Path.Combine(State, "resolutions-00"), [], Token, allowCreate: false);
        var capture = CapturedResolutionArchive.Load(archive, hash, Token);
        var saved = Assert.Single(capture.Entries);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(state.CanonicalPath)!, Path.GetFileName(Source)), saved.Metadata!.Identity.CanonicalPath);
        if (!OperatingSystem.IsWindows()) Assert.Equal(416, saved.Entry.UnixMode);
        File.WriteAllText(Source, "edited after capture"); File.Delete(Source);
        using var bytes = new MemoryStream(); capture.OpenReader(archive).CopyEntry("resource", bytes, Token);
        Assert.Equal(new byte[] { 0, 255, 0, 10 }, bytes.ToArray());
        Assert.All(Directory.EnumerateFileSystemEntries(archive.CanonicalPath), path => Assert.False(PathResolver.IsSymlink(path)));
    }

    [Fact]
    public void SkillParsingDependsOnTargetPathRatherThanInputFileName()
    {
        File.WriteAllBytes(Source, [0, 255, 0]);
        using var state = NativeStateDirectory.OpenOrCreate(State, [], Token);
        Assert.ThrowsAny<ArgumentException>(() => CapturedResolutionArchive.Capture(state, "invalid-skill", [new("SKILL.md", Source)],
            CapturedResolutionArchive.MaxRetainedBytes, CapturedResolutionArchive.MaxRetainedManifestBytes, Token));
        var inputDirectory = Directory.CreateDirectory(Path.Combine(temporary.FullName, "inputs"));
        var namedSkill = Path.Combine(inputDirectory.FullName, "SKILL.md"); File.WriteAllBytes(namedSkill, [0, 255, 0]);
        CapturedResolutionArchive.Capture(state, "opaque-resource", [new("binary", namedSkill)],
            CapturedResolutionArchive.MaxRetainedBytes, CapturedResolutionArchive.MaxRetainedManifestBytes, Token);
        File.WriteAllText(Source, "---\nname: demo\ndescription: Resolution\n---\nResolved instructions\n");
        var hash = CapturedResolutionArchive.Capture(state, "valid-skill", [new("SKILL.md", Source)],
            CapturedResolutionArchive.MaxRetainedBytes, CapturedResolutionArchive.MaxRetainedManifestBytes, Token);
        using var archive = NativeStateDirectory.OpenOrCreate(Path.Combine(State, "valid-skill"), [], Token, allowCreate: false);
        var entry = Assert.Single(CapturedResolutionArchive.Load(archive, hash, Token).Entries).Entry;
        Assert.NotEmpty(entry.ComparisonHash); Assert.Equal("SKILL.md", entry.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedInputDuringCaptureOrCorruptFrozenBlobCannotBecomeAUsableResolution(bool corruptBlob)
    {
        File.WriteAllText(Source, "original input");
        using var state = NativeStateDirectory.OpenOrCreate(State, [], Token);
        if (!corruptBlob)
        {
            Assert.Throws<IOException>(() => CapturedResolutionArchive.Capture(state, "changed", [new("file", Source)],
                CapturedResolutionArchive.MaxRetainedBytes, CapturedResolutionArchive.MaxRetainedManifestBytes, Token,
                _ => File.WriteAllText(Source, "foreign change")));
            Assert.False(File.Exists(Path.Combine(State, "changed", "manifest.json")));
        }
        else
        {
            var hash = CapturedResolutionArchive.Capture(state, "frozen", [new("file", Source)],
                CapturedResolutionArchive.MaxRetainedBytes, CapturedResolutionArchive.MaxRetainedManifestBytes, Token);
            using var archive = NativeStateDirectory.OpenOrCreate(Path.Combine(State, "frozen"), [], Token, allowCreate: false);
            var capture = CapturedResolutionArchive.Load(archive, hash, Token);
            var item = Assert.Single(capture.Entries);
            File.WriteAllBytes(Path.Combine(archive.CanonicalPath, item.Blob!), new byte[checked((int)item.StoredBytes)]);
            Assert.Throws<IOException>(() => capture.OpenReader(archive).CopyEntry("file", Stream.Null, Token));
        }
    }

    [Fact]
    public void SymlinkDirectoryAndFifoInputsAreRefusedWithoutFollowingOrBlocking()
    {
        File.WriteAllText(Source, "unchanged");
        var link = Path.Combine(temporary.FullName, "link"); File.CreateSymbolicLink(link, Source);
        using var state = NativeStateDirectory.OpenOrCreate(State, [], Token);
        Assert.Throws<IOException>(() => CapturedResolutionArchive.Capture(state, "link-refusal", [new("file", link)],
            CapturedResolutionArchive.MaxRetainedBytes, CapturedResolutionArchive.MaxRetainedManifestBytes, Token));
        Assert.Throws<IOException>(() => CapturedResolutionArchive.Capture(state, "directory-refusal", [new("file", temporary.FullName)],
            CapturedResolutionArchive.MaxRetainedBytes, CapturedResolutionArchive.MaxRetainedManifestBytes, Token));
        if (!OperatingSystem.IsWindows())
        {
            var fifo = Path.Combine(temporary.FullName, "fifo"); Assert.Equal(0, Native.mkfifo(fifo, 384));
            Assert.Throws<IOException>(() => CapturedResolutionArchive.Capture(state, "fifo-refusal", [new("file", fifo)],
                CapturedResolutionArchive.MaxRetainedBytes, CapturedResolutionArchive.MaxRetainedManifestBytes, Token));
        }
        Assert.Equal("unchanged", File.ReadAllText(Source));
    }

    [Fact]
    public void CancellationAndRemainingStorageBudgetsCloseAdmissionWithoutChangingInput()
    {
        File.WriteAllText(Source, "12345678");
        using var state = NativeStateDirectory.OpenOrCreate(State, [], Token);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CapturedResolutionArchive.Capture(state, "canceled", [new("file", Source)], 64, 64, canceled.Token));
        Assert.False(Directory.Exists(Path.Combine(State, "canceled")));
        Assert.Throws<IOException>(() => CapturedResolutionArchive.Capture(state, "bytes-full", [new("file", Source)], 4, 1024, Token));
        Assert.False(File.Exists(Path.Combine(State, "bytes-full", "entry-00000")));
        Assert.Throws<IOException>(() => CapturedResolutionArchive.Capture(state, "manifest-full", [new("file", Source)], 64, 4, Token));
        Assert.Equal(0, new FileInfo(Path.Combine(State, "manifest-full", "manifest.json")).Length);
        Assert.Equal("12345678", File.ReadAllText(Source));
    }

    private static class Native
    {
        [DllImport("libc", SetLastError = true)] internal static extern int mkfifo(string path, uint mode);
    }
}
