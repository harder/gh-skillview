using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class MaintenanceBackupHistoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("skillview-history-").FullName;
    private string State => Path.Combine(_root, "state");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => Directory.Delete(_root, true);

    private BackupRecord Capture()
    {
        var payload = Directory.CreateDirectory(Path.Combine(_root, "skills", "demo")).FullName;
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: demo\ndescription: Demo\n---\nbody\n");
        Directory.CreateDirectory(Path.Combine(payload, "empty"));
        var scripts = Directory.CreateDirectory(Path.Combine(payload, "scripts")).FullName;
        File.WriteAllBytes(Path.Combine(scripts, "run.bin"), [0, 1, 255]);
        File.WriteAllText(Path.Combine(payload, ".skillview-ignore"), "keep");
        var target = Assert.Single(new LocalSkillScanner(new Logger()).Scan([new(Path.GetDirectoryName(payload)!, Scope.Custom, null)]));
        return new MaintenanceBackupStore(State).Capture(target, Token);
    }

    [Fact]
    public void CompleteHistoryVerifiesEveryBlobWithoutChangingPayloadOrStateBytes()
    {
        var record = Capture();
        var before = StateHashes();
        var payload = PayloadSnapshot.CaptureEvidence(record.Path, Token);
        var entry = Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token));
        Assert.Equal("verified", entry.Status);
        Assert.Equal(record, entry.Record);
        Assert.Equal(payload.Entries.Length, entry.PayloadEntries);
        Assert.Equal(payload.Entries.Sum(item => item.Bytes), entry.PayloadBytes);
        Assert.Equal(before, StateHashes());
        Assert.Equal(payload.Fingerprint, PayloadSnapshot.CaptureEvidence(record.Path, Token).Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhitespaceOnlyUnixPayloadNamesRoundTrip(bool directory)
    {
        if (OperatingSystem.IsWindows()) return;
        var first = Capture();
        var path = Path.Combine(first.Path, " ");
        if (directory) path = Path.Combine(Directory.CreateDirectory(path).FullName, "  ");
        File.WriteAllText(path, "whitespace fixture");
        var target = Assert.Single(new LocalSkillScanner(new Logger()).Scan([new(Path.GetDirectoryName(first.Path)!, Scope.Custom, null)]));
        var record = new MaintenanceBackupStore(State).Capture(target, Token);
        var entry = Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token), entry => entry.Id == record.Id);
        Assert.Equal("verified", entry.Status);
        Assert.Equal(record.BeforeFingerprint, PayloadSnapshot.CaptureEvidence(first.Path, Token).Fingerprint);
        Assert.Equal("whitespace fixture", File.ReadAllText(path));
    }

    [Fact]
    public void MissingStateRefusesWithoutCreatingEvenAncestors()
    {
        var path = Path.Combine(_root, "absent", "nested");
        Assert.Throws<DirectoryNotFoundException>(() => new MaintenanceBackupStore(path).ReadHistory(Token));
        Assert.False(Directory.Exists(Path.Combine(_root, "absent")));
    }

    [Fact]
    public void CancellationClosesReadAdmissionWithoutCreatingState()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => new MaintenanceBackupStore(State).ReadHistory(source.Token));
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void IncompleteAndOrphanedRecordsRemainVisibleAndUntouched()
    {
        var record = Capture();
        File.Delete(Path.Combine(State, record.Id + ".captured.json"));
        var store = new MaintenanceBackupStore(State);
        Assert.Equal("incomplete", Assert.Single(store.ReadHistory(Token)).Status);
        File.Delete(Path.Combine(State, record.Id + ".creating.json"));
        var before = StateHashes();
        Assert.Equal("orphaned", Assert.Single(store.ReadHistory(Token)).Status);
        Assert.Equal(before, StateHashes());
    }

    [Fact]
    public void TruncatedJournalIsCorruptRatherThanEmptyHistory()
    {
        var record = Capture();
        File.WriteAllText(Path.Combine(State, record.Id + ".captured.json"), "{\"id\":");
        Assert.Equal("corrupt", Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token)).Status);
    }

    [Fact]
    public void RawArchiveCorruptionDoesNotReceiveVerifiedStatus()
    {
        var record = Capture();
        using (var file = File.OpenWrite(record.ArchivePath)) { file.Position = 4; file.WriteByte(255); }
        var entry = Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token));
        Assert.Equal("corrupt", entry.Status);
        Assert.Contains("hash", entry.Error);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("empty//child")]
    public void ForgedManifestPathIsRefusedEvenWhenArchiveHashMatches(string path)
    {
        var record = Capture();
        RewriteArchive(record, root => root["entries"]![0]!["path"] = path);
        var entry = Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token));
        Assert.Equal("corrupt", entry.Status);
        Assert.Contains("path", entry.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("bytes")]
    [InlineData("blobBytes")]
    [InlineData("unixMode")]
    [InlineData("blob")]
    public void ForgedManifestEntryIsRefusedAfterMatchingArchiveHash(string field)
    {
        var record = Capture();
        RewriteArchive(record, root =>
        {
            var row = root["entries"]!.AsArray().First(item => item!["kind"]!.GetValue<string>() == "file")!;
            row[field] = field switch
            {
                "hash" => JsonValue.Create(new string('A', 64)),
                "bytes" or "blobBytes" => JsonValue.Create(-1L),
                "unixMode" => JsonValue.Create(0x1000),
                _ => JsonValue.Create("../outside")
            };
        });
        Assert.Equal("corrupt", Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token)).Status);
    }

    [Fact]
    public void UndeclaredZipEntriesCannotHideBehindForgedEndRecordCount()
    {
        var record = Capture();
        var bytes = File.ReadAllBytes(record.ArchivePath);
        var end = bytes.Length - 22;
        Assert.Equal(0x06054b50u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end)));
        bytes[end + 8] = bytes[end + 10] = 1;
        bytes[end + 9] = bytes[end + 11] = 0;
        File.WriteAllBytes(record.ArchivePath, bytes); UpdateHash(record);
        var entry = Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token));
        Assert.Equal("corrupt", entry.Status);
        Assert.Contains("undeclared central", entry.Error);
    }

    [Fact]
    public void CommentContainingAnotherZipEndRecordIsRefusedBeforeZipParsing()
    {
        var record = Capture();
        var original = File.ReadAllBytes(record.ArchivePath);
        var bytes = new byte[original.Length + 23]; original.CopyTo(bytes, 0);
        original.AsSpan(original.Length - 22).CopyTo(bytes.AsSpan(original.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(original.Length - 2), 23);
        File.WriteAllBytes(record.ArchivePath, bytes); UpdateHash(record);
        var entry = Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token));
        Assert.Equal("corrupt", entry.Status); Assert.Contains("comment-free", entry.Error);
    }

    [Fact]
    public void CompressedEntriesAreOutsideCaptureFormatAndRefused()
    {
        var record = Capture();
        RewriteArchive(record, _ => { }, CompressionLevel.Optimal);
        var entry = Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token));
        Assert.Equal("corrupt", entry.Status); Assert.Contains("uncompressed", entry.Error);
    }

    [Fact]
    public void ManifestDepthBeyondCaptureLimitIsRefused()
    {
        var record = Capture();
        RewriteArchive(record, root => root["entries"]![0]!["path"] = string.Join('/', Enumerable.Repeat("nested", PayloadSnapshot.MaxDepth + 2)));
        var entry = Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token));
        Assert.Equal("corrupt", entry.Status); Assert.Contains("depth limit", entry.Error);
    }

    [Fact]
    public void AggregateVerificationBudgetIsSharedAcrossRecordsAndRetainsEvidence()
    {
        var first = Capture(); Capture();
        long oneRecordBytes;
        using (var archive = ZipFile.OpenRead(first.ArchivePath)) oneRecordBytes = archive.Entries.Sum(entry => entry.Length);
        var before = StateHashes();
        var error = Assert.ThrowsAny<IOException>(() => new MaintenanceBackupStore(State).ReadHistoryWithBudgetForTests(oneRecordBytes + 1, Token));
        Assert.Contains("Aggregate history", error.Message);
        Assert.Equal(before, StateHashes());
        Assert.All(new MaintenanceBackupStore(State).ReadHistory(Token), entry => Assert.Equal("verified", entry.Status));
    }

    [Fact]
    public void DuplicateJsonFieldAndUnknownSchemaAreRefused()
    {
        var record = Capture();
        var path = Path.Combine(State, record.Id + ".captured.json");
        var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("{", "{\"status\":\"captured\",", StringComparison.Ordinal));
        Assert.Equal("corrupt", Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token)).Status);
        var row = JsonNode.Parse(original)!; row["schemaVersion"] = 99; File.WriteAllText(path, row.ToJsonString());
        Assert.Equal("corrupt", Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token)).Status);
    }

    [Fact]
    public void InvalidRetainedArchivePathIsCorruptRatherThanEscapingInspection()
    {
        var record = Capture();
        var path = Path.Combine(State, record.Id + ".captured.json");
        var row = JsonNode.Parse(File.ReadAllText(path))!;
        row["archivePath"] = Path.Combine(_root, "invalid\0path");
        File.WriteAllText(path, row.ToJsonString());
        Assert.Equal("corrupt", Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token)).Status);
    }

    [Fact]
    public void ConcurrentWriterRefusesHistoryThenReleaseAllowsVerification()
    {
        var record = Capture();
        using (var state = NativeStateDirectory.OpenOrCreate(State, [], Token))
        using (var writer = state.OpenFile("writer.lock", writerLock: true, Token))
            Assert.Throws<IOException>(() => new MaintenanceBackupStore(State).ReadHistory(Token));
        Assert.Equal("verified", Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token)).Status);
        Assert.True(File.Exists(record.ArchivePath));
    }

    [Fact]
    public void ExistingReadCannotCreateMissingFileOrFollowLink()
    {
        var record = Capture();
        using (var state = NativeStateDirectory.OpenOrCreate(State, [], Token, allowCreate: false))
            Assert.Throws<IOException>(() => state.OpenExistingFile("absent", false, Token));
        Assert.False(File.Exists(Path.Combine(State, "absent")));
        var external = Path.Combine(_root, "external"); File.WriteAllText(external, "private fixture");
        File.Delete(record.ArchivePath);
        File.CreateSymbolicLink(record.ArchivePath, external);
        Assert.Equal("corrupt", Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token)).Status);
        using (var state = NativeStateDirectory.OpenOrCreate(State, [], Token, allowCreate: false))
            Assert.Throws<IOException>(() => state.OpenExistingFile(record.Id + ".svbackup", false, Token));
        Assert.Equal("private fixture", File.ReadAllText(external));
    }

    [Fact]
    public void LinkedCorruptArchiveDoesNotHideVerifiedPeerOrFollowExternalTarget()
    {
        var first = Capture();
        var second = Capture();
        var external = Path.Combine(_root, "external"); File.WriteAllText(external, "untouched");
        File.Delete(first.ArchivePath); File.CreateSymbolicLink(first.ArchivePath, external);
        var entries = new MaintenanceBackupStore(State).ReadHistory(Token);
        Assert.Equal(2, entries.Count);
        Assert.Equal("corrupt", Assert.Single(entries, entry => entry.Id == first.Id).Status);
        Assert.Equal("verified", Assert.Single(entries, entry => entry.Id == second.Id).Status);
        Assert.Equal("untouched", File.ReadAllText(external));
    }

    [Fact]
    public void OverLimitJournalIsRefusedAndRetained()
    {
        var record = Capture();
        var path = Path.Combine(State, record.Id + ".captured.json");
        File.WriteAllBytes(path, new byte[1024 * 1024 + 1]);
        var entry = Assert.Single(new MaintenanceBackupStore(State).ReadHistory(Token));
        Assert.Equal("corrupt", entry.Status); Assert.Contains("byte limit", entry.Error);
        Assert.Equal(1024 * 1024 + 1, new FileInfo(path).Length);
    }

    private string[] StateHashes() => Directory.EnumerateFiles(State).Order(StringComparer.Ordinal)
        .Select(path => Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private void RewriteArchive(BackupRecord record, Action<JsonNode> change, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        using var rewritten = new MemoryStream();
        using (var source = ZipFile.OpenRead(record.ArchivePath))
        using (var destination = new ZipArchive(rewritten, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var input = entry.Open();
                using var output = destination.CreateEntry(entry.FullName, compression).Open();
                if (entry.FullName == "manifest.json")
                { var row = JsonNode.Parse(input)!; change(row); using var writer = new Utf8JsonWriter(output); row.WriteTo(writer); writer.Flush(); }
                else input.CopyTo(output);
            }
        }
        File.WriteAllBytes(record.ArchivePath, rewritten.ToArray()); UpdateHash(record);
    }
    private void UpdateHash(BackupRecord record)
    {
        var path = Path.Combine(State, record.Id + ".captured.json");
        var row = JsonNode.Parse(File.ReadAllText(path))!;
        row["archiveHash"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(record.ArchivePath)));
        File.WriteAllText(path, row.ToJsonString());
    }
}
