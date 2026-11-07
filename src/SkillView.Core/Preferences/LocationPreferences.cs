using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Maintenance;
using SkillView.Subprocess;

namespace SkillView.Preferences;

internal sealed record SelectedLocation(string Kind, string Path, string? Agent = null, string? Name = null);
internal sealed record LocationRead(ImmutableArray<SelectedLocation> Locations, long Sequence = 0, string Fingerprint = "missing", string? Diagnostic = null)
{
    internal string? RecordsName { get; init; }
    internal int UsedRecords { get; init; }
    internal long UsedBytes { get; init; }
    internal string? RecoveryHint { get; init; }
    internal bool HasSavedSnapshot { get; init; }
}

/// The root and its empty writer lock are permanent. Only an explicitly reviewed,
/// backed-up records child may be removed through existing native ownership APIs.
internal sealed partial class LocationPreferences(string root, string contextKey = "standalone")
{
    internal string Root { get; } = Path.GetFullPath(root);
    internal const int MaxRecords = 64;
    internal const int MaxRecordBytes = 128 * 1024;
    internal const long MaxBytes = (long)MaxRecords * MaxRecordBytes;
    internal Action<string>? Checkpoint { get; set; }
    internal static LocationPreferences For(ProcessRunner runner) => new(Path.Combine(
        Path.GetDirectoryName(MaintenanceBackupStore.ResolveRoot(runner))!, "locations"), SkillView.Gh.InstallNavigationEvidence.Context(runner));

