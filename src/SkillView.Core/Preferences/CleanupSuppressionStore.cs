using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Subprocess;

namespace SkillView.Preferences;

/// Append-only, bounded preferences outside payloads. Integrity detects incomplete
/// records; it does not authenticate against another process running as this user.
internal sealed class CleanupSuppressionStore(string root, int maxRecords = 256, long maxBytes = 4 * 1024 * 1024)
{
    internal const int MaxRecordBytes = 32 * 1024;
    internal string Root { get; } = Path.GetFullPath(root);
    private const int ClearReserveBytes = MaxRecordBytes;

    internal static CleanupSuppressionStore For(ProcessRunner runner) => new(Path.Combine(
        Path.GetDirectoryName(MaintenanceBackupStore.ResolveRoot(runner))!, "preferences"));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Key(SuppressionBinding binding) => Hash(JsonSerializer.SerializeToUtf8Bytes(binding,
        SuppressionJsonContext.Default.SuppressionBinding));
    private static bool Legacy(InstalledSkill skill) => skill.CleanupSuppression?.LegacyMarker ?? skill.Ignored;
    private static string Address(InstalledSkill skill)
    {
        if (skill.ObservedSubject?.NativeObject is { Status: "observed", CanonicalPath: { } captured }) return captured;
        if (SecureRemovalBackend.TryObserveIdentity(skill.ResolvedPath, out var identity, out _)) return identity.CanonicalPath;
        return OwnershipPath.EntryAddress(skill.ResolvedPath) ?? throw new IOException("Physical address unavailable.");
    }
    internal static string OwnerNamespace(InstalledSkill skill) => string.Join("\n", skill.AllOwnershipClaims.Select(owner => JsonSerializer.Serialize(new[]
        { owner.Provider, owner.LockFilePath, owner.Selector, owner.ContextRoot, owner.InstallationRoot, owner.SourceType, owner.Source, owner.SourceUrl, owner.SkillPath },
        SuppressionJsonContext.Default.StringArray)).Order(StringComparer.Ordinal));

    internal static bool SameObject(NativeObjectObservation? before, NativeObjectObservation? after) =>
        before is { Status: "observed" } && after is { Status: "observed" }
        && before with { CapturedAt = null, Role = "" } == after with { CapturedAt = null, Role = "" };

    internal SuppressionRead Read(IReadOnlyList<string> exclusions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _ = File.GetAttributes(Root); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return new([], null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new([], "Cleanup preferences unavailable: " + ex.Message); }
        try
        {
            using var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token, allowCreate: false);
            using var held = state.OpenExistingFile("writer.lock", true, token);
            if (held.Length != 0) throw new IOException("Preference writer lock must be empty.");
            return Read(state, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or OverflowException)
        { return new([], "Cleanup preferences unavailable: " + ex.Message); }
    }

