using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Preferences;

internal sealed record DifferenceRawFile(string Name, SecureFileIdentity Identity, ImmutableArray<byte> Data)
{
    internal string Sha256 => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Data.AsSpan()));
}
internal sealed record DifferenceBackupEntry(string Name, SecureFileIdentity SourceIdentity, long Bytes, string Sha256,
    SecureFileIdentity BackupIdentity);
internal sealed record DifferenceBackupManifest(int SchemaVersion, string Records, SecureFileIdentity StoreIdentity,
    SecureFileIdentity RecordsIdentity, ImmutableArray<DifferenceBackupEntry> Files);
internal sealed record DifferenceResetReview(string Store, string Context, string? Records, string? Backup,
    SecureFileIdentity StoreIdentity, SecureFileIdentity LockIdentity, SecureFileIdentity? RecordsIdentity,
    ImmutableArray<DifferenceRawFile> Files, ImmutableArray<DifferenceRawFile> BackupFiles, DifferenceBackupManifest? Manifest,
    string Fingerprint);
internal sealed record DifferenceResetResult(string State, string BackupPath, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(DifferenceBackupManifest))]
internal partial class DifferenceResetJsonContext : JsonSerializerContext;

internal sealed partial class IntentionalDifferenceStore
{
    private const int MaxManifestBytes = 512 * 1024;
    private const string ManifestName = "manifest.json";
    // Test-only interruption seam at publication/removal boundaries. Production
    // uses the same path with no callback and never deletes the permanent lock.
    internal Action<string>? Checkpoint { get; init; }

    private sealed record StoreLayout(string? Records, string? Backup);
    private sealed class ResetStopped(string message) : Exception(message);
    private static bool RecordName(string? name) => name is { Length: 17 } && name.EndsWith(".json", StringComparison.Ordinal)
        && name.AsSpan(0, 12).IndexOfAnyExceptInRange('0', '9') < 0;
    private static bool ChildName(string name, string prefix) => name.StartsWith(prefix, StringComparison.Ordinal)
        && name.Length == prefix.Length + 32 && name.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static SecureFileIdentity FileKey(SecureFileIdentity identity) => identity with { CanonicalPath = "", DirectoryFingerprint = null };
    private static string ObjectKey(SecureFileIdentity identity) => string.Join(':', identity.Volume.ToString("x16"),
        identity.FileIdHigh.ToString("x16"), identity.FileIdLow.ToString("x16"), identity.WindowsCreationTime.ToString(CultureInfo.InvariantCulture));
    private static bool SameObject(SecureFileIdentity left, SecureFileIdentity right) => NativeDirectoryMoveLease.SameObject(left, right)
        && PathIdentity.Equals(left.CanonicalPath, right.CanonicalPath);

