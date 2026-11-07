using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.Maintenance;

namespace SkillView.Preferences;

internal sealed record LocationRaw(string Name, SecureFileIdentity Identity, byte[] Bytes)
{
    internal string Hash => Convert.ToHexStringLower(SHA256.HashData(Bytes));
}
internal sealed record LocationLog(string Name, string Path, SecureFileIdentity Identity, LocationRaw[] Files);
internal sealed record LocationBackup(LocationLog Log, LocationBackupManifest? Manifest, SecureFileIdentity? EmptyWitness)
{
    internal string Path => Log.Path;
}
internal sealed record LocationSet(SecureFileIdentity RootIdentity, LocationLog? Records, LocationBackup? Backup, string Version);
internal sealed record LocationBackupEntry(string Name, SecureFileIdentity Identity, long Bytes, string Sha256);
internal sealed record LocationBackupManifest(int Schema, string Context, string RecordsId, SecureFileIdentity Root,
    SecureFileIdentity Records, LocationBackupEntry[] Files);
internal sealed record LocationEmptyWitness(int Schema, string Context, string RecordsId, SecureFileIdentity Records, string ManifestSha256);
internal sealed record LocationResetReview(string Version, string Root, string? RecordsPath, string? BackupPath,
    int Records, long Bytes, bool CanReset, bool CanCompact, ImmutableArray<SelectedLocation>? Saved, string Explanation);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16)]
[JsonSerializable(typeof(LocationBackupManifest))]
[JsonSerializable(typeof(LocationEmptyWitness))]
internal sealed partial class LocationRecoveryJson : JsonSerializerContext;

internal sealed partial class LocationPreferences
{
    private sealed class RecoveryMismatch(string message) : Exception(message);
    private static bool IdName(string? name, string prefix) => name is not null && name.StartsWith(prefix, StringComparison.Ordinal)
        && name.Length == prefix.Length + 32 && name.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static bool RecordName(string? name) => name is not null && name.Length == 9 && name.EndsWith(".json", StringComparison.Ordinal)
        && int.TryParse(name.AsSpan(0, 4), out var n) && n is >= 1 and <= MaxRecords
        && name[..4] == n.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    private static bool SameObject(SecureFileIdentity a, SecureFileIdentity b) => a.Volume == b.Volume && a.FileIdLow == b.FileIdLow
        && a.FileIdHigh == b.FileIdHigh && a.WindowsCreationTime == b.WindowsCreationTime && a.IsDirectory == b.IsDirectory
        && a.IsReparsePoint == b.IsReparsePoint && a.CanonicalPath == b.CanonicalPath;
    private static bool SameGeneration(SecureFileIdentity a, SecureFileIdentity b) => SameObject(a, b)
        && a.ChangeTimeSeconds == b.ChangeTimeSeconds && a.ChangeTimeNanoseconds == b.ChangeTimeNanoseconds
        && a.WindowsChangeTime == b.WindowsChangeTime;
    private static SecureFileIdentity Stable(SecureFileIdentity value) => value with
    { ChangeTimeSeconds = 0, ChangeTimeNanoseconds = 0, WindowsChangeTime = 0, DirectoryFingerprint = null };
    private const string EmptyRecordsDiagnostic = "records directory is empty; backup verified; run Reset to finish";
    private static bool VerifiedEmptyRecords(LocationSet set) => set.Records is { Files.Length: 0 } records
        && set.Backup?.Manifest is { } manifest && records.Name == "records-" + manifest.RecordsId
        && SameObject(records.Identity, manifest.Records);

