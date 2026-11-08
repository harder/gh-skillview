using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Subprocess;

namespace SkillView.Preferences;

internal sealed record DifferenceBinding(string Family, string Evidence);
internal sealed record DifferenceRecord(int SchemaVersion, long Sequence, string Action, DifferenceBinding Binding);
internal sealed record DifferenceRead(ImmutableArray<DifferenceRecord> Records, string? Diagnostic)
{
    internal string Epoch { get; init; } = "empty";
    internal string? CapacityNotice { get; init; }
    internal bool NearCapacity { get; init; }
    internal string? StorePath { get; init; }
    internal static DifferenceRead Empty { get; } = new([], null);
    internal string Version => SkillDistributorIdentity.Digest([Epoch, .. Records.Select(record =>
        $"{record.Sequence}:{record.Action}:{record.Binding.Family}:{record.Binding.Evidence}")]);
    internal bool Active(DifferenceBinding binding) => Diagnostic is null && Records.LastOrDefault(record => record.Binding.Family == binding.Family) is { Action: "set" } latest && latest.Binding.Evidence == binding.Evidence;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(DifferenceRecord))]
internal partial class DifferenceJsonContext : JsonSerializerContext;

internal static class DifferenceBindingCapture
{
    // Hash metadata only, with explicit field boundaries and no startup payload
    // hashing. Large families stay inspectable but cannot be acknowledged.
    internal static DifferenceBinding? Capture(string kind, ImmutableArray<OperationSubject> subjects, CancellationToken token,
        ImmutableArray<(string Label, string? Value)> diagnostics = default)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        var oversized = false;
        void Put(string? value)
        {
            token.ThrowIfCancellationRequested();
            if (oversized) return;
            if ((value?.Length ?? 0) > 65536 || bytes.Length > 1024 * 1024) { oversized = true; return; }
            writer.Write(value is not null); if (value is not null) writer.Write(value);
        }
        string? PathKey(string? path) => path is null ? null : PathIdentity.NormalizeKey(path);
        Put("intentional-difference-v1"); Put(kind);
        if (!diagnostics.IsDefault)
            foreach (var diagnostic in diagnostics.OrderBy(item => item.Label, StringComparer.Ordinal).ThenBy(item => item.Value, StringComparer.Ordinal))
            { Put(diagnostic.Label); Put(diagnostic.Value); }
        Put("copies");
        var ordered = subjects.OrderBy(subject => subject.InstallationId, StringComparer.Ordinal).ThenBy(subject => subject.Path, StringComparer.Ordinal).ToArray();
        var family = SkillDistributorIdentity.Digest(["copy-family-v1", .. ordered.Select(subject => subject.InstallationId).Distinct()]);
        Put(ordered.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var subject in ordered)
        {
            Put(subject.InstallationId); Put(PathKey(subject.Path)); Put(subject.Name); Put(subject.Scope?.ToString());
            Put(subject.Source.Provider); Put(subject.Source.Origin); Put(subject.Source.Subpath); Put(subject.Source.Evidence); Put(subject.Source.Conflicted.ToString());
            Put(subject.Revision.AuthoredVersion); Put(subject.Revision.RepositoryRef); Put(subject.Revision.RecordedTree); Put(subject.Revision.PinnedRef);
            Put(subject.Revision.OwnerRecordedHash); Put(subject.Revision.OwnerHashKind); Put(subject.Revision.DistributorVersion);
            Put(subject.Materialization); Put(subject.ExposureCoverageComplete.ToString()); Put(subject.ExposureCoverage);
            // Detect unknown-source object replacement without binding ordinary
            // payload timestamps or reading the installed files.
            Put(subject.NativeObject.Volume); Put(subject.NativeObject.ObjectId);
            var owners = subject.Owners.Select(owner => SkillDistributorIdentity.Digest(owner.Provider, owner.SourceType, owner.Source,
                owner.SourceUrl, owner.SkillPath, owner.RepositoryRef, owner.ContentHash, owner.HashKind, owner.PinnedRef, owner.PackageVersion,
                owner.InstallationMode, owner.ReadOnly.ToString(), owner.IsExclusiveOwner.ToString(), owner.SchemaVersion?.ToString(CultureInfo.InvariantCulture),
                owner.Diagnostic, owner.Selector, PathKey(owner.LockFilePath), PathKey(owner.ContextRoot), PathKey(owner.InstallationRoot),
                SkillDistributorIdentity.Digest(owner.CoveredRoots.Select(root => PathKey(root)).Order(StringComparer.Ordinal).ToArray())))
                .Order(StringComparer.Ordinal).ToArray();
            Put(owners.Length.ToString(CultureInfo.InvariantCulture)); foreach (var owner in owners) Put(owner);
            var exposures = subject.Exposures.Select(exposure =>
            {
                var member = exposure.Membership;
                return SkillDistributorIdentity.Digest(member.AgentId, PathKey(member.Path), member.IsSymlink.ToString(), member.Scope?.ToString(),
                    PathKey(member.Context), PathKey(member.DiscoveryRoot), member.Profile, member.AgentVersion, member.Enabled?.ToString(), member.LoadingRule,
                    exposure.EntryKey, PathKey(exposure.EntryAddress));
            }).Order(StringComparer.Ordinal).ToArray();
            Put(exposures.Length.ToString(CultureInfo.InvariantCulture)); foreach (var exposure in exposures) Put(exposure);
        }
        return oversized || bytes.Length > 1024 * 1024 ? null : new(family, Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))));
    }
}