    private StoreLayout Layout(NativeStateDirectory state, CancellationToken token)
    {
        var entries = state.EnumerateHistoryEntries(token).Take(4).ToArray();

        string? records = null, backup = null;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Name == "writer.lock" && entry.Error is null && !entry.Directory && entry.Bytes == 0) continue;
            if (entry.Error is not null || !entry.Directory) throw new IOException($"Unsafe/unknown preference entry: {Path.Combine(Root, entry.Name)}. {entry.Error}");
            if (ChildName(entry.Name, "records-") && records is null) records = entry.Name;
            else if (ChildName(entry.Name, "backup-") && backup is null) backup = entry.Name;
            else throw new IOException($"Unrecognized or additional preference directory: {Path.Combine(Root, entry.Name)}. Preserve it externally; no entry was deleted.");
        }
        return new(records, backup);
    }

    private static NativeStateDirectory OpenChild(NativeStateDirectory parent, string name, IReadOnlyList<string> exclusions, CancellationToken token)
    {
        parent.ValidateLocation(token);
        var address = Path.Combine(parent.CanonicalPath, name);
        if (!SecureRemovalBackend.TryCaptureDirectoryValidationWithinRoot(parent.CanonicalPath, address, out var rooted, out var error, token))
            throw new IOException($"Preference child native containment unavailable at {address}: {error}");
        var child = NativeStateDirectory.OpenOrCreate(Path.Combine(parent.CanonicalPath, name), exclusions, token, allowCreate: false);
        try
        {
            parent.ValidateLocation(token);
            if (!PathIdentity.Equals(child.CanonicalPath, address)
                || FileKey(rooted.Directory.Identity) != FileKey(Identity(child, token))
                || FileKey(rooted.RootIdentity) != FileKey(Identity(parent, token))) throw new IOException("Preference child/parent identity or mount changed.");
            return child;
        }
        catch { child.Dispose(); throw; }
    }

    private static DifferenceRawFile CaptureFile(NativeStateDirectory state, string name, int limit, CancellationToken token)
    {
        try
        {
            using var file = state.OpenExistingFile(name, false, token);
            return CaptureFile(file, name, limit, token, Path.Combine(state.CanonicalPath, name));
        }
        catch (Exception ex) when (Expected(ex))
        { throw new IOException($"Cannot capture preference file {Path.Combine(state.CanonicalPath, name)}: {ex.Message}", ex); }
    }
    private static DifferenceRawFile CaptureFile(FileStream file, string name, int limit, CancellationToken token, string? expectedPath = null)
    {
        DifferenceRawFile? result = null;
        PayloadFileEvidence Capture(NativePayloadItem item)
        {
            token.ThrowIfCancellationRequested();
            if (expectedPath is not null && !PathIdentity.Equals(item.Identity.CanonicalPath, expectedPath))
                throw new IOException($"Preference file moved from its held native location: {expectedPath}.");
            var length = file.Length;
            if (length < 0 || length > limit) throw new IOException($"Preference file exceeds {limit} bytes: {item.Identity.CanonicalPath}.");
            var bytes = new byte[(int)length]; file.Position = 0; file.ReadExactly(bytes); token.ThrowIfCancellationRequested();
            result = new(name, FileKey(item.Identity), bytes.ToImmutableArray());
            return new(result.Sha256.ToUpperInvariant(), length);
        }
        if (OperatingSystem.IsWindows()) WindowsSecureRemovalBackend.CaptureRegularFile(file, Capture, token);
        else UnixSecureRemovalBackend.CaptureRegularFile(file, Capture, token);
        return result ?? throw new IOException("Preference file capture did not complete.");
    }
    private static ImmutableArray<DifferenceRawFile> CaptureRecords(NativeStateDirectory state, CancellationToken token)
    {
        var entries = state.EnumerateEntries(token).Take(513).ToArray();
        if (entries.Length > 512) throw new IOException($"Reset supports at most 512 files: {state.CanonicalPath}.");
        var files = ImmutableArray.CreateBuilder<DifferenceRawFile>();
        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            if (entry.Error is not null || !RecordName(entry.Name)) throw new IOException($"Reset refused unknown/unsafe file: {Path.Combine(state.CanonicalPath, entry.Name)}. {entry.Error}");
            files.Add(CaptureFile(state, entry.Name, MaxRecordBytes, token));
        }
        return files.ToImmutable();
    }
    private static SecureFileIdentity Identity(NativeStateDirectory state, CancellationToken token)
    { using var owned = state.CaptureOwnership(token); return owned.Capture(token).Identity; }

    internal DifferenceResetReview InspectReset(InventorySnapshot inventory, string context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var exclusions = Exclusions(inventory);
        using var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token, allowCreate: false);
        using var held = state.OpenExistingFile("writer.lock", true, token);
        RequireLock(state, held, token);
        return InspectReset(state, held, exclusions, context, token);
    }
    private DifferenceResetReview InspectReset(NativeStateDirectory state, FileStream held, IReadOnlyList<string> exclusions, string context, CancellationToken token)
    {
        var layout = Layout(state, token);
        var storeIdentity = Identity(state, token);
        var lockIdentity = CaptureFile(held, "writer.lock", 0, token).Identity;
        if (layout.Records is null && layout.Backup is null) throw new IOException($"No saved intentional differences to reset at {Root}.");
        if (layout.Backup is { } retained && layout.Records is { } active && active[8..] != retained[7..])
            throw new IOException("Reset cannot create a second backup. " + BackupGuidance(Path.Combine(Root, retained)));
        SecureFileIdentity? recordsIdentity = null;
        ImmutableArray<DifferenceRawFile> files = [], backupFiles = [];
        if (layout.Records is { } name)
        {
            using var records = OpenChild(state, name, exclusions, token);
            recordsIdentity = Identity(records, token);
            files = CaptureRecords(records, token);
            if (Identity(records, token) != recordsIdentity) throw new IOException("Records directory changed during reset review.");
        }
        DifferenceBackupManifest? manifest = null;
        if (layout.Backup is { } backupName)
        {
            using var backup = OpenChild(state, backupName, exclusions, token);
            var entries = backup.EnumerateEntries(token).Take(514).ToArray();
            if (entries.Length > 513) throw new IOException($"Reset backup exceeds 513 entries: {backup.CanonicalPath}.");
            var captured = ImmutableArray.CreateBuilder<DifferenceRawFile>();
            foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                if (entry.Error is not null || !(RecordName(entry.Name) || entry.Name == ManifestName))
                    throw new IOException($"Reset refused unknown/unsafe backup entry: {Path.Combine(backup.CanonicalPath, entry.Name)}. {entry.Error}");
                captured.Add(CaptureFile(backup, entry.Name, entry.Name == ManifestName ? MaxManifestBytes : MaxRecordBytes, token));
            }
            backupFiles = captured.ToImmutable();
            if (backupFiles.FirstOrDefault(file => file.Name == ManifestName) is { } rawManifest)
            {
                try { manifest = ParseManifest(rawManifest, state, backupName, backupFiles, token); }
                catch (Exception ex) when (Expected(ex))
                {
                    // A torn manifest may be completed only when it is an exact
                    // prefix of the deterministic, fully matched backup manifest.
                    if (recordsIdentity is null) throw new IOException($"Reset manifest is invalid at {backup.CanonicalPath}; preserve it externally. {ex.Message}");
                }
            }
            if (manifest is not null)
            {
                if (layout.Records is { } current && current != manifest.Records || recordsIdentity is { } identity && !SameObject(identity, manifest.RecordsIdentity))
                    throw new IOException("Reset records native identity changed; preserve both locations.");
                foreach (var file in files) MatchOriginal(file, manifest.Files.SingleOrDefault(entry => entry.Name == file.Name));
            }
            else
            {
                if (recordsIdentity is null) throw new IOException($"Incomplete backup without original records: {backup.CanonicalPath}. Preserve it externally.");
                ValidatePrefixes(files, backupFiles, state, layout.Records!, recordsIdentity.Value, token);
            }
        }
        var fingerprint = ReviewHash(context, layout, storeIdentity, lockIdentity, recordsIdentity, files, backupFiles);
        return new(Root, context, layout.Records, layout.Backup, storeIdentity, lockIdentity, recordsIdentity, files, backupFiles, manifest, fingerprint);
    }

    private static string ReviewHash(string context, StoreLayout layout, SecureFileIdentity store, SecureFileIdentity heldLock,
        SecureFileIdentity? records, ImmutableArray<DifferenceRawFile> files, ImmutableArray<DifferenceRawFile> backup) =>
        SkillDistributorIdentity.Digest([context, layout.Records, layout.Backup, store.ToString(), heldLock.ToString(), records?.ToString(),
            .. files.Concat(backup).Select(file => file.Name + ":" + file.Identity + ":" + file.Data.Length + ":" + file.Sha256)]);

    private DifferenceBackupManifest BuildManifest(NativeStateDirectory state, string records, SecureFileIdentity recordsIdentity,
        ImmutableArray<DifferenceRawFile> files, ImmutableArray<DifferenceRawFile> backup, CancellationToken token) =>
        new(1, records, Identity(state, token) with { ChangeTimeSeconds = 0, ChangeTimeNanoseconds = 0, WindowsChangeTime = 0, DirectoryFingerprint = null },
            recordsIdentity, files.Select(file => new DifferenceBackupEntry(file.Name, file.Identity, file.Data.Length, file.Sha256,
                backup.Single(entry => entry.Name == file.Name).Identity)).ToImmutableArray());
    private static byte[] ManifestBytes(DifferenceBackupManifest manifest) => Wrap(JsonSerializer.SerializeToUtf8Bytes(manifest, DifferenceResetJsonContext.Default.DifferenceBackupManifest));

    private void ValidatePrefixes(ImmutableArray<DifferenceRawFile> files, ImmutableArray<DifferenceRawFile> backup,
        NativeStateDirectory state, string records, SecureFileIdentity recordsIdentity, CancellationToken token)
    {
        foreach (var existing in backup.Where(file => file.Name != ManifestName))
        {
            var source = files.SingleOrDefault(file => file.Name == existing.Name) ?? throw new IOException("Backup file has no matched original; nothing was removed.");
            if (existing.Data.Length > source.Data.Length || !source.Data.AsSpan(0, existing.Data.Length).SequenceEqual(existing.Data.AsSpan()))
                throw new IOException($"Backup bytes do not match original prefix: {Path.Combine(Root, "backup-" + records[8..], existing.Name)}.");
        }
        if (backup.FirstOrDefault(file => file.Name == ManifestName) is { } partial)
        {
            if (backup.Count(file => file.Name != ManifestName) != files.Length || files.Any(file => !backup.Any(entry => entry.Name == file.Name && entry.Data.AsSpan().SequenceEqual(file.Data.AsSpan()))))
                throw new IOException("A partial manifest cannot precede a complete matched raw backup.");
            var expected = ManifestBytes(BuildManifest(state, records, recordsIdentity, files, backup, token));
            if (partial.Data.Length > expected.Length || !expected.AsSpan(0, partial.Data.Length).SequenceEqual(partial.Data.AsSpan()))
                throw new IOException($"Unrecognized manifest bytes at {Path.Combine(Root, "backup-" + records[8..], ManifestName)}; preserve them externally.");
        }
    }

    private DifferenceBackupManifest ParseManifest(DifferenceRawFile raw, NativeStateDirectory state, string backupName, ImmutableArray<DifferenceRawFile> backup, CancellationToken token)
    {
        var manifest = JsonSerializer.Deserialize(DecodeEnvelope(raw.Data.ToArray()), DifferenceResetJsonContext.Default.DifferenceBackupManifest)
            ?? throw new IOException("Missing reset manifest.");
        if (manifest.SchemaVersion != 1 || (manifest.Records is null || !ChildName(manifest.Records, "records-")) || backupName != "backup-" + manifest.Records[8..]
            || manifest.Files.IsDefault || manifest.Files.Length > 512 || manifest.Files.Select(file => file?.Name).Distinct().Count() != manifest.Files.Length
            || !SameObject(manifest.StoreIdentity, Identity(state, token))) throw new IOException("Unsupported or moved reset manifest.");
        if (!manifest.RecordsIdentity.IsDirectory || manifest.RecordsIdentity.IsReparsePoint
            || manifest.RecordsIdentity.Volume != manifest.StoreIdentity.Volume
            || manifest.RecordsIdentity.CanonicalPath != Path.Combine(Root, manifest.Records)) throw new IOException("Reset source binding is invalid.");
        if (backup.Length != manifest.Files.Length + 1) throw new IOException("Reset backup is incomplete.");
        foreach (var entry in manifest.Files)
        {
            if (entry is null || !RecordName(entry.Name) || entry.Bytes is < 0 or > MaxRecordBytes || !Digest(entry.Sha256)
                || entry.SourceIdentity.CanonicalPath != "" || entry.SourceIdentity.IsDirectory || entry.SourceIdentity.IsReparsePoint)
                throw new IOException("Invalid reset manifest entry.");
            var file = backup.SingleOrDefault(file => file.Name == entry.Name) ?? throw new IOException("Missing raw backup file.");
            if (file.Identity != entry.BackupIdentity || file.Data.Length != entry.Bytes || file.Sha256 != entry.Sha256) throw new IOException("Backup native identity/bytes changed.");
        }
        return manifest;
    }

    private void VerifyCompleteBackup(NativeStateDirectory state, string backupName, IReadOnlyList<string> exclusions, CancellationToken token)
    {
        using var backup = OpenChild(state, backupName, exclusions, token);
        var entries = backup.EnumerateEntries(token).Take(514).ToArray();
        if (entries.Length > 513) throw new IOException("Backup entry limit exceeded.");
        var files = entries.Select(entry => entry.Error is not null || !(RecordName(entry.Name) || entry.Name == ManifestName)
            ? throw new IOException($"Unknown backup entry: {Path.Combine(backup.CanonicalPath, entry.Name)}.")
            : CaptureFile(backup, entry.Name, entry.Name == ManifestName ? MaxManifestBytes : MaxRecordBytes, token)).ToImmutableArray();
        ParseManifest(files.SingleOrDefault(file => file.Name == ManifestName) ?? throw new IOException("Backup manifest incomplete."), state, backupName, files, token);
    }
    private static void MatchOriginal(DifferenceRawFile file, DifferenceBackupEntry? expected)
    {
        if (expected is null || file.Identity != expected.SourceIdentity || file.Data.Length != expected.Bytes || file.Sha256 != expected.Sha256)
            throw new IOException($"Remaining original differs from the preserved reset manifest: {file.Name}.");
    }

    internal DifferenceResetResult Reset(DifferenceResetReview review, InventorySnapshot inventory, string context, CancellationToken token, Action? mutationObserved = null)
    {
        token.ThrowIfCancellationRequested();
        if (review.Store != Root || review.Context != context) throw new IOException($"Reset context changed; review the exact captured store again: {Root}.");
        var exclusions = Exclusions(inventory);
        using var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token, allowCreate: false);
        using var held = state.OpenExistingFile("writer.lock", true, token);
        RequireLock(state, held, token);
        var current = InspectReset(state, held, exclusions, context, token);
        if (review.Fingerprint != current.Fingerprint) throw new IOException($"Preference reset review is stale at {Root}; inspect Reset again. No bytes were removed.");
        var backupName = current.Backup ?? "backup-" + current.Records![8..];
        mutationObserved?.Invoke();
        var backupPath = Path.Combine(Root, backupName);
        var manifest = current.Manifest;
        if (manifest is null)
        {
            using var backup = current.Backup is null ? state.CreateStagingDirectory(backupName, token) : OpenChild(state, backupName, exclusions, token);
            mutationObserved?.Invoke(); Checkpoint?.Invoke("backup-created");
            foreach (var file in current.Files)
            {
                CompleteFile(backup, file.Name, file.Data.AsSpan(), MaxRecordBytes, token);
                Checkpoint?.Invoke("backup-file:" + file.Name);
            }
            var copied = current.Files.Select(file => CaptureFile(backup, file.Name, MaxRecordBytes, token)).ToImmutableArray();
            manifest = BuildManifest(state, current.Records!, current.RecordsIdentity!.Value, current.Files, copied, token);
            var bytes = ManifestBytes(manifest);
            if (bytes.Length > MaxManifestBytes) throw new IOException("Reset manifest exceeds its 512 KiB bound; originals remain intact.");
            Checkpoint?.Invoke("before-manifest");
            CompleteFile(backup, ManifestName, bytes, MaxManifestBytes, token);
            backup.FlushPublication(token); state.FlushNamespace(token);
            Checkpoint?.Invoke("manifest-published");
        }
        VerifyCompleteBackup(state, backupName, exclusions, token);
        Checkpoint?.Invoke("backup-verified");
        if (current.Records is { } recordsName)
        {
            var path = Path.Combine(Root, recordsName);
            OwnedDirectoryLease ownership;
            using (var records = OpenChild(state, recordsName, exclusions, token))
            {
                var remaining = CaptureRecords(records, token);
                foreach (var file in remaining) MatchOriginal(file, manifest.Files.SingleOrDefault(entry => entry.Name == file.Name));
                ownership = records.CaptureOwnership(token);
            }
            var removed = false;
            try
            {
                using (ownership)
                {
                    var before = ownership.Capture(token);
                    if (!SameObject(before.Identity, manifest.RecordsIdentity)) throw new IOException("Reset records directory was replaced.");
                    state.ValidateLocation(token); Checkpoint?.Invoke("before-removal"); token.ThrowIfCancellationRequested();
                    SecureRemovalBackend.RemoveTree(path, before.Identity, false, 0,
                        entry => { if (!manifest.Files.Any(file => file.Name == Path.GetFileName(entry)) || Path.GetDirectoryName(entry) != path) throw new ResetStopped("Unexpected entry during reset: " + entry); },
                        (entry, directory) =>
                        {
                            try
                            {
                                token.ThrowIfCancellationRequested(); state.ValidateLocation(token); RequireLock(state, held, token);
                                if (directory) { if (entry != path) throw new ResetStopped("Unexpected reset directory: " + entry); }
                                else
                                {
                                    DifferenceRawFile? raw = null;
                                    RegularFileInspection.Capture(entry, item =>
                                    {
                                        var length = item.Content!.Length;
                                        if (length < 0 || length > MaxRecordBytes) throw new ResetStopped("Reset file grew: " + entry);
                                        var bytes = new byte[(int)length]; item.Content.Position = 0; item.Content.ReadExactly(bytes);
                                        raw = new(Path.GetFileName(entry), FileKey(item.Identity), bytes.ToImmutableArray());
                                        return new(raw.Sha256.ToUpperInvariant(), raw.Data.Length);
                                    }, token);
                                    MatchOriginal(raw!, manifest.Files.SingleOrDefault(file => file.Name == raw!.Name));
                                }
                                Checkpoint?.Invoke("deleting:" + Path.GetFileName(entry)); token.ThrowIfCancellationRequested();
                            }
                            catch (Exception ex) when (Expected(ex)) { throw new ResetStopped(ex.Message); }
                        },
                        (entry, directory) => { if (directory && entry == path) removed = true; Checkpoint?.Invoke("deleted:" + Path.GetFileName(entry)); },
                        (entry, error) => throw new ResetStopped($"Reset stopped at {entry}: {error}"), token);
                }
            }
            catch (ResetStopped ex) { throw new IOException($"Reset incomplete. Backup: {backupPath}. Use Reset again after inspecting the exact remaining bytes. {ex.Message}", ex); }
            if (!removed) throw new IOException($"Reset incomplete at {path}. Backup: {backupPath}; use Reset again.");
            state.FlushNamespace(token); Checkpoint?.Invoke("records-removed");
        }
        if (Layout(state, token).Records is not null) throw new IOException("Records namespace changed before fresh preference publication.");
        Checkpoint?.Invoke("before-fresh-generation"); token.ThrowIfCancellationRequested();
        using (var fresh = state.CreateStagingDirectory("records-" + Guid.NewGuid().ToString("N"), token)) fresh.FlushPublication(token);
        state.FlushNamespace(token); Checkpoint?.Invoke("fresh-generation-published");
        return new("reset", backupPath, $"Saved intentional-difference acknowledgements/history reset. Raw backup: {backupPath}. " + BackupGuidance(backupPath));
    }

    private static void CompleteFile(NativeStateDirectory state, string name, ReadOnlySpan<byte> desired, int limit, CancellationToken token)
    {
        // Existing partial copies may only be extended from an exact reviewed
        // prefix. No truncation, replacement, file deletion or unknown overwrite.
        var existing = state.EnumerateEntries(token).FirstOrDefault(entry => entry.Name == name);
        var length = 0;
        DifferenceRawFile? capturedPrefix = null;
        if (existing.Name is not null)
        {
            if (existing.Error is not null) throw new IOException(existing.Error);
            var prefix = CaptureFile(state, name, limit, token);
            capturedPrefix = prefix;
            if (prefix.Data.Length > desired.Length || !desired[..prefix.Data.Length].SequenceEqual(prefix.Data.AsSpan())) throw new IOException($"Backup prefix changed: {Path.Combine(state.CanonicalPath, name)}.");
            length = prefix.Data.Length;
        }
        if (length == desired.Length && existing.Name is not null) return;
        using (var file = state.OpenFile(name, existing.Name is not null, token))
        {
            if (file.Length != length) throw new IOException("Backup changed before prefix completion.");
            if (capturedPrefix is not null)
            {
                var live = CaptureFile(file, name, limit, token);
                if (live.Identity != capturedPrefix.Identity || !live.Data.AsSpan().SequenceEqual(capturedPrefix.Data.AsSpan()))
                    throw new IOException("Backup object/bytes changed before prefix completion.");
            }
            file.Position = length; token.ThrowIfCancellationRequested(); file.Write(desired[length..]); file.Flush(true); state.FlushPublication(token);
        }
        var verified = CaptureFile(state, name, limit, token);
        if (!verified.Data.AsSpan().SequenceEqual(desired)) throw new IOException("Backup readback differs.");
    }
}