    internal LocationRead Read(IReadOnlyList<string> excluded, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            try { _ = File.GetAttributes(Root); }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return new([]); }
            using var state = NativeStateDirectory.OpenOrCreate(Root, excluded, token, allowCreate: false);
            FileStream held;
            try { held = state.OpenExistingFile("writer.lock", true, token); }
            catch (Exception ex) when (Expected(ex))
            {
                // Interrupted first initialization may leave ONLY an empty
                // private root. Observe through its held native identity and
                // never create a lock (or ignore any entry) during a read.
                using var owner = state.CaptureOwnership(token);
                var before = owner.Capture(token);
                var entries = state.EnumerateHistoryEntries(token).Take(1).ToArray();
                var after = owner.Capture(token);
                state.ValidateLocation(token);
                if (entries.Length == 0 && before.IsEmpty && after.IsEmpty && SameGeneration(before.Identity, after.Identity))
                    return new([]) { RecoveryHint = At(Root, "Empty Locations initialization was interrupted. Save your selection to finish creating the permanent writer.lock; nothing was written by this read.") };
                throw new IOException(At(Path.Combine(state.CanonicalPath, entries.FirstOrDefault().Name ?? "writer.lock"),
                    "Permanent writer.lock is unavailable and the root is not verified empty; existing state is protected. " + ex.Message), ex);
            }
            using (held)
            {
                RequireLock(held);
                return ReadSet(CaptureSet(state, excluded, token));
            }
        }
        catch (Exception ex) when (Expected(ex)) { return new([], Diagnostic: At(Root, ex.Message)); }
    }

    private LocationRead ReadSet(LocationSet set)
    {
        try
        {
            if (PendingCompact(set, out _)) throw new IOException("Incomplete compaction. Open Recovery and choose Compact, keeping saved selection to resume the reviewed saved snapshot.");
            if (VerifiedEmptyRecords(set)) throw new IOException(EmptyRecordsDiagnostic);
            if (set.Backup is { } backup && (backup.Manifest is null || set.Records?.Name == "records-" + backup.Manifest.RecordsId))
                throw new IOException("Incomplete recovery; backup at " + backup.Path + ". Run Reset saved Locations again.");
            var read = Decode(set.Records?.Files ?? []);
            return read with
            {
                Fingerprint = set.Version,
                RecordsName = set.Records?.Name,
                UsedRecords = set.Records?.Files.Length ?? 0,
                UsedBytes = set.Records?.Files.Sum(file => (long)file.Bytes.Length) ?? 0,
                RecoveryHint = set.Backup is null ? null : ArchiveGuidance(set.Backup.Path)
            };
        }
        catch (Exception ex) when (Expected(ex))
        {
            return new([], Fingerprint: set.Version, Diagnostic: At(set.Records?.Path ?? Root, ex.Message))
            {
                RecordsName = set.Records?.Name,
                UsedRecords = set.Records?.Files.Length ?? 0,
                UsedBytes = set.Records?.Files.Sum(file => (long)file.Bytes.Length) ?? 0,
                RecoveryHint = set.Backup is null ? "Open Recovery to review Reset saved Locations (keeps backup)." : "Open Recovery. " + ArchiveGuidance(set.Backup.Path)
            };
        }
    }

    private static LocationRead Decode(LocationRaw[] files)
    {
        var records = new List<LocationRead>();
        foreach (var file in files)
        {
            if (file.Bytes.Length == 0) throw new IOException("Empty record: " + file.Name);
            using var doc = JsonDocument.Parse(file.Bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var body = doc.RootElement; Properties(body, "schemaVersion", "sequence", "locations");
            if (body.GetProperty("schemaVersion").GetInt32() != 1) throw new IOException("Unsupported location schema.");
            var sequence = body.GetProperty("sequence").GetInt64();
            if (sequence < 1 || sequence > MaxRecords || file.Name != sequence.ToString("D4", CultureInfo.InvariantCulture) + ".json")
                throw new IOException("Invalid location sequence: " + file.Name);
            var array = body.GetProperty("locations");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 320) throw new IOException("Invalid location selection count.");
            var selected = ImmutableArray.CreateBuilder<SelectedLocation>();
            foreach (var item in array.EnumerateArray())
            {
                Properties(item, "kind", "path", "agent", "name");
                selected.Add(new(item.GetProperty("kind").GetString()!, item.GetProperty("path").GetString()!,
                    item.GetProperty("agent").GetString(), item.GetProperty("name").GetString()));
            }
            ValidateShape(selected); records.Add(new(selected.ToImmutable(), sequence));
        }
        var ordered = records.OrderBy(record => record.Sequence).ToArray();
        for (var i = 0; i < ordered.Length; i++) if (ordered[i].Sequence != i + 1) throw new IOException("Incomplete location record sequence.");
        return (ordered.LastOrDefault() ?? new([])) with { HasSavedSnapshot = ordered.Length > 0 };
    }

    internal LocationRead Save(LocationRead expected, ImmutableArray<SelectedLocation> selected,
        IReadOnlyList<string> excluded, string cwd, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (expected.Diagnostic is not null) throw new IOException(expected.Diagnostic);
        ValidateShape(selected);
        foreach (var item in selected) if (!expected.Locations.Contains(item)) _ = ValidateNew(item, cwd, token);
        using var state = NativeStateDirectory.OpenOrCreate(Root, excluded, token, afterCreate: path =>
        {
            if (PathIdentity.Equals(path, Root) || PathIdentity.Equals(path, OwnershipPath.CanonicalCandidate(Root) ?? Root))
            { Checkpoint?.Invoke("save-created-root-before-lock"); token.ThrowIfCancellationRequested(); }
        });
        using var held = state.OpenFile("writer.lock", true, token); RequireLock(held);
        var set = CaptureSet(state, excluded, token); var current = ReadSet(set);
        var bootstrap = expected.Fingerprint == "missing" && set.Records is null && set.Backup is null;
        if (!bootstrap && current.Fingerprint != expected.Fingerprint) throw new IOException(At(Root, "Locations changed in another session or were reset; reopen and review the saved selection."));
        if (current.Diagnostic is not null) throw new IOException(current.Diagnostic);
        if (current.HasSavedSnapshot && current.Locations.SequenceEqual(selected)) return current;
        if (current.UsedRecords >= MaxRecords) throw new IOException(At(Root, $"Locations capacity {current.UsedRecords}/{MaxRecords} records, {current.UsedBytes}/{MaxBytes} bytes. Open Recovery: Compact, keeping saved selection; or Reset saved Locations (keeps backup). " + current.RecoveryHint));
        var name = set.Records?.Name ?? "records-" + Guid.NewGuid().ToString("N");
        using var records = set.Records is null ? state.CreateStagingDirectory(name, token)
            : NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, name), excluded, token, allowCreate: false);
        using var recordOwner = records.CaptureOwnership(token);
        if (set.Records is { } previous && !SameGeneration(recordOwner.Capture(token).Identity, previous.Identity))
            throw new IOException(At(records.CanonicalPath, "Records generation changed before Save."));
        state.ValidateLocation(token);
        var bytes = Encode(current.Sequence + 1, selected);
        if (bytes.Length > MaxRecordBytes || current.UsedBytes + bytes.Length > MaxBytes) throw new IOException(At(records.CanonicalPath, "Location byte capacity exceeded; open Recovery."));
        Checkpoint?.Invoke("save-created-generation"); token.ThrowIfCancellationRequested();
        using (var file = records.OpenFile((current.Sequence + 1).ToString("D4", CultureInfo.InvariantCulture) + ".json", false, token))
        {
            Checkpoint?.Invoke("save-created-file"); token.ThrowIfCancellationRequested();
            file.Write(bytes); Checkpoint?.Invoke("save-written"); file.Flush(true);
        }
        // Child namespace flush, then the permanent held writer's device
        // durability barrier (also required when reopening children on macOS).
        records.FlushNamespace(token); state.FlushPublication(token);
        Checkpoint?.Invoke("save-published");
        return ReadSet(CaptureSet(state, excluded, token));
    }

    private static byte[] Encode(long sequence, ImmutableArray<SelectedLocation> selected)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject(); json.WriteNumber("schemaVersion", 1); json.WriteNumber("sequence", sequence); json.WriteStartArray("locations");
            foreach (var item in selected)
            {
                json.WriteStartObject(); json.WriteString("kind", item.Kind); json.WriteString("path", item.Path);
                json.WriteString("agent", item.Agent); json.WriteString("name", item.Name); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static string At(string path, string message) => "Locations at " + path + ": " + message;
    private static void RequireLock(FileStream file) { if (file.Length != 0) throw new IOException("Permanent Locations writer.lock must be empty."); }

    private static void Properties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new IOException("Invalid location object.");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || names.Except(expected, StringComparer.Ordinal).Any()) throw new IOException("Unknown, missing or duplicate location property.");
    }

    internal static void ValidateShape(IEnumerable<SelectedLocation> selections)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new HashSet<SelectedLocation>();
        foreach (var item in selections)
        {
            if (item.Kind is not ("workspace" or "scan-root" or "profile") || string.IsNullOrWhiteSpace(item.Path)
                || item.Path.Length > 4096 || item.Path.Any(char.IsControl) || !Path.IsPathFullyQualified(item.Path)
                || PathIdentity.Normalize(item.Path) != item.Path || !keys.Add(item)) throw new IOException("Invalid or duplicate location selection.");
            counts[item.Kind] = counts.GetValueOrDefault(item.Kind) + 1;
            if (counts[item.Kind] > (item.Kind == "scan-root" ? 256 : 32)) throw new IOException("Location limit exceeded (32 workspaces/profiles, 256 scan roots).");
            if (item.Kind == "profile")
            {
                if (!SkillView.Gh.GhPlacementCatalog.Entries.Any(entry => entry.Id == item.Agent)
                    || item.Name is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)))
                    throw new IOException("Invalid agent or profile name.");
            }
            else if (item.Agent is not null || item.Name is not null) throw new IOException("Unexpected profile fields.");
        }
    }

    internal static SelectedLocation ValidateNew(SelectedLocation item, string cwd, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(item.Path) || item.Path.Length > 4096 || item.Path.Any(char.IsControl)) throw new IOException("Enter a directory path without control characters (at most 4096 characters).");
        item = item with { Path = PathIdentity.Normalize(Path.GetFullPath(item.Path, cwd)) };
        ValidateShape([item]);
        if (!Directory.Exists(item.Path) || OwnershipPath.CanonicalCandidate(item.Path) is null) throw new IOException("Select an existing, accessible directory.");
        using var cursor = Directory.EnumerateFileSystemEntries(item.Path).GetEnumerator();
        _ = cursor.MoveNext(); token.ThrowIfCancellationRequested();
        return item;
    }

    internal static AppOptions Apply(AppOptions cli, LocationRead saved) => cli with
    {
        WorkspaceDirectories = cli.WorkspaceDirectories.Count > 0 ? cli.WorkspaceDirectories : saved.Locations.Where(item => item.Kind == "workspace").Select(item => item.Path).ToArray(),
        ScanRoots = cli.ScanRoots.Count > 0 ? cli.ScanRoots : saved.Locations.Where(item => item.Kind == "scan-root").Select(item => item.Path).ToArray(),
        Profiles = cli.Profiles.Count > 0 ? cli.Profiles : saved.Locations.Where(item => item.Kind == "profile").Select(item => new ScanRootResolver.AgentProfileRoot(item.Agent!, item.Path, item.Name)).ToArray()
    };
    internal static ImmutableArray<SelectedLocation> Choices(AppOptions options, string cwd) => options.WorkspaceDirectories.Select(path => new SelectedLocation("workspace", PathIdentity.Normalize(Path.GetFullPath(path, cwd))))
        .Concat(options.ScanRoots.Select(path => new SelectedLocation("scan-root", PathIdentity.Normalize(Path.GetFullPath(path, cwd)))))
        .Concat(options.Profiles.Select(profile => new SelectedLocation("profile", PathIdentity.Normalize(Path.GetFullPath(profile.SkillsDirectory, cwd)), AgentIdentity.Normalize(profile.AgentId), profile.Name)))
        .Distinct().ToImmutableArray();
    internal static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or InvalidOperationException or OverflowException;
}