/// Separate append-only issue preferences, using the existing held native state
/// primitive. Explicit Reset preserves raw records before removing only the
/// owned log child. No automatic compaction or payload/registry writes.
internal sealed partial class IntentionalDifferenceStore(string root, int maxRecords = 512, long maxBytes = 2 * 1024 * 1024)
{
    internal const int MaxRecordBytes = 2048;
    internal string Root { get; } = Path.GetFullPath(root);
    internal static IntentionalDifferenceStore For(ProcessRunner runner) => new(Path.Combine(
        Path.GetDirectoryName(MaintenanceBackupStore.ResolveRoot(runner))!, "intentional-differences"));
    internal static string[] Exclusions(InventorySnapshot inventory) => inventory.ProtectedRoots.Select(root => root.Path)
        .Concat(inventory.Skills.Select(skill => skill.ResolvedPath)).ToArray();

    internal static DifferenceRead ReadFor(ProcessRunner runner, InventorySnapshot snapshot, CancellationToken token)
    {
        try { return For(runner).Read(Exclusions(snapshot), token); }
        catch (Exception ex) when (Expected(ex)) { return new([], "Intentional-difference preferences unavailable: " + ex.Message); }
    }
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or OverflowException;
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal DifferenceRead Read(IReadOnlyList<string> exclusions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            try { _ = File.GetAttributes(Root); }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return DifferenceRead.Empty with { StorePath = Root }; }
            using var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token, allowCreate: false);
            using var held = state.OpenExistingFile("writer.lock", true, token);
            RequireLock(state, held, token);
            var result = ReadCurrent(state, exclusions, token);
            RequireLock(state, held, token); return result;
        }
        catch (Exception ex) when (Expected(ex))
        { return new([], "Intentional differences unavailable. " + ex.Message + RecoveryGuidance()) { StorePath = Root }; }
    }

    private DifferenceRead ReadCurrent(NativeStateDirectory state, IReadOnlyList<string> exclusions, CancellationToken token)
    {
        var layout = Layout(state, token);
        if (layout.Backup is { } backup)
        {
            if (layout.Records is null || layout.Records[8..] == backup[7..])
                throw new IOException($"Reset incomplete or unverified. Backup location: {Path.Combine(Root, backup)}. Use Reset saved intentional differences again to inspect and resume; no old acknowledgement is active.");
            VerifyCompleteBackup(state, backup, exclusions, token);
        }
        if (layout.Records is null) return DifferenceRead.Empty with { StorePath = Root };
        using var records = OpenChild(state, layout.Records, exclusions, token);
        using var owned = records.CaptureOwnership(token);
        var identity = owned.Capture(token).Identity;
        var entries = records.EnumerateEntries(token).Take(maxRecords + 1).ToArray();
        if (entries.Length > maxRecords) throw new IOException($"Record quota exceeded at {records.CanonicalPath}.");
        var parsed = ImmutableArray.CreateBuilder<DifferenceRecord>();
        long bytes = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Error is not null || !RecordName(entry.Name)) throw new IOException($"Unrecognized or unsafe preference file: {Path.Combine(records.CanonicalPath, entry.Name)}. {entry.Error}");
            var raw = CaptureFile(records, entry.Name, MaxRecordBytes, token);
            bytes = checked(bytes + raw.Data.Length);
            if (raw.Data.Length == 0 || bytes > maxBytes) throw new IOException($"Preference byte quota exceeded at {records.CanonicalPath}.");
            var envelope = DecodeEnvelope(raw.Data.ToArray());
            var record = JsonSerializer.Deserialize(envelope, DifferenceJsonContext.Default.DifferenceRecord) ?? throw new IOException("Missing preference record.");
            if (record.SchemaVersion != 1 || record.Sequence is < 1 or > 999999999999 || record.Action is not ("set" or "clear")
                || record.Binding is null || !Digest(record.Binding.Family) || !Digest(record.Binding.Evidence)
                || entry.Name != record.Sequence.ToString("D12", CultureInfo.InvariantCulture) + ".json")
                throw new IOException($"Unsupported/inconsistent preference record: {Path.Combine(records.CanonicalPath, entry.Name)}.");
            parsed.Add(record);
        }
        var sorted = parsed.OrderBy(record => record.Sequence).ToImmutableArray();
        if (sorted.Where((record, index) => record.Sequence != index + 1).Any()) throw new IOException($"Incomplete preference sequence at {records.CanonicalPath}.");
        var active = sorted.GroupBy(record => record.Binding.Family).Count(group => group.Last().Action == "set");
        var near = sorted.Length + active >= (maxRecords * 3 + 3) / 4 || bytes + (long)active * MaxRecordBytes >= maxBytes * 3 / 4;
        return new(sorted, null)
        {
            StorePath = Root,
            Epoch = layout.Records + ":" + ObjectKey(identity),
            NearCapacity = near,
            CapacityNotice = near ? Capacity(sorted.Length, active, bytes, layout.Backup) : layout.Backup is null ? null : BackupGuidance(Path.Combine(Root, layout.Backup))
        };
    }

    internal string Set(DifferenceBinding binding, string expectedVersion, bool clear, InventorySnapshot inventory, CancellationToken token, Action? mutationObserved = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Digest(binding.Family) || !Digest(binding.Evidence)) throw new IOException("Invalid intentional-difference binding.");
        var exclusions = Exclusions(inventory);
        mutationObserved?.Invoke();
        using var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token);
        // Initialization may create the permanent lock only in an empty held
        // root. Recreating a missing lock beside retained records could revive
        // old choices merely by attempting a stale write.
        var initialize = !state.EnumerateHistoryEntries(token).Take(1).Any();
        using var held = initialize ? state.OpenFile("writer.lock", true, token)
            : state.OpenExistingFile("writer.lock", true, token);
        RequireLock(state, held, token);
        var read = ReadCurrent(state, exclusions, token);
        if (read.Version != expectedVersion) throw new IOException($"Preferences changed at {Root}; refresh Issues before trying this acknowledgement again.");
        if (read.Active(binding) == !clear) return Root;
        var layout = Layout(state, token);
        var record = new DifferenceRecord(1, read.Records.Length + 1, clear ? "clear" : "set", binding);
        var envelope = Encode(record);
        var latest = read.Records.GroupBy(item => item.Binding.Family).Select(group => group.Last()).ToArray();
        var wasActive = latest.Any(item => item.Binding.Family == binding.Family && item.Action == "set");
        var active = latest.Count(item => item.Action == "set") + (clear ? -1 : wasActive ? 0 : 1);
        long bytes = 0;
        if (layout.Records is not null)
        {
            using var existing = OpenChild(state, layout.Records, exclusions, token);
            bytes = existing.EnumerateFiles(token).Sum(entry => entry.Bytes);
        }
        if (envelope.Length > MaxRecordBytes || read.Records.Length + 1 + active > maxRecords
            || bytes + envelope.Length + (long)active * MaxRecordBytes > maxBytes)
            throw new IOException("Intentional-difference capacity reached. " + Capacity(read.Records.Length, latest.Count(item => item.Action == "set"), bytes, layout.Backup));
        using var records = layout.Records is { } name ? OpenChild(state, name, exclusions, token)
            : state.CreateStagingDirectory("records-" + Guid.NewGuid().ToString("N"), token);
        mutationObserved?.Invoke();
        RequireLock(state, held, token);
        using (var file = records.OpenFile(record.Sequence.ToString("D12", CultureInfo.InvariantCulture) + ".json", false, token))
        { token.ThrowIfCancellationRequested(); file.Write(envelope); file.Flush(true); }
        records.FlushNamespace(token); state.FlushPublication(token);
        return Path.Combine(records.CanonicalPath, record.Sequence.ToString("D12", CultureInfo.InvariantCulture) + ".json");
    }

    private string Capacity(int count, int active, long bytes, string? backup) =>
        $"Store: {Root}. Retained records: {count}/{maxRecords}; active families and reserved clear slots: {active}; bytes: {bytes}/{maxBytes}. "
        + "At most 512 files of 2 KiB are supported per generation. Use Reset saved intentional differences (Shift+K) to forget acknowledgements/history after review; refreshing cannot free capacity. "
        + (backup is null ? "Reset preserves one raw backup, at most 1.5 MiB. A second reset requires external archival of that backup first."
            : BackupGuidance(Path.Combine(Root, backup)));
    private string RecoveryGuidance() => $" Store: {Root}. Use Reset saved intentional differences (Shift+K) to inspect bounded ordinary record bytes and preserve a backup. Unknown names, unsafe files and unsupported state are never deleted; close SkillView and externally archive the exact reported entry/store before manual recovery. Only saved intentional-difference choices/history would be forgotten.";
    private static string BackupGuidance(string path) => $"Preserved backup: {path}. Only one backup is retained. Before another reset, close SkillView and archive this entire backup folder outside the preference root using your filesystem tools. The in-product preservation copy is then no longer available here; active acknowledgements are not changed by archival.";
    private static void RequireLock(NativeStateDirectory state, FileStream held, CancellationToken token)
    {
        state.ValidateLocation(token);
        if (held.Length != 0) throw new IOException($"Permanent preference lock must be empty: {Path.Combine(state.CanonicalPath, "writer.lock")}. Preserve it externally before recovery.");
        CaptureFile(held, "writer.lock", 0, token, Path.Combine(state.CanonicalPath, "writer.lock"));
    }
    private static byte[] Encode(DifferenceRecord record) => Wrap(JsonSerializer.SerializeToUtf8Bytes(record, DifferenceJsonContext.Default.DifferenceRecord));
    private static byte[] Wrap(byte[] body) => JsonSerializer.SerializeToUtf8Bytes(new SuppressionEnvelope(body, Hash(body)), SuppressionJsonContext.Default.SuppressionEnvelope);
    private static byte[] DecodeEnvelope(byte[] bytes)
    {
        CheckJson(bytes);
        var envelope = JsonSerializer.Deserialize(bytes, SuppressionJsonContext.Default.SuppressionEnvelope) ?? throw new IOException("Missing preference envelope.");
        if (envelope.Body is null || envelope.Sha256 != Hash(envelope.Body)) throw new IOException("Incomplete/corrupt preference record; raw bytes are preserved.");
        CheckJson(envelope.Body); return envelope.Body;
    }

    internal string SetObserved(CapturedFinding selected, InventorySnapshot inventory, CancellationToken token, Action? mutationObserved = null)
    {
        token.ThrowIfCancellationRequested();
        var current = MaintenanceQueueBuilder.Build(inventory, token).Findings
            .SingleOrDefault(finding => finding.Id == selected.Id && finding.Difference == selected.Difference);
        if (selected.Difference is null || current?.Subject is not FindingCopies copies)
            throw new IOException("Selected family changed; refresh Issues before changing intentional differences.");
        // This is a preference, not mutation admission for these copies. Still
        // reject changed metadata/owner observations before acknowledging them.
        foreach (var subject in selected.Intentional ? [] : copies.Copies)
        {
            token.ThrowIfCancellationRequested();
            var skill = MaintenanceQueueBuilder.ResolveInstallation(inventory, subject)
                ?? throw new IOException("Selected family has a missing or ambiguous address.");
            if (!SecureRemovalBackend.TryObserveIdentity(subject.Path, out var native, out var error)
                || !CleanupSuppressionStore.SameObject(subject.NativeObject, NativeObjectObservation.From(native, inventory.CapturedAt, "preference-observation")))
                throw new IOException(error ?? "Selected copy changed; refresh Issues.");
            using var metadata = RegularFileInspection.Open(Path.Combine(subject.Path, "SKILL.md"));
            var prefix = new byte[LocalSkillScanner.MaxFrontMatterPrefixBytes];
            var length = 0;
            while (length < prefix.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = metadata.Read(prefix, length, prefix.Length - length); if (read == 0) break; length += read;
            }
            var (_, live, _) = FrontMatterParser.Parse(Encoding.UTF8.GetString(prefix, 0, length));
            var observed = skill with { FrontMatter = live, ObservedSubject = null };
            if (live.Name != skill.FrontMatter.Name || observed.SourceIdentity != skill.SourceIdentity || observed.Revision != skill.Revision)
                throw new IOException("Selected source/revision metadata changed; refresh Issues.");
            foreach (var owner in subject.Owners)
            {
                if (owner.LockFilePath is null || owner.RecordFingerprint is null)
                    throw new IOException("Owner evidence is incomplete; this family remains inspectable but cannot be acknowledged.");
                if (Hash(SkillLockFileReader.ReadBytes(owner.LockFilePath, token)) != owner.RecordFingerprint)
                    throw new IOException("Selected owner record changed; refresh Issues.");
            }
            if (!SecureRemovalBackend.TryObserveIdentity(subject.Path, out var after, out _) || after != native)
                throw new IOException("Selected copy changed during preference review; refresh Issues.");
        }
        return Set(selected.Difference, selected.PreferenceVersion, selected.Intentional, inventory, token, mutationObserved);
    }

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void CheckJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        Visit(document.RootElement);
        static void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                { if (!names.Add(property.Name)) throw new IOException("Duplicate preference property."); Visit(property.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item);
        }
    }
}
