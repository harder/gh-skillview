using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Threading;

namespace SkillView.Maintenance;

/// Relocates one explicitly reviewed standalone custom-directory installation.
/// No manager records, archive extraction, replacement or recursive deletion.
internal sealed partial class QuarantineStore(string root, Logger logger, Action<string>? afterJournal = null)
{
    internal string Root { get; } = Path.GetFullPath(root);
    internal const int MaxOperations = 64;
    private const int MaxJournalBytes = 1024 * 1024;
    private static readonly HashSet<string> EntryNames = ["writer.lock", "prepared.json", "quarantined.json", "undo-prepared.json", "restored.json", "payload"];

    internal QuarantineRecord Preview(InstalledSkill target, InventorySnapshot inventory, CancellationToken token)
    {
        Admit(target, inventory, token);
        CheckStatePlacement(inventory);
        var scanRoot = inventory.ScannedRoots.Where(scan => scan.Scope == Scope.Custom
                && PathIdentity.IsInside(OwnershipPath.CanonicalCandidate(target.ResolvedPath) ?? target.ResolvedPath, OwnershipPath.CanonicalCandidate(scan.Path) ?? scan.Path))
            .OrderByDescending(scan => scan.Path.Length).FirstOrDefault()
            ?? throw new IOException("Select a standalone directory below an explicit custom --scan-root.");
        using var lease = NativeDirectoryMoveLease.Open(OwnershipPath.CanonicalCandidate(scanRoot.Path) ?? scanRoot.Path, OwnershipPath.CanonicalCandidate(target.ResolvedPath) ?? target.ResolvedPath, token);
        var before = lease.Capture(token);
        RejectOwnerArtifacts(before);
        lease.Revalidate(token);
        var context = ContextFingerprint(target, inventory);
        var review = ReviewFingerprint(target.InstallationId, before, context, lease);
        return new(1, Guid.NewGuid().ToString("N"), "preview", DateTimeOffset.UtcNow,
            target.InstallationId, target.Name, before.Path, lease.RootIdentity.CanonicalPath,
            before.Fingerprint, before.ObjectStateFingerprint!, context, review,
            before.Entries.Sum(entry => entry.Bytes), before.Entries.Length, before.RootUnixMode, target.Agents,
            lease.RootIdentity, lease.ParentIdentity, lease.TargetIdentity);
    }

