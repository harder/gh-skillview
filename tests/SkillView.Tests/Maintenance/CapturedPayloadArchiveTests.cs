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
    public void PersistedManifestRestoresNativeEvidenceAndIndependentBytesAfterRestart()
    {
        using var original = CreateOriginal();
        using var state = NativeStateDirectory.OpenOrCreate(State, [Target], Token);
        var capture = CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        using var archive = NativeStateDirectory.OpenOrCreate(Path.Combine(State, "installed-evidence"), [Target], Token, allowCreate: false);
        var loaded = CapturedPayloadArchive.Load(archive, null, Token);
        Assert.Equal(capture.Snapshot.RootIdentity, loaded.Archive.Snapshot.RootIdentity);
        Assert.Equal(capture.Snapshot.Objects, loaded.Archive.Snapshot.Objects);
        Assert.Equal(capture.Snapshot.ObjectStateFingerprint, loaded.Archive.Snapshot.ObjectStateFingerprint);
        Assert.Equal(capture.Snapshot.ComparisonFingerprint, loaded.Archive.Snapshot.ComparisonFingerprint);
        File.WriteAllBytes(Path.Combine(Target, "binary"), [1]);
        using var saved = new MemoryStream();
        CapturedPayloadArchive.Load(archive, loaded.Fingerprint, Token).Archive.CopyEntry(archive, "binary", saved, Token);
        Assert.Equal(new byte[] { 0, 255, 0, 13, 10 }, saved.ToArray());
        Assert.Throws<IOException>(() => CapturedPayloadArchive.Load(archive, new('0', 64), Token));
    }

    [Fact]
    public void PersistedManifestRejectsDuplicatePropertiesUnexpectedFilesAndNativeEvidenceChanges()
    {
        using var original = CreateOriginal();
        using var state = NativeStateDirectory.OpenOrCreate(State, [Target], Token);
        CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        var path = Path.Combine(State, "installed-evidence");
        using var archive = NativeStateDirectory.OpenOrCreate(path, [Target], Token, allowCreate: false);
        var manifest = Path.Combine(path, "manifest.json"); var bytes = File.ReadAllText(manifest);
        File.WriteAllText(manifest, bytes.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1"));
        Assert.Throws<IOException>(() => CapturedPayloadArchive.Load(archive, null, Token));
        File.WriteAllText(manifest, bytes.Replace("\"objects\": \"", "\"objects\": \"0"));
        Assert.Throws<IOException>(() => CapturedPayloadArchive.Load(archive, null, Token));
        File.WriteAllText(manifest, bytes);
        File.WriteAllText(Path.Combine(path, "unreviewed"), "extra");
        Assert.Throws<IOException>(() => CapturedPayloadArchive.Load(archive, null, Token));
    }

    [Fact]
    public void ManifestReadClosesBeforeEnumerationAndDisappearanceStillRefusesLoad()
    {
        using var original = CreateOriginal();
        using var state = NativeStateDirectory.OpenOrCreate(State, [Target], Token);
        CapturedPayloadArchive.Capture(state, "installed-evidence", original, Token);
        var path = Path.Combine(State, "installed-evidence");
        using var archive = NativeStateDirectory.OpenOrCreate(path, [Target], Token, allowCreate: false);
        using var intercepted = new BeforeEnumerationState(archive, () => File.Delete(Path.Combine(path, "manifest.json")));
        var error = Assert.Throws<IOException>(() => CapturedPayloadArchive.Load(intercepted, null, Token));
        Assert.Contains("Missing: manifest.json", error.Message);
    }

    [Fact]
    public void ArchiveEntryFailureDetailsRetainExactSizesAndBoundUntrustedMessages()
    {
        var expected = Enumerable.Range(0, PayloadSnapshot.MaxEntries)
            .ToDictionary(index => $"entry-{index:D5}", index => (long)index, StringComparer.Ordinal);
        var error = "native\n\u001b" + new string('x', 20000);
        var actual = expected.Select(item => (item.Key, item.Value + 1, (string?)error)).ToArray();
        var message = CapturedPayloadArchive.DescribeEntryFailure(actual, expected);
        Assert.Contains($"Observed {PayloadSnapshot.MaxEntries} entries; expected {PayloadSnapshot.MaxEntries + 1}.", message);
        Assert.Contains("name=entry-00000 expected=0 actual=1 error=native??", message);
        Assert.Contains("Missing: manifest.json", message);
        Assert.InRange(message.Length, 1, 6000);
        Assert.DoesNotContain('\n', message);
        Assert.DoesNotContain('\u001b', message);
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

    private sealed class BeforeEnumerationState(NativeStateDirectory inner, Action beforeEnumeration) : NativeStateDirectory
    {
        private Microsoft.Win32.SafeHandles.SafeFileHandle? manifest;
        internal override string CanonicalPath => inner.CanonicalPath;
        internal override FileStream OpenExistingFile(string name, bool writerLock, CancellationToken token)
        {
            var file = inner.OpenExistingFile(name, writerLock, token);
            if (name == "manifest.json") manifest = file.SafeFileHandle;
            return file;
        }
        internal override IEnumerable<(string Name, long Bytes, string? Error)> EnumerateEntries(CancellationToken token)
        {
            Assert.NotNull(manifest);
            Assert.True(manifest.IsClosed, "Manifest read must release its exclusive handle before native enumeration.");
            beforeEnumeration();
            return inner.EnumerateEntries(token);
        }
        internal override OwnedDirectoryLease CaptureOwnership(CancellationToken token) => throw new NotSupportedException();
        internal override FileStream OpenFile(string name, bool writerLock, CancellationToken token) => throw new NotSupportedException();
        internal override void FlushPublication(CancellationToken token) => throw new NotSupportedException();
        internal override void ValidateLocation(CancellationToken token) => throw new NotSupportedException();
        internal override NativeStateDirectory CreateStagingDirectory(string name, CancellationToken token) => throw new NotSupportedException();
        internal override void SealStagingDirectory(int unixMode, CancellationToken token) => throw new NotSupportedException();
        internal override void ReceiveDirectory(NativeDirectoryMoveLease source, string name, CancellationToken token) => throw new NotSupportedException();
        public override void Dispose() { }
    }
}
