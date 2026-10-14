using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SkillView.Inventory;

namespace SkillView.Maintenance;

public sealed record BackupHistoryEntry(string Id, string Status, BackupRecord? Record,
    int PayloadEntries, long PayloadBytes, string? Error);

public sealed partial class MaintenanceBackupStore
{
    /// Inspect retained evidence through held native state authority. No file is
    /// created, repaired or extracted, and verified bytes grant no restore authority.
    public IReadOnlyList<BackupHistoryEntry> ReadHistory(CancellationToken token = default,
        IReadOnlyList<string>? excludedRoots = null) => ReadHistoryCore(token, excludedRoots, MaxStoredBytes);

    internal IReadOnlyList<BackupHistoryEntry> ReadHistoryWithBudgetForTests(long budget, CancellationToken token) =>
        ReadHistoryCore(token, null, budget);

    private IReadOnlyList<BackupHistoryEntry> ReadHistoryCore(CancellationToken token,
        IReadOnlyList<string>? excludedRoots, long maxVerifiedBytes)
    {
        token.ThrowIfCancellationRequested();
        var verificationBudget = new HistoryVerificationBudget(maxVerifiedBytes);
        using var owner = NativeStateDirectory.OpenOrCreate(Root, excludedRoots ?? [], token, allowCreate: false);
        using var writerLock = owner.OpenExistingFile("writer.lock", writerLock: true, token);
        if (writerLock.Length != 0) throw new IOException("Maintenance writer lock must be empty.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var entryErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        long storedBytes = 0;
        foreach (var entry in owner.EnumerateEntries(token))
        {
            token.ThrowIfCancellationRequested();
            if (names.Count >= MaxStoreEntries || (storedBytes = checked(storedBytes + entry.Bytes)) > MaxStoredBytes)
                throw new IOException("Maintenance history exceeds its bounded inspection quota.");
            if (!names.Add(entry.Name)) throw new IOException("Maintenance history entry changed during enumeration.");
            if (entry.Error is not null) entryErrors.Add(entry.Name, entry.Error);
        }
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var results = new List<BackupHistoryEntry>();
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            if (name == "writer.lock") continue;
            var suffix = name.EndsWith(".creating.json", StringComparison.Ordinal) ? ".creating.json"
                : name.EndsWith(".captured.json", StringComparison.Ordinal) ? ".captured.json"
                : name.EndsWith(".svbackup", StringComparison.Ordinal) ? ".svbackup" : null;
            var id = suffix is null ? "" : name[..^suffix.Length];
            if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            { results.Add(new(name, "unrecognized", null, 0, 0, "Unexpected retained state entry; preserved for review.")); continue; }
            ids.Add(id);
        }
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var suffix in new[] { ".creating.json", ".captured.json", ".svbackup" })
                    if (entryErrors.TryGetValue(id + suffix, out var error)) throw new IOException(error);
                if (!names.Contains(id + ".creating.json"))
                { results.Add(new(id, "orphaned", null, 0, 0, "Creating journal is absent; retained evidence requires review.")); continue; }
                var creating = ReadJournal(owner, id, "creating", token);
                if (!names.Contains(id + ".captured.json"))
                { results.Add(new(id, "incomplete", creating, 0, 0, "Capture did not publish a captured journal; no restore is authorized.")); continue; }
                var captured = ReadJournal(owner, id, "captured", token);
                if (creating with { Status = "captured", ArchiveHash = captured.ArchiveHash } != captured)
                    throw new IOException("Creating and captured journals disagree.");
                using var archive = owner.OpenExistingFile(id + ".svbackup", writerLock: false, token);
                var details = VerifyArchive(archive, captured, token, verificationBudget);
                owner.ValidateLocation(token);
                results.Add(new(id, "verified", captured, details.Entries, details.Bytes, null));
            }
            catch (Exception ex) when (ex is not HistoryVerificationBudgetException
                && ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException or NotSupportedException)
            { results.Add(new(id, "corrupt", null, 0, 0, ex.Message.Length > 1024 ? ex.Message[..1024] : ex.Message)); }
        }
        owner.ValidateLocation(token);
        return results;
    }

    private static BackupRecord ReadJournal(NativeStateDirectory owner, string id, string status, CancellationToken token)
    {
        using var file = owner.OpenExistingFile(id + "." + status + ".json", writerLock: false, token);
        using var document = ReadBoundedJson(file, MaxJournalBytes, token);
        var row = document.RootElement;
        CheckProperties(row, ["schemaVersion", "id", "installationId", "path", "beforeFingerprint", "archiveHash", "status", "createdAt", "archivePath", "message"]);
        if (row.GetProperty("schemaVersion").GetInt32() != 1 || RequiredString(row, "id") != id || RequiredString(row, "status") != status)
            throw new IOException("Journal schema, ID or status is invalid.");
        var path = RequiredString(row, "path");
        var archivePath = RequiredString(row, "archivePath");
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(archivePath)
            || !PathIdentity.Equals(archivePath, Path.Combine(owner.CanonicalPath, id + ".svbackup")))
            throw new IOException("Journal paths do not match retained state authority.");
        var before = RequiredString(row, "beforeFingerprint");
        RequireHash(before, lower: true);
        var installationId = RequiredString(row, "installationId");
        RequireHash(installationId, lower: true);
        var archiveHash = NullableString(row, "archiveHash");
        if (status == "captured") RequireHash(archiveHash ?? throw new IOException("Captured archive hash is absent."), lower: true);
        else if (archiveHash is not null) throw new IOException("Creating journal unexpectedly declares a captured archive hash.");
        if (!row.GetProperty("createdAt").TryGetDateTimeOffset(out var createdAt)) throw new IOException("Invalid journal timestamp.");
        return new(id, installationId, path, before, archiveHash, status, createdAt,
            archivePath, NullableString(row, "message"));
    }

    private static (int Entries, long Bytes) VerifyArchive(FileStream file, BackupRecord record, CancellationToken token, HistoryVerificationBudget budget)
    {
        var length = file.Length;
        if (length > MaxArchiveBytes) throw new IOException("Archive byte limit exceeded.");
        // ZIP parsing must consume the same immutable bytes we prevalidate.
        // An advisory writer lock cannot prevent an unrelated same-user writer
        // changing a Unix file between end-record validation and ZIP allocation.
        token.ThrowIfCancellationRequested();
        var retained = new byte[checked((int)length)];
        for (var offset = 0; offset < retained.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = file.Read(retained.AsSpan(offset, Math.Min(64 * 1024, retained.Length - offset)));
            if (count == 0) throw new IOException("Retained archive is truncated.");
            offset += count;
        }
        using var frozen = new MemoryStream(retained, writable: false);
        if (HashStream(frozen, length, token, lower: true) != record.ArchiveHash)
            throw new IOException("Archive hash differs from the captured journal.");
        CheckZipDirectoryBound(frozen, token);
        frozen.Position = 0;
        using var archive = new ZipArchive(frozen, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > PayloadSnapshot.MaxEntries + 1) throw new IOException("Archive entry limit exceeded.");
        var blobs = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var blob in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!blobs.TryAdd(blob.FullName, blob)) throw new IOException("Duplicate archive blob.");
        }
        if (!blobs.Remove("manifest.json", out var manifest)) throw new IOException("Archive manifest is absent.");
        if (manifest.Length > MaxManifestBytes) throw new IOException("Manifest byte limit exceeded.");
        budget.Charge(manifest.Length);
        using var manifestStream = manifest.Open();
        using var document = ReadBoundedJson(manifestStream, MaxManifestBytes, token);
        var root = document.RootElement;
        CheckProperties(root, ["schemaVersion", "path", "fingerprint", "metadataContract", "entries"]);
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || RequiredString(root, "path") != record.Path
            || RequiredString(root, "fingerprint") != record.BeforeFingerprint)
            throw new IOException("Archive manifest does not match captured journal.");
        _ = RequiredString(root, "metadataContract");
        var rows = root.GetProperty("entries");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > PayloadSnapshot.MaxEntries)
            throw new IOException("Invalid payload entry array.");
        var entries = ImmutableArray.CreateBuilder<PayloadEntry>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        long bytes = 0;
        long fileBytes = 0;
        foreach (var row in rows.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            CheckProperties(row, ["path", "kind", "hash", "bytes", "unixMode", "blob", "blobBytes"]);
            var path = RequiredString(row, "path");
            if (path.Length > 32_768 || path.StartsWith('/') || path.Split('/').Any(part => part is "" or "." or "..") || path.Contains('\0'))
                throw new IOException("Unsafe archive payload path.");
            var kind = RequiredString(row, "kind");
            if (kind is not ("file" or "directory" or "link") || !paths.TryAdd(path, kind)) throw new IOException("Invalid or duplicate payload entry.");
            var components = path.Count(c => c == '/') + 1;
            if (components > PayloadSnapshot.MaxDepth + (kind == "directory" ? 0 : 1))
                throw new IOException("Payload path exceeds the capture depth limit.");
            var hash = row.GetProperty("hash").GetString() ?? throw new IOException("Payload hash is not a string.");
            var entryBytes = row.GetProperty("bytes").GetInt64();
            var blobBytes = row.GetProperty("blobBytes").GetInt64();
            var modeField = row.GetProperty("unixMode");
            int? mode = modeField.ValueKind == JsonValueKind.Null ? null : modeField.GetInt32();
            if (mode is < 0 or > 0xFFF || entryBytes < 0 || blobBytes < 0) throw new IOException("Invalid payload mode or byte count.");
            var blobName = NullableString(row, "blob");
            if (kind == "directory")
            {
                if (hash != "" || entryBytes != 0 || blobBytes != 0 || blobName is not null) throw new IOException("Directory has unexpected blob data.");
            }
            else
            {
                RequireHash(hash, lower: false);
                if (kind == "file" && entryBytes != blobBytes || kind == "link" && (entryBytes != 0 || blobBytes is <= 0 or > 32 * 1024))
                    throw new IOException("Payload blob size is inconsistent.");
                if (blobName is null || !blobName.StartsWith("payload/", StringComparison.Ordinal)
                    || blobName.Length != 13 || blobName[8..].Any(c => !char.IsAsciiDigit(c)) || !blobs.Remove(blobName, out var blob))
                    throw new IOException("Payload blob is missing, duplicated or invalid.");
                if (kind == "file" && (fileBytes = checked(fileBytes + blobBytes)) > PayloadSnapshot.MaxBytes)
                    throw new IOException("Payload file bytes exceed the capture quota.");
                if (blob.Length != blobBytes || (bytes = checked(bytes + blobBytes)) > MaxArchiveBytes)
                    throw new IOException("Payload blob exceeds its bounded size.");
                budget.Charge(blobBytes);
                using var stream = blob.Open();
                if (HashStream(stream, blobBytes, token, lower: false) != hash) throw new IOException("Payload blob hash differs from manifest.");
            }
            entries.Add(new(path, kind, hash, hash, entryBytes, mode, null));
        }
        foreach (var path in paths.Keys)
        {
            token.ThrowIfCancellationRequested();
            var prefix = path;
            while (prefix.LastIndexOf('/') is var slash && slash >= 0)
            {
                token.ThrowIfCancellationRequested();
                prefix = prefix[..slash];
                if (!paths.TryGetValue(prefix, out var parentKind) || parentKind != "directory")
                    throw new IOException("Payload parent directory is absent or is a link/file.");
            }
        }
        if (blobs.Count != 0 || PayloadSnapshot.Digest(entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToImmutableArray(), normalized: false) != record.BeforeFingerprint)
            throw new IOException("Unreferenced archive data or payload fingerprint mismatch.");
        file.Position = 0;
        if (file.Length != length || HashStream(file, length, token, lower: true) != record.ArchiveHash)
            throw new IOException("Archive changed during verification.");
        return (entries.Count, bytes);
    }

    private static JsonDocument ReadBoundedJson(Stream stream, long limit, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(buffer);
            if (count == 0) break;
            if (bytes.Length + count > limit) throw new IOException("Retained JSON exceeds its byte limit.");
            bytes.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)), new() { MaxDepth = 16 });
    }

    private static void CheckZipDirectoryBound(Stream file, CancellationToken token)
    {
        // Check the writer's bounded, single-disk non-ZIP64 format before
        // ZipArchive.Entries can allocate an unbounded central directory.
        token.ThrowIfCancellationRequested();
        if (file.Length < 22) throw new IOException("Archive end record is truncated.");
        var tail = new byte[22];
        file.Position = file.Length - tail.Length;
        file.ReadExactly(tail);
        var row = tail.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(row) != 0x06054B50
            || BinaryPrimitives.ReadUInt16LittleEndian(row[20..]) != 0)
            throw new IOException("Archive requires an unambiguous comment-free end record.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(row[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(row[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(row[16..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(row[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(row[6..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(row[8..]) != count || count == 0 || count > PayloadSnapshot.MaxEntries + 1
            || size < 46L * count || size > MaxArchiveBytes || (long)offset + size != file.Length - 22)
            throw new IOException("Archive central directory exceeds its supported format or entry bound.");
        file.Position = offset;
        var header = new byte[46];
        var end = (long)offset + size;
        for (var entry = 0; entry < count; entry++)
        {
            token.ThrowIfCancellationRequested();
            if (end - file.Position < header.Length) throw new IOException("Truncated archive central directory.");
            file.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50) throw new IOException("Invalid archive central entry.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)))
                throw new IOException("Archive must use the capture format's stored, uncompressed entries.");
            var extraBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))
                + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            if (extraBytes > end - file.Position) throw new IOException("Archive central entry exceeds its bounded directory.");
            file.Position += extraBytes;
        }
        if (file.Position != end) throw new IOException("Archive has undeclared central entries.");
    }

    private static string HashStream(Stream stream, long expected, CancellationToken token, bool lower)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(buffer);
            if (count == 0) break;
            if ((bytes = checked(bytes + count)) > expected) throw new IOException("Retained blob grew beyond its declared size.");
            hash.AppendData(buffer.AsSpan(0, count));
        }
        if (bytes != expected) throw new IOException("Retained blob is truncated.");
        var digest = hash.GetHashAndReset();
        return lower ? Convert.ToHexStringLower(digest) : Convert.ToHexString(digest);
    }

    private static void CheckProperties(JsonElement row, string[] expected)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new IOException("Retained JSON object expected.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in row.EnumerateObject())
            if (!expected.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw new IOException("Unknown or duplicate retained JSON field.");
        if (seen.Count != expected.Length) throw new IOException("Retained JSON fields are missing.");
    }

    private static string RequiredString(JsonElement row, string name) =>
        NullableString(row, name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new IOException("Missing retained string: " + name);
    private static string? NullableString(JsonElement row, string name)
    {
        var field = row.GetProperty(name);
        if (field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String) throw new IOException("Retained field is not a string: " + name);
        var value = field.GetString();
        if (value?.Length > 32_768) throw new IOException("Retained string exceeds its bound: " + name);
        return value;
    }
    private static void RequireHash(string value, bool lower)
    {
        if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c) || (lower ? char.IsUpper(c) : char.IsLower(c))))
            throw new IOException("Invalid retained SHA-256 hash.");
    }

    private sealed class HistoryVerificationBudgetException() : IOException("Aggregate history verification byte limit exceeded; retained evidence is unchanged.");
    private sealed class HistoryVerificationBudget(long maximum)
    {
        private long _remaining = maximum is >= 0 and <= MaxStoredBytes ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));
        internal void Charge(long bytes)
        {
            if (bytes < 0 || bytes > _remaining) throw new HistoryVerificationBudgetException();
            _remaining -= bytes;
        }
    }
}