    private SuppressionRead Read(NativeStateDirectory state, CancellationToken token)
    {
        var records = ImmutableArray.CreateBuilder<SuppressionRecord>();
        long total = 0;
        var entries = state.EnumerateEntries(token).Take(maxRecords + 2).ToArray();
        if (entries.Length > maxRecords + 1) throw new IOException("Preference record quota exceeded.");
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Name == "writer.lock") continue;
            if (entry.Error is not null) throw new IOException(entry.Error);
            total = checked(total + entry.Bytes);
            if (entry.Bytes <= 0 || entry.Bytes > MaxRecordBytes || total > maxBytes)
                throw new IOException("Preference record or aggregate byte limit exceeded.");
            using var stream = state.OpenExistingFile(entry.Name, false, token);
            if (stream.Length != entry.Bytes) throw new IOException("Preference record changed during capture.");
            var bytes = new byte[checked((int)entry.Bytes)];
            stream.ReadExactly(bytes); token.ThrowIfCancellationRequested();
            CheckJson(bytes);
            var envelope = JsonSerializer.Deserialize(bytes, SuppressionJsonContext.Default.SuppressionEnvelope)
                ?? throw new IOException("Empty preference envelope.");
            if (envelope.Body is null || envelope.Sha256 != Hash(envelope.Body)) throw new IOException("Incomplete or corrupt preference record.");
            CheckJson(envelope.Body);
            var record = JsonSerializer.Deserialize(envelope.Body, SuppressionJsonContext.Default.SuppressionRecord)
                ?? throw new IOException("Empty preference record.");
            if (record.SchemaVersion != 1 || record.Sequence < 1 || record.Sequence > 999999999999
                || entry.Name != record.Sequence.ToString("D12", CultureInfo.InvariantCulture) + ".json"
                || record.Action is not ("set" or "clear") || record.Binding is null
                || record.Key != Key(record.Binding) || !Path.IsPathFullyQualified(record.Binding.Path)
                || record.Binding.Mode is not ("source" or "object") || record.Binding.Source is null || record.Binding.Owners is null)
                throw new IOException("Unsupported or inconsistent preference record.");
            records.Add(record);
        }
        if (records.Select(record => record.Sequence).Distinct().Count() != records.Count)
            throw new IOException("Preference sequence is ambiguous.");
        return new(records.OrderBy(record => record.Sequence).ToImmutableArray(), null);
    }

    // Duplicate properties must not let the checksum projection and parser disagree.
    private static void CheckJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        Visit(document.RootElement);
        static void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new IOException("Duplicate preference property.");
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item);
        }
    }

    internal static CleanupSuppression Match(InstalledSkill skill, SuppressionRead read, CancellationToken token, ref long byteBudget)
    {
        var legacy = Legacy(skill);
        if (read.Diagnostic is not null) return new(legacy, false, "unavailable", Diagnostic: read.Diagnostic);
        if (read.Records.IsEmpty) return new(legacy, false, legacy ? "legacy" : "none");
        try
        {
            var address = Address(skill);
            var candidates = read.Records.Where(record => PathIdentity.Equals(record.Binding.Path, address))
                .GroupBy(record => record.Key).Select(group => group.MaxBy(record => record.Sequence)!).Where(record => record.Action == "set").ToArray();
            if (candidates.Length == 0) return new(legacy, false, legacy ? "legacy" : "none");
            var binding = CaptureBinding(skill, token, ref byteBudget);
            var current = candidates.SingleOrDefault(record => record.Key == Key(binding));
            return current is not null ? new(legacy, true, "active", binding.Mode, current.Sequence)
                : new(legacy, false, "inactive", Diagnostic: "Recorded cleanup suppression belongs to a different source, owner context or object/payload generation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(legacy, false, "inactive", Diagnostic: ex.Message); }
    }

    internal SuppressionResult Set(InstalledSkill target, InventorySnapshot inventory, bool clear, CancellationToken token, Action? mutationObserved = null)
    {
        token.ThrowIfCancellationRequested();
        var exclusions = inventory.ProtectedRoots.Select(scan => scan.Path).Concat(inventory.Skills.Select(skill => skill.ResolvedPath)).ToArray();
        // Clear never needs to read the payload and remains possible after a source
        // or object change. It clears external records only at this exact address.
        long budget = 32 * 1024 * 1024;
        var desired = clear ? null : CaptureBinding(target, token, ref budget);
        var address = desired?.Path ?? Address(target);
        var legacy = Legacy(target);
        using var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token);
        using var held = state.OpenFile("writer.lock", true, token);
        if (held.Length != 0) throw new IOException("Preference writer lock must be empty.");
        var read = Read(state, token);
        var latest = read.Records.GroupBy(record => record.Key).Select(group => group.MaxBy(record => record.Sequence)!).ToArray();
        var changes = clear ? latest.Where(record => record.Action == "set" && PathIdentity.Equals(record.Binding.Path, address)).Select(record => record.Binding).ToArray()
            : latest.Any(record => record.Key == Key(desired!) && record.Action == "set") ? [] : [desired!];
        if (changes.Length == 0)
            return new(false, target.ResolvedPath, new(legacy, !clear, clear ? "cleared" : "active", desired?.Mode,
                Diagnostic: clear && legacy ? "External suppression cleared; the legacy payload marker remains unchanged." : null));
        var sequence = read.Records.IsEmpty ? 0 : read.Records.Max(record => record.Sequence);
        var active = latest.Count(record => record.Action == "set");
        long bytes = state.EnumerateFiles(token).Sum(entry => entry.Bytes);
        var count = read.Records.Length;
        foreach (var binding in changes)
        {
            token.ThrowIfCancellationRequested();
            var record = new SuppressionRecord(1, checked(++sequence), clear ? "clear" : "set", Key(binding), binding);
            var body = JsonSerializer.SerializeToUtf8Bytes(record, SuppressionJsonContext.Default.SuppressionRecord);
            var envelope = JsonSerializer.SerializeToUtf8Bytes(new SuppressionEnvelope(body, Hash(body)), SuppressionJsonContext.Default.SuppressionEnvelope);
            var afterActive = active + (clear ? -1 : 1);
            if (envelope.Length > MaxRecordBytes - (clear ? 0 : 8) || ++count + afterActive > maxRecords
                || bytes + envelope.Length + (long)afterActive * ClearReserveBytes > maxBytes)
                throw new IOException("Preference capacity refused; clear capacity is reserved for every active suppression.");
            if (!clear)
            {
                long remaining = 32 * 1024 * 1024;
                if (CaptureBinding(target, token, ref remaining) != binding) throw new IOException("Selected installation changed before preference publication.");
            }
            using var file = state.OpenFile(sequence.ToString("D12", CultureInfo.InvariantCulture) + ".json", false, token);
            mutationObserved?.Invoke();
            token.ThrowIfCancellationRequested(); file.Write(envelope); file.Flush(true); state.FlushPublication(token);
            bytes += envelope.Length; active = afterActive;
        }
        return new(true, target.ResolvedPath, new(legacy, !clear, clear ? "cleared" : "active", desired?.Mode, sequence,
            clear && legacy ? "External suppression cleared; the legacy payload marker remains unchanged." : null));
    }

    internal static SuppressionBinding CaptureBinding(InstalledSkill skill, CancellationToken token, ref long byteBudget)
    {
        token.ThrowIfCancellationRequested();
        if (skill.Validity == ValidityState.BrokenSymlink)
            throw new IOException("Broken-link suppression is not supported by this binding; the selected row was skipped, and its link remains unchanged.");
        if (!SecureRemovalBackend.TryObserveIdentity(skill.ResolvedPath, out var native, out var error)
            || !native.IsDirectory || native.IsReparsePoint) throw new IOException(error ?? "Selected physical directory is unavailable.");
        var owners = OwnerNamespace(skill);
        if (skill.SourceIdentity.Key is { } source)
        {
            using var metadata = RegularFileInspection.Open(Path.Combine(skill.ResolvedPath, "SKILL.md"));
            var prefix = new byte[LocalSkillScanner.MaxFrontMatterPrefixBytes];
            var length = 0;
            while (length < prefix.Length)
            {
                token.ThrowIfCancellationRequested();
                var count = metadata.Read(prefix, length, prefix.Length - length);
                if (count == 0) break;
                length += count;
            }
            var (_, live, _) = FrontMatterParser.Parse(System.Text.Encoding.UTF8.GetString(prefix, 0, length));
            if ((skill with { FrontMatter = live }).SourceIdentity.Key != source)
                throw new IOException("Selected source metadata changed; refresh before setting suppression.");
            return new(native.CanonicalPath, "source", source, owners, null, null);
        }
        if (!SameObject(skill.ObservedSubject?.NativeObject,
                NativeObjectObservation.From(native, DateTimeOffset.UtcNow, "preference-observation")))
            throw new IOException("Selected unknown-source object changed or its continuity is unavailable; refresh before ignoring.");
        var allowance = byteBudget;
        byteBudget = 0; // Failed captures may already have consumed the allowance.
        var payload = PayloadSnapshot.CaptureEvidence(skill.ResolvedPath, token, allowance);
        byteBudget = allowance - payload.Entries.Sum(entry => entry.Bytes);
        if (payload.RootIdentity != native) throw new IOException("Selected installation changed during preference capture.");
        var objectId = string.Join(':', native.Volume, native.FileIdHigh, native.FileIdLow,
            native.ChangeTimeSeconds, native.ChangeTimeNanoseconds, native.WindowsCreationTime, native.WindowsChangeTime);
        return new(native.CanonicalPath, "object", "unknown", owners, objectId, payload.Fingerprint + ":" + payload.RootUnixMode);
    }
}
