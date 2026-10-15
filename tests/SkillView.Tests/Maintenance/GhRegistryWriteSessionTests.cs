using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class GhRegistryWriteSessionTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-owner-write-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Registry => Path.Combine(temporary.FullName, ".skill-lock.json");
    private string Journal => Path.Combine(temporary.FullName, "journal");
    private const string Id = "11111111111111111111111111111111";
    private const string Original = "{\n  \"version\": 3,\n  \"rootExtension\":{\"preserve\":true},\n  \"skills\":{\"demo\": {\"source\":\"harder/fixture\",\"sourceType\":\"github\",\"skillFolderHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"installedAt\":\"original\",\"extension\":{\"x\":1}},\n    \"other\":{\"source\":\"different/author\",\"extension\":[1,2]}}\n}\n";
    public GhRegistryWriteSessionTests() => File.WriteAllText(Registry, Original);
    public void Dispose() => temporary.Delete(true);
    private GhRegistryPatch Patch(byte[]? bytes = null)
    {
        bytes ??= Encoding.UTF8.GetBytes(Original);
        return GhRegistryEditor.Prepare(bytes, "demo", GhRegistryEditor.CaptureEntry(bytes, "demo", Token).Fingerprint,
            new('b', 40), null, DateTimeOffset.UnixEpoch, Token);
    }
    private byte[] DesiredEntry() => GhRegistryEditor.CaptureEntry(Patch().DesiredBytes, "demo", Token).Bytes;

    [Fact]
    public void IntentPrecedesTruncationAndWritePreservesUnrelatedRawBytes()
    {
        var patch = Patch(); var observed = false;
        using var journal = NativeStateDirectory.OpenOrCreate(Journal, [Registry], Token);
        using var writerLock = journal.OpenFile("writer.lock", true, Token);
        using var session = new GhRegistryWriteSession(Registry, "demo", new Logger(), Token, step =>
        {
            if (step != "registry-truncated") return;
            var intent = ReadIntent("intent");
            Assert.Equal(Encoding.UTF8.GetBytes(Original), intent.Before.Bytes);
            Assert.Equal(patch.DesiredBytes, intent.DesiredBytes);
            Assert.Equal(NativeRegistryFileLease.Hash(intent.DesiredBytes), intent.DesiredHash);
            observed = true;
        });
        var before = session.Read(Token);
        var result = session.WriteSelection(patch.BeforeEntryFingerprint, DesiredEntry(), journal, Id, Token);
        Assert.True(result.Succeeded, result.Error); Assert.True(observed); Assert.True(result.MutationAttempted);
        Assert.False(result.PreimageRestored); Assert.NotNull(result.Current);
        Assert.Equal(patch.DesiredBytes, result.Current.Bytes);
        Assert.True(NativeDirectoryMoveLease.SameObject(before.Identity, result.Current.Identity));
        Assert.True(File.Exists(Path.Combine(Journal, $"registry-{Id}-complete.json")));
    }

    [Theory]
    [InlineData("registry-truncated")]
    [InlineData("registry-chunk-written")]
    [InlineData("registry-flushed")]
    public void InterruptedWriteRestoresExactPreimageWhileSameDataFileLockIsHeld(string stop)
    {
        var patch = Patch();
        using var journal = NativeStateDirectory.OpenOrCreate(Journal, [Registry], Token);
        using var writerLock = journal.OpenFile("writer.lock", true, Token);
        using var session = new GhRegistryWriteSession(Registry, "demo", new Logger(), Token,
            step => { if (step == stop) throw new IOException("Injected write failure"); });
        var before = session.Read(Token);
        var result = session.WriteSelection(patch.BeforeEntryFingerprint, DesiredEntry(), journal, Id, Token);
        Assert.False(result.Succeeded); Assert.True(result.MutationAttempted); Assert.True(result.PreimageRestored, result.Error);
        Assert.False(result.Canceled); Assert.Equal(before.Bytes, session.Read(Token).Bytes);
        Assert.True(NativeDirectoryMoveLease.SameObject(before.Identity, session.Read(Token).Identity));
        var restore = ReadIntent("restore-intent");
        Assert.Equal(before.Bytes, restore.DesiredBytes); Assert.Null(restore.BeforeEntryFingerprint);
        Assert.True(File.Exists(Path.Combine(Journal, $"registry-{Id}-restore-complete.json")));
        Assert.Throws<IOException>(() => NativeRegistryFileLease.Open(Registry, Token));
    }

    [Fact]
    public void CanceledWriteAfterTruncationRestoresDespiteCanceledCaller()
    {
        using var canceled = new CancellationTokenSource();
        var patch = Patch();
        using var journal = NativeStateDirectory.OpenOrCreate(Journal, [Registry], Token);
        using var writerLock = journal.OpenFile("writer.lock", true, Token);
        using var session = new GhRegistryWriteSession(Registry, "demo", new Logger(), Token,
            step => { if (step == "registry-truncated") canceled.Cancel(); });
        var result = session.WriteSelection(patch.BeforeEntryFingerprint, DesiredEntry(), journal, Id, canceled.Token);
        Assert.False(result.Succeeded); Assert.True(result.Canceled); Assert.True(result.PreimageRestored, result.Error);
        Assert.Equal(Encoding.UTF8.GetBytes(Original), session.Read(Token).Bytes);
    }

    [Fact]
    public void RepeatingIdenticalEntryIsVerifiedNoOpWithoutIntentOrTruncation()
    {
        var original = Encoding.UTF8.GetBytes(Original);
        var entry = GhRegistryEditor.CaptureEntry(original, "demo", Token);
        using var journal = NativeStateDirectory.OpenOrCreate(Journal, [Registry], Token);
        using var writerLock = journal.OpenFile("writer.lock", true, Token);
        using var session = new GhRegistryWriteSession(Registry, "demo", new Logger(), Token, _ => throw new InvalidOperationException("No write expected"));
        var result = session.WriteSelection(entry.Fingerprint, entry.Bytes, journal, Id, Token);
        Assert.True(result.Succeeded); Assert.False(result.MutationAttempted);
        Assert.Equal(original, session.Read(Token).Bytes);
        Assert.Equal(new[] { "writer.lock" }, Directory.EnumerateFiles(Journal).Select(Path.GetFileName));
    }

    [Fact]
    public void SelectedEntryDriftRefusesBeforeIntentOrMutation()
    {
        var patch = Patch(); var current = Original.Replace("\"x\":1", "\"x\":2");
        File.WriteAllText(Registry, current);
        using var journal = NativeStateDirectory.OpenOrCreate(Journal, [Registry], Token);
        using var writerLock = journal.OpenFile("writer.lock", true, Token);
        using var session = new GhRegistryWriteSession(Registry, "demo", new Logger(), Token);
        Assert.Throws<IOException>(() => session.WriteSelection(patch.BeforeEntryFingerprint, DesiredEntry(), journal, Id, Token));
        Assert.Equal(Encoding.UTF8.GetBytes(current), session.Read(Token).Bytes);
        Assert.Equal(new[] { "writer.lock" }, Directory.EnumerateFiles(Journal).Select(Path.GetFileName));
    }

    [Fact]
    public void RecoveryWholeFileReviewRejectsEvenUnrelatedChangesSinceInspection()
    {
        var patch = Patch(); RegistryFileSnapshot reviewed;
        using (var inspect = new GhRegistryWriteSession(Registry, "demo", new Logger(), Token)) reviewed = inspect.Read(Token);
        var foreign = Original.Replace("[1,2]", "[1,2,3]");
        File.WriteAllText(Registry, foreign);
        using var journal = NativeStateDirectory.OpenOrCreate(Journal, [Registry], Token);
        using var writerLock = journal.OpenFile("writer.lock", true, Token);
        using var session = new GhRegistryWriteSession(Registry, "demo", new Logger(), Token);
        Assert.Throws<IOException>(() => session.WriteSelection(patch.BeforeEntryFingerprint, DesiredEntry(), journal, Id, Token, reviewed));
        Assert.Equal(Encoding.UTF8.GetBytes(foreign), session.Read(Token).Bytes);
        Assert.Equal(new[] { "writer.lock" }, Directory.EnumerateFiles(Journal).Select(Path.GetFileName));
    }

    [Fact]
    public void UnrelatedChangesBeforeWriteAndUndoSurviveSelectedEntryReplacement()
    {
        var initial = Encoding.UTF8.GetBytes(Original); var originalEntry = GhRegistryEditor.CaptureEntry(initial, "demo", Token);
        var foreign = Original.Replace("[1,2]", "[1,2,3]");
        File.WriteAllText(Registry, foreign);
        using var journal = NativeStateDirectory.OpenOrCreate(Journal, [Registry], Token);
        using var writerLock = journal.OpenFile("writer.lock", true, Token);
        using (var forward = new GhRegistryWriteSession(Registry, "demo", new Logger(), Token))
        {
            var applied = forward.WriteSelection(originalEntry.Fingerprint, DesiredEntry(), journal, Id, Token);
            Assert.True(applied.Succeeded, applied.Error);
        }
        var after = File.ReadAllText(Registry).Replace("[1,2,3]", "[1,2,3,4]");
        File.WriteAllText(Registry, after);
        using (var undo = new GhRegistryWriteSession(Registry, "demo", new Logger(), Token))
        {
            var restored = undo.WriteSelection(Patch().DesiredEntryFingerprint, originalEntry.Bytes, journal, new('2', 32), Token);
            Assert.True(restored.Succeeded, restored.Error);
        }
        Assert.Equal(Original.Replace("[1,2]", "[1,2,3,4]"), File.ReadAllText(Registry));
    }

    [Fact]
    public void ForeignBytesAfterFailedWriteAreRetainedForExplicitReview()
    {
        var patch = Patch(); byte[] foreign = Encoding.UTF8.GetBytes("foreign writer");
        using var journal = NativeStateDirectory.OpenOrCreate(Journal, [Registry], Token);
        using var writerLock = journal.OpenFile("writer.lock", true, Token);
        using var session = new GhRegistryWriteSession(Registry, "demo", new Logger(), Token, step =>
        {
            if (step != "registry-truncated") return;
            // Through a second authority on Unix this would violate flock; use
            // the existing native noncooperating test writer below instead.
            using var writer = NativeRegistryTestWriter.Open(Registry);
            writer.Position = 1; writer.Write(foreign); writer.Flush(true);
            throw new IOException("Interrupted with foreign bytes");
        });
        var result = session.WriteSelection(patch.BeforeEntryFingerprint, DesiredEntry(), journal, Id, Token);
        Assert.False(result.Succeeded); Assert.False(result.PreimageRestored);
        Assert.Contains("cannot be attributed", result.Error);
        Assert.False(File.Exists(Path.Combine(Journal, $"registry-{Id}-restore-intent.json")));
        session.Dispose();
        Assert.Equal(foreign, File.ReadAllBytes(Registry).AsSpan(1).ToArray());
    }

    private RegistryWriteIntent ReadIntent(string suffix) => JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(Journal, $"registry-{Id}-{suffix}.json")), RegistryWriteJsonContext.Default.RegistryWriteIntent)!;
}
