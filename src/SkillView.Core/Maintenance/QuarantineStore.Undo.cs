using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Threading;

namespace SkillView.Maintenance;

internal sealed partial class QuarantineStore
{
    internal QuarantineResult Undo(string id, InventorySnapshot inventory, bool execute, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidId(id)) throw new IOException("Undo requires a lowercase 32-character operation ID.");
        CheckStatePlacement(inventory, token);
        using var state = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token, allowCreate: false);
        using var writer = state.OpenExistingFile("writer.lock", true, token);
        if (writer.Length != 0) throw new IOException("Operation writer lock must be empty.");
        using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, id), Exclusions(inventory), token, allowCreate: false);
        using var operationWriter = operation.OpenExistingFile("writer.lock", true, token);
        if (operationWriter.Length != 0) throw new IOException("Operation writer lock must be empty.");
        var names = Names(operation, token);
        var prepared = ReadRecord(operation, "prepared.json", token);
        if (prepared.Id != id || prepared.Status != "prepared") throw new IOException("Prepared operation identity/status is invalid.");
        var original = OriginalObservation(prepared);
        AdmitRecovery(original, prepared, inventory, token);
        if (names.Contains("restored.json"))
        {
            var restored = ReadRecord(operation, "restored.json", token);
            VerifyTransition(prepared, restored, "restored");
            if (names.Contains("payload")) throw new IOException("Both restored and retained entries exist; inspect without automatic recovery.");
            using var existing = NativeDirectoryMoveLease.Open(restored.RootPath, restored.OriginalPath, token);
            var current = existing.Capture(token);
            VerifyRecordedPayload(restored, current);
            return Result(restored, true, false, false, "Already restored; current native object and payload evidence verified. No files changed.");
        }
        if (!names.Contains("quarantined.json") || names.Contains("undo-prepared.json"))
            return Result(prepared, false, false, false, "Interrupted/uncertain operation requires inspection; automatic recovery is refused.");
        var record = ReadRecord(operation, "quarantined.json", token);
        VerifyTransition(prepared, record, "quarantined");
        if (record.RootAfter is null || record.ParentAfter is null || record.TargetAfter is null
            || !ValidHash(record.ObjectFingerprintAfter!)) throw new IOException("Quarantine completion authority is incomplete.");
        var retainedPath = Path.Combine(operation.CanonicalPath, "payload");
        EnsureNoConsumers(retainedPath, inventory, token);
        EnsureNoConsumers(record.OriginalPath, inventory, token);
        RequireAbsent(record.OriginalPath);
        using (var source = NativeDirectoryMoveLease.Open(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "payload"), token))
            VerifyRecordedPayload(record, source.Capture(token));
        VerifyOriginalContext(record, token);
        if (!execute) return Result(record, true, false, false,
            "Undo preview: return the retained original directory to its vacant original path. Live permissions and links are retained.");
        WriteRecord(operation, "undo-prepared.json", record with { Status = "undo-prepared" }, token);
        // Opening after publishing the undo journal captures the private parent
        // generation after this operation's own namespace change.
        using var lease = NativeDirectoryMoveLease.Open(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "payload"), token);
        var before = lease.Capture(token);
        VerifyRecordedPayload(record, before);
        try
        {
            ManagedSkillDiscovery.RefusePaths([record.OriginalPath], token, runner);
            lease.MoveToSlot(record.RootPath, record.OriginalPath, record.RootAfter.Value, record.ParentAfter.Value, token);
            using var reconciliation = new CancellationSource(TimeSpan.FromSeconds(30),
                CancellationCallbackReporter.For(logger, "Undo reconciliation"));
            lease.FlushSourceNamespace(reconciliation.Token);
            operation.FlushPublication(reconciliation.Token);
            var after = lease.Capture(reconciliation.Token);
            VerifyPreserved(before, after);
            if (!PathIdentity.Equals(after.Path, record.OriginalPath)) throw new IOException("Restored location is uncertain.");
            var restored = record with { Status = "restored", TargetAfter = after.RootIdentity, ObjectFingerprintAfter = after.ObjectStateFingerprint };
            WriteRecord(operation, "restored.json", restored, reconciliation.Token);
            return Result(restored, !token.IsCancellationRequested, true, token.IsCancellationRequested,
                "Original directory restored and payload evidence verified. Agent session state was not observed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or EntryPointNotFoundException)
        {
            if (!lease.MutationAttempted && ex is OperationCanceledException) throw;
            return Result(record with { Status = "undo-prepared" }, false, lease.MutationCompleted, token.IsCancellationRequested,
                "Undo requires inspection; no automatic retry or replacement was attempted. " + ex.Message);
        }
    }

    internal IReadOnlyList<QuarantineResult> History(InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckStatePlacement(inventory, token);
        try { _ = File.GetAttributes(Root); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        using var state = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token, allowCreate: false);
        using var writer = state.OpenExistingFile("writer.lock", true, token);
        if (writer.Length != 0) throw new IOException("Operation writer lock must be empty.");
        var results = new List<QuarantineResult>();
        foreach (var entry in state.EnumerateEntries(token))
        {
            token.ThrowIfCancellationRequested();
            if (entry.Name == "writer.lock" && entry.Error is null && entry.Bytes == 0) continue;
            if (results.Count >= MaxOperations) throw new IOException("Operation history entry limit exceeded.");
            if (!ValidId(entry.Name))
            { results.Add(new(entry.Name, "unrecognized", false, false, false, "", "", null, "Unrecognized state entry; not followed.")); continue; }
            try
            {
                using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, entry.Name), Exclusions(inventory), token, allowCreate: false);
                using var operationWriter = operation.OpenExistingFile("writer.lock", true, token);
                if (operationWriter.Length != 0) throw new IOException("Operation writer lock must be empty.");
                var names = Names(operation, token);
                var prepared = ReadRecord(operation, "prepared.json", token);
                if (prepared.Id != entry.Name || prepared.Status != "prepared") throw new IOException("Operation identity/status mismatch.");
                var name = names.Contains("restored.json") ? "restored.json" : names.Contains("undo-prepared.json") ? "undo-prepared.json"
                    : names.Contains("quarantined.json") ? "quarantined.json" : "prepared.json";
                var record = ReadRecord(operation, name, token);
                VerifyTransition(prepared, record, Path.GetFileNameWithoutExtension(name));
                results.Add(Result(record, false, false, false,
                    "Recorded state only. Undo preview verifies current locations and payload before admitting restoration."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            { results.Add(new(entry.Name, "needs-inspection", false, false, false, "", Path.Combine(Root, entry.Name), null, ex.Message)); }
        }
        return results;
    }

    private static InstalledSkill OriginalObservation(QuarantineRecord record) => new()
    {
        Name = record.Name,
        ResolvedPath = record.OriginalPath,
        ScanRoot = record.RootPath,
        Scope = Scope.Custom,
        Agents = record.Agents,
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };

    private void AdmitRecovery(InstalledSkill original, QuarantineRecord record, InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ManagedSkillDiscovery.RefuseTarget(original, token, runner);
        if (inventory.IsPartial || !inventory.ConfiguredRootsComplete || inventory.ConfiguredRoots.Length > 512
            || !inventory.ScannedRoots.Any(scan => scan.Scope == Scope.Custom && OwnershipPath.Same(scan.Path, record.RootPath)))
            throw new IOException("Recovery requires the same explicit custom scan root and complete inventory.");
        if (inventory.OwnershipRecords.Any(claim => claim.Diagnostic is not null || claim.Describes(original))
            || ContextFingerprint(original, inventory, record.InstallationId) != record.ContextFingerprint)
            throw new IOException("Ownership/discovery context changed; review recovery instead of guessing.");
    }

    private static void VerifyOriginalContext(QuarantineRecord record, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SecureRemovalBackend.TryCaptureIdentity(record.RootPath, out var root, out var rootError)
            || root != record.RootAfter) throw new IOException("Original scan root changed: " + rootError);
        if (!SecureRemovalBackend.TryCaptureIdentity(Path.GetDirectoryName(record.OriginalPath)!, out var parent, out var parentError)
            || parent != record.ParentAfter) throw new IOException("Original parent changed: " + parentError);
        if (ScanRootResolver.FindGitRoot(record.OriginalPath) is not null) throw new IOException("Original context is now repository-managed.");
    }

    private static void VerifyRecordedPayload(QuarantineRecord record, PayloadSnapshot payload)
    {
        if (record.TargetAfter is not { } expected || payload.RootIdentity is not { } actual
            || !NativeDirectoryMoveLease.SameObject(expected, actual)
            || record.PayloadFingerprint != payload.Fingerprint || record.ObjectFingerprintAfter != payload.ObjectStateFingerprint
            || record.RootUnixMode != payload.RootUnixMode)
            throw new IOException("Retained/restored payload or native object evidence changed; automatic undo is refused.");
    }

    private static void VerifyTransition(QuarantineRecord prepared, QuarantineRecord next, string status)
    {
        if (next.Id != prepared.Id || next.Status != status || next.InstallationId != prepared.InstallationId
            || next.OriginalPath != prepared.OriginalPath || next.RootPath != prepared.RootPath
            || next.ReviewFingerprint != prepared.ReviewFingerprint || next.PayloadFingerprint != prepared.PayloadFingerprint
            || next.ObjectFingerprint != prepared.ObjectFingerprint || next.ContextFingerprint != prepared.ContextFingerprint
            || next.OriginalRoot != prepared.OriginalRoot || next.OriginalParent != prepared.OriginalParent
            || next.OriginalTarget != prepared.OriginalTarget || next.RootUnixMode != prepared.RootUnixMode
            || next.EntryCount != prepared.EntryCount || next.PayloadBytes != prepared.PayloadBytes
            || next.Name != prepared.Name || next.CreatedAt != prepared.CreatedAt || !next.Agents.SequenceEqual(prepared.Agents))
            throw new IOException("Operation transition disagrees with its prepared journal.");
    }

    private static void RequireAbsent(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException("Original path is occupied; undo never overwrites a replacement, directory or link.");
    }
}