    internal QuarantineResult Apply(InstalledSkill target, InventorySnapshot inventory, string expectedReview,
        bool standaloneConfirmed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!standaloneConfirmed) throw new IOException("Confirm this is a standalone copy with --standalone; native packages and manager-owned installs are excluded.");
        var preview = Preview(target, inventory, token);
        if (preview.ReviewFingerprint != expectedReview) throw new IOException("The reviewed installation/context changed. Preview again before applying.");
        using var state = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token);
        using var writer = state.OpenFile("writer.lock", true, token);
        if (writer.Length != 0) throw new IOException("Operation writer lock must be empty.");
        EnforceQuota(state, inventory, token);
        var operationPath = Path.Combine(state.CanonicalPath, preview.Id);
        using var operation = NativeStateDirectory.OpenOrCreate(operationPath, Exclusions(inventory), token);
        using var operationWriter = operation.OpenFile("writer.lock", true, token);
        if (operationWriter.Length != 0 || operation.EnumerateEntries(token).Any(entry => entry.Name != "writer.lock"))
            throw new IOException("Operation identifier is already occupied; no existing evidence will be overwritten.");
        using var lease = NativeDirectoryMoveLease.Open(preview.RootPath, preview.OriginalPath, token);
        var before = lease.Capture(token);
        if (ReviewFingerprint(target.InstallationId, before, ContextFingerprint(target, inventory), lease) != expectedReview)
            throw new IOException("The installation changed during preparation. Preview again.");
        var prepared = preview with { Status = "prepared" };
        WriteRecord(operation, "prepared.json", prepared, token);
        try
        {
            lease.MoveToState(operation, "payload", token);
            using var reconciliation = new CancellationSource(TimeSpan.FromSeconds(30),
                CancellationCallbackReporter.For(logger, "Quarantine reconciliation"));
            lease.FlushSourceNamespace(reconciliation.Token);
            operation.FlushPublication(reconciliation.Token);
            var after = lease.Capture(reconciliation.Token);
            VerifyPreserved(before, after);
            if (!PathIdentity.Equals(after.Path, Path.Combine(operation.CanonicalPath, "payload")))
                throw new IOException("Retained directory location changed; inspect the prepared operation.");
            var completed = prepared with
            {
                Status = "quarantined",
                RootAfter = lease.CurrentRootIdentity,
                ParentAfter = lease.CurrentParentIdentity,
                TargetAfter = after.RootIdentity,
                ObjectFingerprintAfter = after.ObjectStateFingerprint
            };
            WriteRecord(operation, "quarantined.json", completed, reconciliation.Token);
            return Result(completed, !token.IsCancellationRequested, true, token.IsCancellationRequested,
                "Original directory retained with its live permissions and links. Agent session state was not observed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or EntryPointNotFoundException)
        {
            if (!lease.MutationAttempted && ex is OperationCanceledException) throw;
            return Result(prepared, false, lease.MutationCompleted, token.IsCancellationRequested,
                "Operation requires inspection; its prepared journal and all retained data remain. " + ex.Message);
        }
    }

    private void CheckStatePlacement(InventorySnapshot inventory)
    {
        var candidate = OwnershipPath.CanonicalCandidate(Root) ?? Root;
        NativeStateDirectory.CheckExcluded(candidate, Exclusions(inventory).Select(path => OwnershipPath.CanonicalCandidate(path) ?? path).ToArray());
        if (ScanRootResolver.FindGitRoot(candidate) is not null)
            throw new IOException("Quarantine state must be outside Git working trees; choose SKILLVIEW_STATE_HOME accordingly.");
    }

    private static string[] Exclusions(InventorySnapshot inventory) => inventory.ConfiguredRoots.Select(scan => scan.Path)
        .Concat(inventory.ScannedRoots.Select(scan => scan.Path)).Concat(inventory.Skills.Select(skill => skill.ResolvedPath)).ToArray();

    private static void Admit(InstalledSkill target, InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (inventory.IsPartial || !inventory.ConfiguredRootsComplete || inventory.ConfiguredRoots.Length > 512)
            throw new IOException("Complete bounded agent/workspace inventory is required before quarantine or undo.");
        if (target.Scope != Scope.Custom || target.Validity != ValidityState.Valid || target.IsSymlinked
            || target.Agents.Any(agent => agent.IsSymlink || !OwnershipPath.Same(agent.Path, target.ResolvedPath))
            || target.Agents.Length > 1)
            throw new IOException($"This workflow supports one valid standalone custom-directory copy with no shared consumers (scope={target.Scope}, validity={target.Validity}, links={target.IsSymlinked}, memberships={target.Agents.Length}).");
        if (target.AllOwnershipClaims.Any() || !target.OwnershipDiagnostics.IsEmpty || !target.MetadataConflicts.IsEmpty
            || !target.FrontMatter.Diagnostics.IsEmpty || target.Pinned || target.TreeSha is not null
            || target.FrontMatter.GithubRef is not null || target.FrontMatter.LocalPath is not null
            || inventory.OwnershipRecords.Any(claim => claim.Diagnostic is not null || claim.Describes(target)))
            throw new IOException("Owner records, tracking, pins or uncertain metadata require an owner-aware maintenance workflow.");
        var components = target.ResolvedPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (components.Any(part => part.Equals("plugins", StringComparison.OrdinalIgnoreCase)
            || part.Equals("extensions", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Plugin/extension locations are excluded; use the owning manager.");
        var validation = RemoveValidator.Validate(target, inventory.ScannedRoots, inventory.Skills, token);
        if (!validation.Allowed || !validation.Warnings.IsEmpty)
            throw new IOException("Standalone quarantine policy refused: " + string.Join("; ",
                validation.Errors.Select(error => error.Detail).Concat(validation.Warnings.Select(warning => warning.Detail))));
    }

    private static string ContextFingerprint(InstalledSkill target, InventorySnapshot inventory, string? fixedId = null) => Hash(writer =>
    {
        writer.Write(fixedId ?? target.InstallationId); writer.Write(target.Name); writer.Write(target.Scope.ToString());
        foreach (var root in inventory.ConfiguredRoots.OrderBy(scan => scan.Path, StringComparer.Ordinal))
        { writer.Write(Path.GetFullPath(root.Path)); writer.Write(root.Scope.ToString()); writer.Write(root.AgentHint ?? ""); writer.Write(root.Profile ?? ""); writer.Write(root.Context ?? ""); }
        foreach (var agent in target.Agents.OrderBy(agent => agent.Path, StringComparer.Ordinal))
        { writer.Write(agent.AgentId); writer.Write(Path.GetFullPath(agent.Path)); writer.Write(agent.IsSymlink); writer.Write(agent.Context ?? ""); writer.Write(agent.Profile ?? ""); }
    });

    private static string ReviewFingerprint(string id, PayloadSnapshot payload, string context, NativeDirectoryMoveLease lease) => Hash(writer =>
    {
        writer.Write("SkillView.quarantine-review.v1"); writer.Write(id); writer.Write(payload.Path);
        writer.Write(payload.Fingerprint); writer.Write(payload.ObjectStateFingerprint!); writer.Write(context);
        writer.Write(JsonSerializer.Serialize(lease.RootIdentity, QuarantineJsonContext.Default.SecureFileIdentity));
        writer.Write(JsonSerializer.Serialize(lease.ParentIdentity, QuarantineJsonContext.Default.SecureFileIdentity));
    });

    private static string Hash(Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true)) write(writer);
        return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }

    private static void VerifyPreserved(PayloadSnapshot before, PayloadSnapshot after)
    {
        if (before.Fingerprint != after.Fingerprint || before.RootUnixMode != after.RootUnixMode
            || before.RootIdentity is not { } original || after.RootIdentity is not { } moved
            || !NativeDirectoryMoveLease.SameObject(original, moved) || !before.Objects.SequenceEqual(after.Objects))
            throw new IOException("Payload/object evidence changed during relocation; automatic recovery is refused.");
    }

    private QuarantineResult Result(QuarantineRecord record, bool succeeded, bool observed, bool canceled, string message) =>
        new(record.Id, record.Status, succeeded, observed, canceled, record.OriginalPath,
            Path.Combine(Root, record.Id, "payload"), record.ReviewFingerprint, message);

    private void WriteRecord(NativeStateDirectory operation, string name, QuarantineRecord record, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, QuarantineJsonContext.Default.QuarantineRecord);
        if (bytes.Length > MaxJournalBytes) throw new IOException("Operation journal limit exceeded.");
        using (var file = operation.OpenFile(name, false, token)) { file.Write(bytes); file.Flush(true); }
        operation.FlushPublication(token);
        afterJournal?.Invoke(name);
    }

    private static QuarantineRecord ReadRecord(NativeStateDirectory operation, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = operation.OpenExistingFile(name, false, token);
        var length = file.Length;
        if (length <= 0 || length > MaxJournalBytes) throw new IOException("Invalid operation journal length.");
        var bytes = new byte[checked((int)length)]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new IOException("Operation journal changed while reading.");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        var properties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.StartObject) properties.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) properties.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !properties.Peek().Add(reader.GetString()!))
                throw new IOException("Duplicate operation journal property.");
        }
        var record = JsonSerializer.Deserialize(bytes, QuarantineJsonContext.Default.QuarantineRecord)
            ?? throw new IOException("Operation journal is empty.");
        if (record.SchemaVersion != 1 || !ValidId(record.Id) || string.IsNullOrWhiteSpace(record.OriginalPath)
            || string.IsNullOrWhiteSpace(record.RootPath) || string.IsNullOrWhiteSpace(record.Name)
            || string.IsNullOrWhiteSpace(record.InstallationId)
            || record.Agents.IsDefault || record.Agents.Length > 1
            || record.Agents.Any(agent => agent is null || string.IsNullOrWhiteSpace(agent.AgentId)
                || string.IsNullOrWhiteSpace(agent.Path) || !Path.IsPathFullyQualified(agent.Path)
                || agent.IsSymlink || !PathIdentity.Equals(agent.Path, record.OriginalPath) && !OwnershipPath.Same(agent.Path, record.OriginalPath))
            || !Path.IsPathFullyQualified(record.OriginalPath) || !Path.IsPathFullyQualified(record.RootPath)
            || !ValidHash(record.PayloadFingerprint) || !ValidHash(record.ObjectFingerprint)
            || !ValidHash(record.ContextFingerprint) || !ValidHash(record.ReviewFingerprint)
            || record.PayloadBytes is < 0 or > PayloadSnapshot.MaxBytes || record.EntryCount is < 1 or > PayloadSnapshot.MaxEntries
            || !ValidDirectoryIdentity(record.OriginalRoot) || !ValidDirectoryIdentity(record.OriginalParent)
            || !ValidDirectoryIdentity(record.OriginalTarget)
            || !PathIdentity.Equals(record.OriginalRoot.CanonicalPath, record.RootPath)
            || !PathIdentity.Equals(record.OriginalTarget.CanonicalPath, record.OriginalPath)
            || !PathIdentity.Equals(record.OriginalParent.CanonicalPath, Path.GetDirectoryName(record.OriginalPath)!)
            || !PathIdentity.IsInside(record.OriginalPath, record.RootPath)
            || record.RootUnixMode is < 0 or > 4095)
            throw new IOException("Operation journal schema/authority is invalid.");
        return record;
    }

    private static bool ValidDirectoryIdentity(SecureFileIdentity identity) =>
        identity.IsDirectory && !identity.IsReparsePoint && !string.IsNullOrWhiteSpace(identity.CanonicalPath)
        && Path.IsPathFullyQualified(identity.CanonicalPath);

    private static void RejectOwnerArtifacts(PayloadSnapshot payload)
    {
        if (payload.Entries.Any(entry => Path.GetFileName(entry.Path) is ".skill-lock.json" or "skills-lock.json"))
            throw new IOException("Payload contains package-manager records; use the owning manager.");
    }

    internal static bool ValidId(string value) => value is { Length: 32 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool ValidHash(string value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string[] Names(NativeStateDirectory operation, CancellationToken token)
    {
        var names = new List<string>();
        foreach (var entry in operation.EnumerateEntries(token))
        {
            if (names.Count >= EntryNames.Count || !EntryNames.Contains(entry.Name) || entry.Error is not null && entry.Name != "payload")
                throw new IOException("Unexpected or unsafe operation entry.");
            names.Add(entry.Name);
        }
        return names.ToArray();
    }

    private static void EnforceQuota(NativeStateDirectory state, InventorySnapshot inventory, CancellationToken token)
    {
        var count = 0; long bytes = 0; var entries = 0;
        foreach (var entry in state.EnumerateEntries(token))
        {
            token.ThrowIfCancellationRequested();
            if (entry.Name == "writer.lock" && entry.Error is null && entry.Bytes == 0) continue;
            if (!ValidId(entry.Name) || ++count >= MaxOperations) throw new IOException("Quarantine operation quota/entry policy refused; inspect retained operations.");
            using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, entry.Name), Exclusions(inventory), token, allowCreate: false);
            var names = Names(operation, token);
            if (!names.Contains("prepared.json") || !names.Contains("restored.json") && !names.Contains("quarantined.json")
                || names.Contains("undo-prepared.json") && !names.Contains("restored.json"))
                throw new IOException("Incomplete operation needs inspection before admitting more work.");
            var prepared = ReadRecord(operation, "prepared.json", token);
            if (prepared.Id != entry.Name || prepared.Status != "prepared") throw new IOException("Invalid retained operation identity.");
            var completed = ReadRecord(operation, names.Contains("restored.json") ? "restored.json" : "quarantined.json", token);
            VerifyTransition(prepared, completed, names.Contains("restored.json") ? "restored" : "quarantined");
            if (names.Contains("restored.json") == names.Contains("payload")) throw new IOException("Retained operation locations disagree with their recorded state.");
            if (names.Contains("payload"))
            {
                using var lease = NativeDirectoryMoveLease.Open(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "payload"), token);
                var payload = lease.Capture(token);
                VerifyRecordedPayload(completed, payload);
                bytes = checked(bytes + payload.Entries.Sum(item => item.Bytes)); entries += payload.Entries.Length;
                if (bytes > MaintenanceBackupStore.MaxStoredBytes - PayloadSnapshot.MaxBytes || entries > 40_000)
                    throw new IOException("Retained payload quota reached; inspect retained operations.");
            }
        }
    }
}