    private LocationSet CaptureSet(NativeStateDirectory state, IReadOnlyList<string> excluded, CancellationToken token)
    {
        using var rootOwner = state.CaptureOwnership(token);
        var before = rootOwner.Capture(token).Identity;
        var entries = state.EnumerateHistoryEntries(token).Take(4).ToArray();
        string? records = null, backup = null;
        foreach (var entry in entries)
        {
            var path = Path.Combine(state.CanonicalPath, entry.Name);
            if (entry.Error is not null) throw new IOException(At(path, entry.Error));
            if (entry.Name == "writer.lock" && !entry.Directory && entry.Bytes == 0) continue;
            if (entry.Directory && IdName(entry.Name, "records-") && records is null) records = entry.Name;
            else if (entry.Directory && IdName(entry.Name, "backup-") && backup is null) backup = entry.Name;
            else throw new IOException(At(path, "Protected unknown, link, duplicate generation or non-directory entry; nothing was removed."));
        }
        var log = records is null ? null : CaptureLog(state, before, records, excluded, backup: false, token);
        var archive = backup is null ? null : CaptureLog(state, before, backup, excluded, backup: true, token);
        LocationBackup? retained = null;
        if (archive is not null)
        {
            var manifestRaw = archive.Files.SingleOrDefault(file => file.Name == "manifest.json");
            LocationBackupManifest? manifest = null;
            SecureFileIdentity? empty = null;
            if (manifestRaw is not null)
            {
                try
                {
                    manifest = ParseManifest(manifestRaw.Bytes);
                    ValidateBackup(before, archive, manifest);
                    var witness = archive.Files.SingleOrDefault(file => file.Name == "empty.json");
                    if (witness is not null)
                    {
                        var receipt = ParseWitness(witness.Bytes);
                        if (receipt.Schema != 1 || receipt.Context != contextKey || receipt.RecordsId != manifest.RecordsId
                            || receipt.ManifestSha256 != manifestRaw.Hash || !SameObject(receipt.Records, manifest.Records))
                            throw new IOException("Empty-directory witness does not match the backup manifest.");
                        empty = receipt.Records;
                    }
                }
                catch (Exception ex) when (Expected(ex))
                {
                    // A partial manifest is resumable only while the untouched source
                    // still supplies the exact manifest prefix. Apply verifies that prefix.
                    if (log is null || archive.Name != "backup-" + log.Name[8..]) throw new IOException(At(archive.Path, ex.Message));
                    manifest = null;
                }
            }
            retained = new(archive, manifest, empty);
        }
        state.ValidateLocation(token);
        if (!SameGeneration(before, rootOwner.Capture(token).Identity)) throw new IOException(At(state.CanonicalPath, "Root changed during capture."));
        var stamp = new StringBuilder(contextKey).Append('\0').Append(before).Append('\0');
        foreach (var item in new[] { log, archive })
        {
            stamp.Append(item?.Name).Append('\0').Append(item?.Identity).Append('\0');
            foreach (var file in item?.Files ?? []) stamp.Append(file.Name).Append('\0').Append(file.Identity).Append('\0').Append(file.Bytes.Length).Append('\0').Append(file.Hash).Append('\0');
        }
        return new(before, log, retained, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString()))));
    }

    private static LocationLog CaptureLog(NativeStateDirectory parent, SecureFileIdentity expectedParent, string name,
        IReadOnlyList<string> excluded, bool backup, CancellationToken token)
    {
        var path = Path.Combine(parent.CanonicalPath, name);
        if (!SecureRemovalBackend.TryCaptureDirectoryValidationWithinRoot(parent.CanonicalPath, path, out var rooted, out var error, token)
            || !SameGeneration(expectedParent, rooted.RootIdentity)) throw new IOException(At(path, error ?? "Parent/root identity changed or mount boundary refused."));
        using var state = NativeStateDirectory.OpenOrCreate(path, excluded, token, allowCreate: false);
        using var owner = state.CaptureOwnership(token);
        var identity = owner.Capture(token).Identity;
        if (!SameGeneration(identity, rooted.Directory.Identity) || identity.CanonicalPath != path) throw new IOException(At(path, "Child address or native identity changed."));
        var named = state.EnumerateHistoryEntries(token).Take(MaxRecords + 3).ToArray();
        foreach (var entry in named)
            if (entry.Error is not null || entry.Directory || !(RecordName(entry.Name) || backup && entry.Name is "manifest.json" or "empty.json"))
                throw new IOException(At(Path.Combine(path, entry.Name), entry.Error ?? "Protected unknown entry, link, subfolder or mount."));
        if (named.Length > MaxRecords + (backup ? 2 : 0)) throw new IOException(At(path, "Entry count exceeds bounded recovery limits."));
        // State-file admission opens exclusively on Windows. Close each handle
        // BEFORE traversal; bind the later walker to that same admitted native
        // generation rather than opening it again while Content is held.
        var admittedFiles = new Dictionary<string, (SecureFileIdentity Identity, long Bytes, string Sha256)>(StringComparer.Ordinal);
        foreach (var entry in named)
        {
            if (entry.Bytes is < 0 or > MaxRecordBytes) throw new IOException(At(Path.Combine(path, entry.Name), "Record size limit exceeded; nothing was removed."));
            using var admitted = state.OpenExistingFile(entry.Name, false, token);
            PayloadFileEvidence Inspect(NativePayloadItem item)
            {
                if (item.Content is null || item.Content.Length > MaxRecordBytes)
                    throw new IOException(At(Path.Combine(path, entry.Name), "Record size limit exceeded; nothing was removed."));
                var bytes = new byte[checked((int)item.Content.Length)]; item.Content.ReadExactly(bytes);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                admittedFiles.Add(entry.Name, (item.Identity, item.Content.Length, hash));
                return new(hash, item.Content.Length);
            }
            try
            {
                if (OperatingSystem.IsWindows()) WindowsSecureRemovalBackend.CaptureRegularFile(admitted, Inspect, token);
                else UnixSecureRemovalBackend.CaptureRegularFile(admitted, Inspect, token);
            }
            catch (Exception ex) when (Expected(ex)) { throw new IOException(At(Path.Combine(path, entry.Name), ex.Message), ex); }
        }
        var files = new List<LocationRaw>(); long total = 0;
        var walked = SecurePayloadWalker.Walk(path, 0, item =>
        {
            token.ThrowIfCancellationRequested();
            if (item.Kind != "file" || item.Content is null || item.RelativePath.Contains('/')
                || !(RecordName(item.RelativePath) || backup && item.RelativePath is "manifest.json" or "empty.json"))
                throw new IOException(At(Path.Combine(path, item.RelativePath), "Protected unknown entry, link, directory or mount; only bounded known regular files are allowed."));
            if (files.Count >= MaxRecords + (backup ? 2 : 0) || item.Content.Length < 0 || item.Content.Length > MaxRecordBytes)
                throw new IOException(At(Path.Combine(path, item.RelativePath), "Record count/size limit exceeded; nothing was removed."));
            var observed = item.Identity with { CanonicalPath = Path.Combine(identity.CanonicalPath, item.RelativePath) };
            if (!admittedFiles.TryGetValue(item.RelativePath, out var admitted) || !SameGeneration(admitted.Identity, observed)
                || admitted.Bytes != item.Content.Length) throw new IOException(At(path, "Regular file changed after private-state admission."));
            total = checked(total + item.Content.Length);
            if (total > MaxBytes + (backup ? 2L * MaxRecordBytes : 0)) throw new IOException(At(path, "Aggregate byte limit exceeded; nothing was removed."));
            var bytes = new byte[checked((int)item.Content.Length)]; item.Content.ReadExactly(bytes);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != admitted.Sha256) throw new IOException(At(path, "Regular file bytes changed after private-state admission."));
            var file = new LocationRaw(item.RelativePath, item.Identity with { CanonicalPath = Path.Combine(identity.CanonicalPath, item.RelativePath) }, bytes); files.Add(file);
            return new PayloadFileEvidence(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        }, token);
        if (files.Count != admittedFiles.Count || !SameGeneration(identity, walked.Identity) || !SameGeneration(identity, owner.Capture(token).Identity)) throw new IOException(At(path, "Records changed during capture."));
        return new(name, path, identity, files.OrderBy(file => file.Name, StringComparer.Ordinal).ToArray());
    }

    private static void NoDuplicateProperties(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        Visit(doc.RootElement);
        static void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject()) { if (!seen.Add(property.Name)) throw new IOException("Duplicate backup property."); Visit(property.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item);
        }
    }
    private static LocationBackupManifest ParseManifest(byte[] bytes)
    { NoDuplicateProperties(bytes); return JsonSerializer.Deserialize(bytes, LocationRecoveryJson.Default.LocationBackupManifest) ?? throw new IOException("Empty backup manifest."); }
    private static LocationEmptyWitness ParseWitness(byte[] bytes)
    { NoDuplicateProperties(bytes); return JsonSerializer.Deserialize(bytes, LocationRecoveryJson.Default.LocationEmptyWitness) ?? throw new IOException("Empty directory witness."); }
    private void ValidateBackup(SecureFileIdentity rootIdentity, LocationLog archive, LocationBackupManifest manifest)
    {
        if (manifest.Schema != 1 || manifest.Context != contextKey || manifest.Files is null || manifest.Files.Length > MaxRecords
            || !IdName("records-" + manifest.RecordsId, "records-") || archive.Name != "backup-" + manifest.RecordsId
            || !SameObject(rootIdentity, manifest.Root) || manifest.Records.CanonicalPath != Path.Combine(rootIdentity.CanonicalPath, "records-" + manifest.RecordsId)
            || manifest.Files.Any(file => file is null || !RecordName(file.Name) || file.Identity.CanonicalPath != "" || file.Bytes is < 0 or > MaxRecordBytes || file.Sha256 is null || file.Sha256.Length != 64)
            || manifest.Files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Length
            || manifest.Files.Sum(file => file.Bytes) > MaxBytes) throw new IOException(At(archive.Path, "Unsupported or inconsistent backup manifest/context."));
        var raw = archive.Files.Where(file => RecordName(file.Name)).ToArray();
        if (raw.Length != manifest.Files.Length) throw new IOException(At(archive.Path, "Backup raw file set is incomplete."));
        foreach (var file in raw)
        {
            var expected = manifest.Files.SingleOrDefault(item => item.Name == file.Name);
            if (expected is null || file.Bytes.Length != expected.Bytes || file.Hash != expected.Sha256) throw new IOException(At(Path.Combine(archive.Path, file.Name), "Backup bytes do not match the manifest."));
        }
    }

    internal static string ArchiveGuidance(string path) => "A retained backup already exists at " + path
        + ". Before another reset: close all SkillView sessions; copy this entire backup (raw files and manifest) to an external archive outside Locations, verify its bytes against manifest.json, then move the verified backup directory out using your file manager. SkillView never removes it. Removing it from Locations loses in-app access to those earlier saved choices; payloads and gh owners are unaffected.";

    private static void RequireSingleLinks(LocationLog? log, CancellationToken token)
    {
        foreach (var expected in log?.Files ?? [])
        {
            var path = Path.Combine(log!.Path, expected.Name);
            RegularFileInspection.Capture(path, item =>
            {
                if (!SameGeneration(item.Identity, expected.Identity) || item.Content is null || item.Content.Length != expected.Bytes.Length)
                    throw new IOException(At(path, "Native record changed during recovery topology review."));
                var topology = PayloadNativeMetadata.Capture(item, token);
                if (topology.LinkCount != 1) throw new IOException(At(path, "Recovery requires a verified single-link regular file; shared or unknown hard-link topology is protected."));
                var bytes = new byte[expected.Bytes.Length]; item.Content.ReadExactly(bytes);
                if (!bytes.AsSpan().SequenceEqual(expected.Bytes)) throw new IOException(At(path, "Reviewed raw record bytes changed."));
                return new PayloadFileEvidence(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
            }, token);
        }
    }

    internal LocationResetReview PrepareReset(IReadOnlyList<string> excluded, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var state = NativeStateDirectory.OpenOrCreate(Root, excluded, token, allowCreate: false);
            using var held = state.OpenExistingFile("writer.lock", true, token); RequireLock(held);
            var set = CaptureSet(state, excluded, token);
            RequireSingleLinks(set.Records, token); RequireSingleLinks(set.Backup?.Log, token);
            var saved = LatestSaved(set);
            var same = set.Records is not null && set.Backup?.Log.Name == "backup-" + set.Records.Name[8..];
            var mismatchedEmpty = same && set.Records?.Files.Length == 0 && set.Backup?.Manifest is not null && !VerifiedEmptyRecords(set);
            var reset = set.Records is not null && (set.Backup is null || same) && !mismatchedEmpty;
            var pendingCompact = PendingCompact(set, out _);
            var compact = saved is not null && (reset || pendingCompact || set.Records is null && set.Backup?.Manifest is not null);
            var backupPath = set.Backup?.Path ?? (set.Records is null ? null : Path.Combine(Root, "backup-" + set.Records.Name[8..]));
            var count = set.Records?.Files.Length ?? 0; var bytes = set.Records?.Files.Sum(file => (long)file.Bytes.Length) ?? 0;
            var why = $"Saved Locations: {count}/{MaxRecords} records, {bytes}/{MaxBytes} bytes. "
                + (count >= MaxRecords * 3 / 4 || bytes >= MaxBytes * 3 / 4 ? "Near capacity (75% or more). " : "")
                + "\nPermanent store/lock: " + Root + " / writer.lock (never removed).\nRecords: " + (set.Records?.Path ?? "none")
                + "\nBackup: " + (backupPath ?? "none")
                + "\nReset loses saved Locations only, preserves raw bytes in the backup, and leaves payloads, owners and all other state unchanged. CLI options remain active. Unsaved draft edits are discarded."
                + "\nCompact keeps only the latest VALID SAVED selection, never CLI/session overrides. "
                + (saved is null ? "No valid latest saved snapshot is available; Compact is refused." : $"Latest saved selection: {saved.Value.Length} locations.")
                + (set.Records is null && compact ? "\nNo active log: Compact explicitly restores the valid saved selection from the retained backup after interrupted compaction/reset." : "")
                + (set.Backup is not null ? "\n" + ArchiveGuidance(set.Backup.Path) : "")
                + (pendingCompact ? "\nCompact will explicitly resume append-only publication into the reviewed partial generation using the VALID SAVED backup selection; no retained prefix is overwritten." : "")
                + (VerifiedEmptyRecords(set) ? "\n" + EmptyRecordsDiagnostic + ". Reset verifies emptiness again and removes only this empty native child." : "")
                + (mismatchedEmpty ? "\nEmpty records child native identity differs from the verified backup; it remains protected." : "")
                + (same ? "\nRecovery is incomplete; this explicit action resumes only the matching reviewed backup and native survivors." : "");
            return new(set.Version, Root, set.Records?.Path, backupPath, count, bytes, reset, compact, saved, why);
        }
        catch (Exception ex) when (Expected(ex))
        { return new("unavailable", Root, null, null, 0, 0, false, false, null, At(Root, ex.Message) + "\nNothing was removed. Unknown/link/subfolder/mount/oversize state is protected; inspect/archive it externally. Reset requires a verified known records child."); }
    }

    private static ImmutableArray<SelectedLocation>? DecodeSaved(LocationRaw[] files)
    {
        try { var read = Decode(files); return read.HasSavedSnapshot ? read.Locations : null; }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }
    private static bool PendingCompact(LocationSet set, out ImmutableArray<SelectedLocation> saved)
    {
        saved = [];
        if (set.Records is not { } records || set.Backup?.Manifest is null
            || set.Backup.Log.Name == "backup-" + records.Name[8..] || records.Files.Length > 1
            || DecodeSaved(records.Files) is not null) return false;
        var previous = DecodeSaved(set.Backup.Log.Files.Where(file => RecordName(file.Name)).ToArray());
        if (previous is null) return false;
        var bytes = Encode(1, previous.Value);
        if (records.Files.Length == 1 && (records.Files[0].Name != "0001.json" || records.Files[0].Bytes.Length >= bytes.Length
            || !bytes.AsSpan(0, records.Files[0].Bytes.Length).SequenceEqual(records.Files[0].Bytes))) return false;
        saved = previous.Value; return true;
    }
    private static ImmutableArray<SelectedLocation>? LatestSaved(LocationSet set)
    {
        if (PendingCompact(set, out var saved)) return saved;
        var files = set.Backup?.Manifest is not null && (set.Records is null || set.Backup.Log.Name == "backup-" + set.Records.Name[8..])
            ? set.Backup.Log.Files.Where(file => RecordName(file.Name)).ToArray() : set.Records?.Files ?? [];
        return DecodeSaved(files);
    }

    private void ResumeCompact(NativeStateDirectory state, LocationSet set, ImmutableArray<SelectedLocation> saved,
        IReadOnlyList<string> excluded, CancellationToken token, Action<CancellationToken>? revalidate)
    {
        var source = set.Records!;
        using var records = NativeStateDirectory.OpenOrCreate(source.Path, excluded, token, allowCreate: false);
        using var owner = records.CaptureOwnership(token);
        if (!SameGeneration(source.Identity, owner.Capture(token).Identity)) throw new RecoveryMismatch("Partial compaction generation changed.");
        var bytes = Encode(1, saved); var previous = source.Files.SingleOrDefault();
        using var file = records.OpenFile("0001.json", previous is not null, token);
        if (previous is not null)
        {
            PayloadFileEvidence Inspect(NativePayloadItem item)
            {
                if (item.Content is null || !SameGeneration(item.Identity, previous.Identity) || item.Content.Length != previous.Bytes.Length)
                    throw new RecoveryMismatch("Partial compaction file native identity or length changed.");
                var prefix = new byte[previous.Bytes.Length]; item.Content.Position = 0; item.Content.ReadExactly(prefix);
                if (!prefix.AsSpan().SequenceEqual(previous.Bytes)) throw new RecoveryMismatch("Partial compaction prefix changed.");
                return new(Convert.ToHexString(SHA256.HashData(prefix)), prefix.Length);
            }
            // Reuse existing held-file metadata capture. No new native API,
            // deletion or replacement: append only to the reviewed matching prefix.
            if (OperatingSystem.IsWindows()) WindowsSecureRemovalBackend.CaptureRegularFile(file, Inspect, token);
            else UnixSecureRemovalBackend.CaptureRegularFile(file, Inspect, token);
        }
        revalidate?.Invoke(token); records.ValidateLocation(token); state.ValidateLocation(token); token.ThrowIfCancellationRequested();
        file.Position = previous?.Bytes.Length ?? 0; file.Write(bytes.AsSpan(checked((int)file.Position))); file.Flush(true);
        records.FlushNamespace(token); state.FlushPublication(token); Checkpoint?.Invoke("compact-published");
    }

    internal LocationRead Reset(LocationResetReview reviewed, bool compact, IReadOnlyList<string> excluded,
        CancellationToken token, Action<CancellationToken>? revalidate = null)
    {
        token.ThrowIfCancellationRequested();
        if (reviewed.Root != Root || !(compact ? reviewed.CanCompact : reviewed.CanReset)) throw new IOException(At(Root, "Recovery was not admitted by the displayed review. " + reviewed.Explanation));
        using var state = NativeStateDirectory.OpenOrCreate(Root, excluded, token, allowCreate: false);
        using var held = state.OpenExistingFile("writer.lock", true, token); RequireLock(held);
        var set = CaptureSet(state, excluded, token);
        if (set.Version != reviewed.Version) throw new IOException(At(Root, "Locations recovery review is stale. Refresh this review; nothing was removed."));
        revalidate?.Invoke(token); state.ValidateLocation(token);
        RequireSingleLinks(set.Records, token); RequireSingleLinks(set.Backup?.Log, token);
        if (compact && LatestSaved(set) is null) throw new IOException(At(Root, "No valid latest SAVED selection; Compact refused."));
        try
        {
            if (compact && PendingCompact(set, out var pending))
            {
                ResumeCompact(state, set, pending, excluded, token, revalidate);
                return ReadSet(CaptureSet(state, excluded, token));
            }
            if (set.Records is not null) RemoveReviewedRecords(state, set, excluded, token, revalidate);
            Checkpoint?.Invoke("records-removed"); token.ThrowIfCancellationRequested();
            if (compact)
            {
                var saved = reviewed.Saved ?? throw new IOException("No saved selection."); ValidateShape(saved);
                using var records = state.CreateStagingDirectory("records-" + Guid.NewGuid().ToString("N"), token);
                Checkpoint?.Invoke("compact-created-generation"); token.ThrowIfCancellationRequested();
                using (var file = records.OpenFile("0001.json", false, token))
                { Checkpoint?.Invoke("compact-created-file"); token.ThrowIfCancellationRequested(); file.Write(Encode(1, saved)); file.Flush(true); }
                records.FlushNamespace(token); state.FlushPublication(token); Checkpoint?.Invoke("compact-published");
            }
            return ReadSet(CaptureSet(state, excluded, token));
        }
        catch (RecoveryMismatch ex) { throw new IOException(At(Root, ex.Message + " Recovery stopped at first mismatch; backup and survivors are preserved."), ex); }
    }

    private void RemoveReviewedRecords(NativeStateDirectory state, LocationSet set, IReadOnlyList<string> excluded,
        CancellationToken token, Action<CancellationToken>? revalidate)
    {
        var source = set.Records!; var id = source.Name[8..]; var backupName = "backup-" + id;
        if (set.Backup is not null && set.Backup.Log.Name != backupName) throw new IOException(ArchiveGuidance(set.Backup.Path));
        using var backup = set.Backup is null ? state.CreateStagingDirectory(backupName, token)
            : NativeStateDirectory.OpenOrCreate(set.Backup.Path, excluded, token, allowCreate: false);
        Checkpoint?.Invoke("backup-created"); token.ThrowIfCancellationRequested();
        var manifest = set.Backup?.Manifest ?? new LocationBackupManifest(1, contextKey, id, Stable(set.RootIdentity), source.Identity,
            // The held records canonical address + strict immediate name binds
            // every file address. Store it once, so 64 long paths cannot turn
            // the bounded manifest into an otherwise valid log's hidden quota.
            source.Files.Select(file => new LocationBackupEntry(file.Name, file.Identity with { CanonicalPath = "" }, file.Bytes.Length, file.Hash)).ToArray());
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, LocationRecoveryJson.Default.LocationBackupManifest);
        if (manifestBytes.Length > MaxRecordBytes) throw new IOException(At(backup.CanonicalPath, "Backup manifest exceeds limit."));
        if (set.Backup?.Manifest is null)
        {
            // Partial backup files are append-resumed only if every retained byte
            // is an exact prefix of the still-intact reviewed raw source.
            foreach (var existing in set.Backup?.Log.Files ?? [])
                if (existing.Name != "manifest.json" && (!RecordName(existing.Name) || !source.Files.Any(file => file.Name == existing.Name)))
                    throw new IOException(At(Path.Combine(backup.CanonicalPath, existing.Name), "Unexpected partial backup file."));
            foreach (var file in source.Files)
            { PublishPrefix(backup, file.Name, file.Bytes, token); Checkpoint?.Invoke("backup-file"); }
            backup.FlushNamespace(token); state.FlushPublication(token);
            Checkpoint?.Invoke("before-manifest"); token.ThrowIfCancellationRequested();
            PublishPrefix(backup, "manifest.json", manifestBytes, token); backup.FlushNamespace(token); state.FlushPublication(token);
        }
        // Manifest was last, flushed, and all backup bytes are read back before
        // any records handle can be admitted for destruction.
        var refreshed = CaptureSet(state, excluded, token);
        var verified = refreshed.Backup ?? throw new IOException("Backup disappeared.");
        if (verified.Manifest is null) throw new IOException(At(backup.CanonicalPath, "Backup manifest did not verify."));
        manifest = verified.Manifest; manifestBytes = verified.Log.Files.Single(file => file.Name == "manifest.json").Bytes;
        Checkpoint?.Invoke("backup-verified"); token.ThrowIfCancellationRequested();
        using var owner = OwnedDirectoryLease.Open(source.Path);
        var current = owner.Capture(token);
        if (!SameObject(current.Identity, manifest.Records)) throw new RecoveryMismatch("Records directory identity no longer matches the backup.");
        var remaining = refreshed.Records ?? throw new RecoveryMismatch("Records disappeared before removal.");
        if (!SameGeneration(current.Identity, remaining.Identity)) throw new RecoveryMismatch("Records generation changed before removal.");
        foreach (var file in remaining.Files) RequireEntry(file, manifest);
        if (remaining.Files.Length == 0)
        {
            CompleteVerifiedEmptyRecords(state, refreshed, owner, current.Identity, token, revalidate);
            backup.ValidateLocation(token); state.FlushPublication(token);
            return;
        }
        var allowed = remaining.Files.ToDictionary(file => file.Name, StringComparer.Ordinal);
        var removed = false;
        void Guard()
        {
            token.ThrowIfCancellationRequested(); revalidate?.Invoke(token); state.ValidateLocation(token);
            if (!SameObject(owner.Capture(token).Identity, manifest.Records)) throw new RecoveryMismatch("Owned records address changed.");
        }
        void EmptyWitness()
        {
            var captured = owner.Capture(token);
            if (!captured.IsEmpty || !SameObject(captured.Identity, manifest.Records)) throw new RecoveryMismatch("Records are not the reviewed empty child.");
            var receipt = new LocationEmptyWitness(1, contextKey, id, captured.Identity, Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
            PublishPrefix(backup, "empty.json", JsonSerializer.SerializeToUtf8Bytes(receipt, LocationRecoveryJson.Default.LocationEmptyWitness), token);
            backup.FlushNamespace(token); state.FlushPublication(token);
        }
        Guard(); Checkpoint?.Invoke("before-remove"); token.ThrowIfCancellationRequested();
        SecureRemovalBackend.RemoveTree(source.Path, current.Identity, false, 0,
            path => { if (Path.GetDirectoryName(path) != source.Path || !allowed.ContainsKey(Path.GetFileName(path))) throw new RecoveryMismatch("Unexpected entry during removal: " + path); },
            (path, directory) =>
            {
                Guard();
                if (directory)
                {
                    if (path != source.Path || allowed.Count != 0) throw new RecoveryMismatch("Unexpected directory/remnant during removal: " + path);
                    EmptyWitness(); Checkpoint?.Invoke("before-remove-directory");
                }
                else
                {
                    var name = Path.GetFileName(path);
                    if (!allowed.TryGetValue(name, out var expected)) throw new RecoveryMismatch("Unreviewed file: " + path);
                    RegularFileInspection.Capture(path, item =>
                    {
                        var bytes = new byte[expected.Bytes.Length];
                        if (item.Content is null || item.Content.Length != bytes.Length) throw new RecoveryMismatch("File size changed: " + path);
                        item.Content.ReadExactly(bytes);
                        if (PayloadNativeMetadata.Capture(item, token).LinkCount != 1) throw new RecoveryMismatch("Shared/unknown file topology changed: " + path);
                        if (!SameGeneration(item.Identity, expected.Identity) || !bytes.AsSpan().SequenceEqual(expected.Bytes)) throw new RecoveryMismatch("File identity/bytes changed: " + path);
                        return new PayloadFileEvidence(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
                    }, token);
                    Checkpoint?.Invoke("before-remove-file");
                }
                token.ThrowIfCancellationRequested();
            },
            (path, directory) =>
            {
                if (directory) removed = path == source.Path;
                else
                {
                    allowed.Remove(Path.GetFileName(path));
                    if (allowed.Count == 0)
                    {
                        // Windows still holds the deleted file until this callback
                        // returns. Process death closes it before directory deletion.
                        Checkpoint?.Invoke("removed-last-file-before-empty-witness");
                        if (!OperatingSystem.IsWindows()) EmptyWitness();
                    }
                    Checkpoint?.Invoke("removed-file");
                }
            },
            (path, error) => throw new RecoveryMismatch(path + ": " + error), token);
        if (!removed || allowed.Count != 0) throw new RecoveryMismatch("Native records removal was incomplete.");
        // Windows legacy deletion completes at the last ownership-handle close.
        owner.Dispose(); backup.ValidateLocation(token); state.FlushPublication(token);
    }

    private void CompleteVerifiedEmptyRecords(NativeStateDirectory state, LocationSet set, OwnedDirectoryLease owner,
        SecureFileIdentity expected, CancellationToken token, Action<CancellationToken>? revalidate)
    {
        if (!VerifiedEmptyRecords(set)) throw new RecoveryMismatch("Empty records child does not match the complete verified backup.");
        var source = set.Records!;
        // CaptureLog already proved zero entries through its bounded native walk.
        // Independently require a zero-entry walk against this held generation.
        var walked = SecurePayloadWalker.Walk(source.Path, 0,
            item => throw new RecoveryMismatch("Unexpected entry in empty records child: " + Path.Combine(source.Path, item.RelativePath)), token);
        if (!SameGeneration(expected, walked.Identity)) throw new RecoveryMismatch("Empty records generation changed during its zero-entry walk.");
        void Guard()
        {
            token.ThrowIfCancellationRequested(); revalidate?.Invoke(token); state.ValidateLocation(token);
            if (!SecureRemovalBackend.TryCaptureDirectoryValidationWithinRoot(state.CanonicalPath, source.Path, out var rooted, out var error, token)
                || !SameGeneration(set.RootIdentity, rooted.RootIdentity)
                || !SameGeneration(expected, rooted.Directory.Identity) || !rooted.Directory.IsEmpty)
                throw new RecoveryMismatch("Empty records root/mount/identity boundary changed: " + (error ?? source.Path));
            var held = owner.Capture(token);
            if (!held.IsEmpty || !SameGeneration(expected, held.Identity) || !SameObject(held.Identity, set.Backup!.Manifest!.Records))
                throw new RecoveryMismatch("Reviewed records child is no longer the same verified empty native object.");
            token.ThrowIfCancellationRequested();
        }
        Checkpoint?.Invoke("before-empty-remove"); Guard();
        var removed = false;
        SecureRemovalBackend.RemoveTree(source.Path, expected, true, 0,
            path => throw new RecoveryMismatch("Any entry in the reviewed empty child is protected: " + path),
            (path, directory) =>
            {
                if (!directory || path != source.Path) throw new RecoveryMismatch("Unexpected deletion in empty records recovery: " + path);
                Checkpoint?.Invoke("before-empty-remove-directory"); Guard();
            },
            (path, directory) => { if (!directory || path != source.Path) throw new RecoveryMismatch("Unexpected removal in empty records recovery: " + path); removed = true; },
            (path, error) => throw new RecoveryMismatch(path + ": " + error), token);
        if (!removed) throw new RecoveryMismatch("Verified empty records child was not removed.");
        // Complete Windows legacy deletion before the permanent-root flush.
        owner.Dispose();
    }

    private static void RequireEntry(LocationRaw file, LocationBackupManifest manifest)
    {
        var expected = manifest.Files.SingleOrDefault(item => item.Name == file.Name);
        if (expected is null || !SameGeneration(expected.Identity with { CanonicalPath = Path.Combine(manifest.Records.CanonicalPath, file.Name) }, file.Identity)
            || expected.Bytes != file.Bytes.Length || expected.Sha256 != file.Hash)
            throw new RecoveryMismatch("Surviving record is not the backed-up native object/bytes: " + file.Name);
    }
    private static void PublishPrefix(NativeStateDirectory state, string name, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = state.OpenFile(name, true, token);
        var prefix = Array.Empty<byte>();
        PayloadFileEvidence Inspect(NativePayloadItem item)
        {
            if (item.Content is null || item.Content.Length > bytes.Length || PayloadNativeMetadata.Capture(item, token).LinkCount != 1)
                throw new IOException(At(Path.Combine(state.CanonicalPath, name), "Unsafe/shared recovery file or size; nothing was overwritten."));
            prefix = new byte[checked((int)item.Content.Length)]; item.Content.Position = 0; item.Content.ReadExactly(prefix);
            if (!bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix)) throw new IOException(At(Path.Combine(state.CanonicalPath, name), "Recovery publication prefix differs; nothing was overwritten."));
            return new(Convert.ToHexString(SHA256.HashData(prefix)), prefix.Length);
        }
        if (OperatingSystem.IsWindows()) WindowsSecureRemovalBackend.CaptureRegularFile(file, Inspect, token);
        else UnixSecureRemovalBackend.CaptureRegularFile(file, Inspect, token);
        state.ValidateLocation(token); token.ThrowIfCancellationRequested(); file.Position = prefix.Length; file.Write(bytes.AsSpan(prefix.Length)); file.Flush(true);
    }
}
