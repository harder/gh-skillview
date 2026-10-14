using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Subprocess;

namespace SkillView.Maintenance;

/// Durable payload evidence outside installed skills. This archive format stores
/// file bytes, entry Unix modes and native link bytes; it is not yet an in-place
/// restore contract for ACLs, extended attributes or owner registries.
public sealed partial class MaintenanceBackupStore(string root)
{
    private readonly Action<string>? _entryArchivedForTests;
    internal MaintenanceBackupStore(string root, Action<string> entryArchivedForTests) : this(root) =>
        _entryArchivedForTests = entryArchivedForTests;
    public const long MaxStoredBytes = 1024L * 1024 * 1024;
    public const int MaxStoreEntries = 4096;
    private const int MaxManifestBytes = 16 * 1024 * 1024;
    private const long MaxArchiveBytes = PayloadSnapshot.MaxBytes + MaxManifestBytes + 10 * 1024 * 1024;
    private const long MaxJournalBytes = 1024 * 1024;
    public string Root { get; } = Path.GetFullPath(root);

    internal static string ResolveRoot(ProcessRunner runner)
    {
        var home = runner.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (string.IsNullOrWhiteSpace(home)) throw new IOException("Cannot establish maintenance state home.");
        home = Path.GetFullPath(home, runner.EffectiveWorkingDirectory);
        var configured = runner.GetEffectiveEnvironmentVariable("SKILLVIEW_STATE_HOME");
        var state = !string.IsNullOrWhiteSpace(configured) ? configured
            : OperatingSystem.IsWindows() ? runner.GetEffectiveEnvironmentVariable("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support")
            : runner.GetEffectiveEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "state");
        return Path.Combine(Path.GetFullPath(state, runner.EffectiveWorkingDirectory), "SkillView", "maintenance");
    }

    public BackupRecord Capture(InstalledSkill target, CancellationToken token = default,
        IReadOnlyList<InstalledSkill>? knownInstallations = null, IReadOnlyList<ScanRoot>? managedRoots = null)
    {
        token.ThrowIfCancellationRequested();
        var before = PayloadSnapshot.CaptureEvidence(target.ResolvedPath, token);
        var excluded = new[] { before.Path, target.ResolvedPath }
            .Concat((knownInstallations ?? []).Select(skill => skill.ResolvedPath))
            .Concat((managedRoots ?? []).Select(scan => scan.Path)).ToArray();
        using var owner = NativeStateDirectory.OpenOrCreate(Root, excluded, token);
        using var writerLock = owner.OpenFile("writer.lock", writerLock: true, token);
        if (!writerLock.CanSeek || writerLock.Length != 0) throw new IOException("Maintenance writer lock must be an empty regular file.");
        EnforceQuota(owner, token);
        var id = Guid.NewGuid().ToString("N");
        var archiveName = id + ".svbackup";
        var archivePath = Path.Combine(owner.CanonicalPath, archiveName);
        var record = new BackupRecord(id, target.InstallationId, before.Path, before.Fingerprint,
            null, "creating", DateTimeOffset.UtcNow, archivePath, null);
        WriteJournal(record, id + ".creating.json", owner, token);
        try
        {
            using (var file = owner.OpenFile(archiveName, writerLock: false, token))
            {
                var blobs = new Dictionary<string, (string Name, long Bytes)>(StringComparer.Ordinal);
                using var archiveBudget = new BoundedWriteStream(file, MaxArchiveBytes);
                using (var archive = new ZipArchive(archiveBudget, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var observed = PayloadSnapshot.CaptureWithEntrySink(target.ResolvedPath, (item, entry) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (item.Kind == "directory") return;
                        var key = "payload/" + blobs.Count.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
                        blobs.Add(entry.Path, (key, item.Kind == "link" ? item.LinkBytes?.Length ?? throw new IOException("Native link bytes missing.") : entry.Bytes));
                        using var destination = archive.CreateEntry(key, CompressionLevel.NoCompression).Open();
                        if (item.Kind == "link") destination.Write(item.LinkBytes ?? throw new IOException("Native link bytes missing."));
                        else CopyVerified(item.Content!, destination, entry, token);
                        _entryArchivedForTests?.Invoke(entry.Path);
                    }, token);
                    if (observed.Fingerprint != before.Fingerprint || observed.Path != before.Path)
                        throw new IOException("Payload changed during backup; no replacement is authorized.");
                    using var manifest = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open();
                    using var bounded = new BoundedWriteStream(manifest, MaxManifestBytes);
                    using var json = new Utf8JsonWriter(bounded);
                    json.WriteStartObject(); json.WriteNumber("schemaVersion", 1);
                    json.WriteString("path", observed.Path); json.WriteString("fingerprint", observed.Fingerprint);
                    json.WriteString("metadataContract", "file bytes, entry Unix modes and native link bytes; ACL/xattr/owner restoration requires a later plan");
                    json.WriteStartArray("entries");
                    foreach (var entry in observed.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        json.WriteStartObject(); json.WriteString("path", entry.Path); json.WriteString("kind", entry.Kind);
                        json.WriteString("hash", entry.ContentHash); json.WriteNumber("bytes", entry.Bytes);
                        if (entry.UnixMode is { } mode) json.WriteNumber("unixMode", mode); else json.WriteNull("unixMode");
                        blobs.TryGetValue(entry.Path, out var blob);
                        json.WriteString("blob", blob.Name); json.WriteNumber("blobBytes", blob.Bytes);
                        json.WriteEndObject(); json.Flush();
                    }
                    json.WriteEndArray(); json.WriteEndObject(); json.Flush();
                }
                file.Flush(flushToDisk: true);
                file.Position = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var count = file.Read(buffer);
                    if (count == 0) break;
                    hash.AppendData(buffer.AsSpan(0, count));
                }
                record = record with { Status = "captured", ArchiveHash = Convert.ToHexStringLower(hash.GetHashAndReset()) };
            }
            owner.FlushPublication(token);
            WriteJournal(record, id + ".captured.json", owner, token);
            return record;
        }
        catch
        {
            // The creating journal and any partial archive stay recoverable.
            // Capture performs no installed-payload replacement or recursive cleanup.
            throw;
        }
    }

    private static void EnforceQuota(NativeStateDirectory owner, CancellationToken token)
    {
        var entries = 0;
        long bytes = 0;
        foreach (var entry in owner.EnumerateFiles(token))
        {
            token.ThrowIfCancellationRequested();
            if (++entries > MaxStoreEntries - 4) throw new IOException("Maintenance state entry limit reached; review retained history before continuing.");
            bytes = checked(bytes + entry.Bytes);
            if (bytes > MaxStoredBytes - MaxArchiveBytes - 2 * MaxJournalBytes)
                throw new IOException("Maintenance backup quota reached; review retained history before continuing.");
        }
    }

    private static void CopyVerified(Stream source, Stream destination, PayloadEntry expected, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = source.Read(buffer);
            if (count == 0) break;
            if ((bytes += count) > expected.Bytes) throw new IOException("Payload grew while archiving.");
            hash.AppendData(buffer.AsSpan(0, count)); destination.Write(buffer, 0, count);
        }
        if (bytes != expected.Bytes || Convert.ToHexString(hash.GetHashAndReset()) != expected.ContentHash)
            throw new IOException("Archived bytes differ from the pinned snapshot.");
    }

    private static void WriteJournal(BackupRecord record, string name, NativeStateDirectory owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using (var stream = owner.OpenFile(name, writerLock: false, token))
        {
            using var budget = new BoundedWriteStream(stream, MaxJournalBytes);
            using var json = new Utf8JsonWriter(budget, new() { Indented = true });
            json.WriteStartObject(); json.WriteNumber("schemaVersion", 1); json.WriteString("id", record.Id);
            json.WriteString("installationId", record.InstallationId); json.WriteString("path", record.Path);
            json.WriteString("beforeFingerprint", record.BeforeFingerprint); json.WriteString("archiveHash", record.ArchiveHash);
            json.WriteString("status", record.Status); json.WriteString("createdAt", record.CreatedAt);
            json.WriteString("archivePath", record.ArchivePath); json.WriteString("message", record.Message);
            json.WriteEndObject(); json.Flush(); stream.Flush(flushToDisk: true);
        }
        owner.FlushPublication(token);
    }

    private sealed class BoundedWriteStream(Stream destination, long limit) : Stream
    {
        private long _bytes;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if ((_bytes += buffer.Length) > limit) throw new IOException("Backup manifest limit exceeded.");
            destination.Write(buffer);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes;
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public sealed record BackupRecord(string Id, string InstallationId, string Path, string BeforeFingerprint,
    string? ArchiveHash, string Status, DateTimeOffset CreatedAt, string ArchivePath, string? Message);
