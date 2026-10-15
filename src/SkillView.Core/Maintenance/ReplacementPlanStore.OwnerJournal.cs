using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    private OwnerJournal? OpenOwnerJournal(Session session, InventorySnapshot inventory, CancellationToken token, bool create)
    {
        if (session.Plan.Revision?.Owner is null) return null;
        var path = Path.Combine(session.Operation.CanonicalPath, "owner-journal");
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { if (!create) return null; }
        catch (DirectoryNotFoundException) { if (!create) return null; }
        var directory = NativeStateDirectory.OpenOrCreate(path, Exclusions(inventory), token, allowCreate: create);
        FileStream? writer = null;
        try
        {
            writer = create ? directory.OpenFile("writer.lock", true, token) : directory.OpenExistingFile("writer.lock", true, token);
            CheckWriter(writer);
            return new OwnerJournal(directory, writer, session.Plan, token);
        }
        catch { writer?.Dispose(); directory.Dispose(); throw; }
    }

    private sealed class OwnerJournal : IDisposable
    {
        private const int MaxAttempts = 8;
        private const long MaxBytes = 64 * 1024 * 1024;
        private const long ReservedBytes = 12 * 1024 * 1024;
        internal NativeStateDirectory Directory { get; }
        private readonly FileStream writer;
        private readonly Dictionary<string, string> directions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RegistryWriteIntent> intents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RegistryWriteIntent> restores = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RegistryWriteCompletion> completions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RegistryWriteCompletion> restoredCompletions = new(StringComparer.Ordinal);
        private readonly HashSet<string> names = new(StringComparer.Ordinal);
        private readonly ReplacementPlan plan;
        private long bytes;
        internal OwnerJournal(NativeStateDirectory directory, FileStream writer, ReplacementPlan plan, CancellationToken token)
        {
            Directory = directory; this.writer = writer; this.plan = plan;
            var entries = directory.EnumerateEntries(token).Take(MaxAttempts * 4 + 2).ToArray();
            if (entries.Length > MaxAttempts * 4 + 1 || entries.Any(entry => entry.Error is not null || entry.Bytes < 0 || entry.Bytes > 4 * SkillLockFileReader.MaxFileBytes))
                throw new IOException("Owner journal entry limit or native policy refused.");
            bytes = entries.Sum(entry => entry.Bytes);
            if (bytes > MaxBytes) throw new IOException("Owner journal aggregate byte limit exceeded.");
            foreach (var entry in entries.Where(entry => entry.Name != "writer.lock"))
            {
                names.Add(entry.Name);
                token.ThrowIfCancellationRequested();
                if (!entry.Name.StartsWith("registry-", StringComparison.Ordinal) || entry.Name.Length < 47)
                    throw new IOException("Unrecognized owner journal entry.");
                var id = entry.Name.Substring(9, 32);
                var suffix = entry.Name[41..];
                if (!QuarantineStore.ValidId(id) || suffix is not ("-intent.json" or "-complete.json" or "-restore-intent.json" or "-restore-complete.json"))
                    throw new IOException("Unrecognized owner journal write identity.");
                directions.TryAdd(id, "unknown");
                try
                {
                    RegistryWriteBinding? binding;
                    if (suffix is "-intent.json" or "-restore-intent.json")
                    {
                        var record = Read(directory, entry.Name, RegistryWriteJsonContext.Default.RegistryWriteIntent, token);
                        binding = record.Binding;
                        if (record.SchemaVersion != 1 || record.RegistryPath != plan.Revision!.Owner!.RegistryPath
                            || record.Selector != plan.Revision.Owner.Selector || record.Before is null || record.DesiredBytes is null
                            || record.Before.Bytes is null || record.Before.Bytes.Length > SkillLockFileReader.MaxFileBytes
                            || record.DesiredBytes.Length > SkillLockFileReader.MaxFileBytes
                            || record.Before.ContentHash != NativeRegistryFileLease.Hash(record.Before.Bytes)
                            || record.DesiredHash != NativeRegistryFileLease.Hash(record.DesiredBytes)
                            || record.Before.Identity.CanonicalPath != record.RegistryPath
                            || !NativeDirectoryMoveLease.SameObject(record.Before.Identity, plan.Revision.Owner.Identity))
                            throw new IOException("Owner write evidence is inconsistent.");
                        (suffix == "-intent.json" ? intents : restores).Add(id, record);
                    }
                    else
                    {
                        var record = Read(directory, entry.Name, RegistryWriteJsonContext.Default.RegistryWriteCompletion, token);
                        binding = record.Binding;
                        if (record.SchemaVersion != 1 || record.Current is null || record.Current.Bytes is null
                            || record.Current.Bytes.Length > SkillLockFileReader.MaxFileBytes
                            || record.Current.ContentHash != NativeRegistryFileLease.Hash(record.Current.Bytes)
                            || !NativeDirectoryMoveLease.SameObject(record.Current.Identity, plan.Revision!.Owner!.Identity)
                            || record.Current.Identity.CanonicalPath != plan.Revision.Owner.RegistryPath)
                            throw new IOException("Owner completion evidence is inconsistent.");
                        (suffix == "-complete.json" ? completions : restoredCompletions).Add(id, record);
                    }
                    if (binding is null || binding.PlanFingerprint != plan.ReviewFingerprint || binding.WriteId != id
                        || binding.Direction is not ("forward" or "undo" or "repair")
                        || binding.Direction == "repair" && (binding.RecoveryDirection is not ("forward" or "undo") || !QuarantineStore.ValidId(binding.ParentWriteId!))
                        || binding.Direction != "repair" && (binding.RecoveryDirection is not null || binding.ParentWriteId is not null))
                        throw new IOException("Owner journal is not bound to this plan/direction/write.");
                    var direction = binding.Direction == "repair" ? binding.RecoveryDirection! : binding.Direction;
                    if (directions[id] != "unknown" && directions[id] != direction)
                        throw new IOException("Owner journal direction changed within a write.");
                    directions[id] = direction;
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or JournalDataException or EndOfStreamException)
                {
                    // Incomplete create-new records remain immutable evidence;
                    // they consume an attempt and never authorize a raw replay.
                }
            }
            if (directions.Count > MaxAttempts) throw new IOException("Owner journal attempt limit exceeded.");
            foreach (var id in intents.Keys) _ = RepairImage(id, new HashSet<string>(StringComparer.Ordinal), token);
            foreach (var (id, restore) in restores)
            {
                if (!intents.TryGetValue(id, out var original) || restore.Binding != original.Binding
                    || restore.BeforeEntryFingerprint is not null
                    || !restore.DesiredBytes.AsSpan().SequenceEqual(original.Before.Bytes)
                    || restore.DesiredEntryFingerprint != original.BeforeEntryFingerprint
                    || !Prefix(restore.Before.Bytes, original.DesiredBytes))
                    throw new IOException("Compensation intent is not bound to its original write/preimage.");
            }
            foreach (var (records, attempts) in new[] { (completions, intents), (restoredCompletions, restores) })
                foreach (var (id, completion) in records)
                    if (!attempts.TryGetValue(id, out var intent) || completion.Binding != intent.Binding
                        || !completion.Current.Bytes.AsSpan().SequenceEqual(intent.DesiredBytes))
                        throw new IOException("Owner completion is not bound to the exact intended image.");
        }

        // A durable main-journal write ID establishes ordering. Never select an
        // older intent by filename or accept a prefix merely because it matches
        // some historical registry image (the empty prefix matches all of them).
        internal (ReplacementOwnerRepair Review, byte[] Image)? FindRepair(Session session, RegistryFileSnapshot current, CancellationToken token)
        {
            if (intents.Keys.Concat(restores.Keys).Any(id => !session.OwnerWriteIds.Contains(id))
                || names.Any(name => (name.EndsWith("-complete.json", StringComparison.Ordinal) || name.EndsWith("-restore-intent.json", StringComparison.Ordinal))
                    && !session.OwnerWriteIds.Contains(name.Substring(9, 32)))) return null;
            var step = session.LatestOwnerStep;
            if (step?.OwnerWriteId is not { } id || !step.Status.EndsWith("-intent", StringComparison.Ordinal)
                || step.Observation.Owner is not { } observation) return null;
            if (!intents.TryGetValue(id, out var original))
            {
                // Publishing the main repair intent precedes publishing its
                // raw write intent. Missing/incomplete raw intent means that
                // this attempt could not enter NativeRegistryFileLease.Write.
                // Preserve the previously reviewed parent only while the exact
                // full torn snapshot is still unchanged.
                if (step.Status != "owner-repair-intent" || observation is not { State: "torn", Repair: { } previous }
                    || observation.Hash != current.ContentHash || observation.Identity != current.Identity
                    || Terminal(id) || !intents.ContainsKey(previous.WriteId) || Terminal(previous.WriteId)) return null;
                var retained = RepairImage(previous.WriteId, new HashSet<string>(StringComparer.Ordinal), token);
                var parentAttempt = restores.TryGetValue(previous.WriteId, out var restoration) ? restoration : intents[previous.WriteId];
                var retainedEntry = GhRegistryEditor.CaptureEntry(retained, plan.Revision!.Owner!.Selector, token);
                var retainedState = retainedEntry.Fingerprint == plan.Revision.Owner.BeforeFingerprint ? "before" : "desired";
                if (!Prefix(current.Bytes, parentAttempt.DesiredBytes)
                    || previous.Direction != directions[previous.WriteId] || previous.ImageHash != NativeRegistryFileLease.Hash(retained)
                    || previous.Bytes != retained.Length || previous.EntryState != retainedState) return null;
                return (previous, retained);
            }
            if (observation.Hash != original.Before.ContentHash || observation.Identity != original.Before.Identity
                || names.Contains($"registry-{id}-restore-complete.json")) return null;
            var attempt = original;
            if (names.Contains($"registry-{id}-restore-intent.json"))
            {
                if (!restores.TryGetValue(id, out attempt)) return null;
            }
            else if (names.Contains($"registry-{id}-complete.json")) return null;
            if (!NativeDirectoryMoveLease.SameObject(attempt.Before.Identity, current.Identity) || !Prefix(current.Bytes, attempt.DesiredBytes)) return null;
            var image = RepairImage(id, new HashSet<string>(StringComparer.Ordinal), token);
            var entry = GhRegistryEditor.CaptureEntry(image, plan.Revision!.Owner!.Selector, token);
            var state = entry.Fingerprint == plan.Revision.Owner.BeforeFingerprint ? "before" : "desired";
            return (new(id, directions[id], NativeRegistryFileLease.Hash(image), image.Length, state), image);
        }

        private byte[] RepairImage(string id, HashSet<string> chain, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!chain.Add(id) || chain.Count > MaxAttempts || !intents.TryGetValue(id, out var intent))
                throw new IOException("Repair evidence ancestry is missing or cyclic.");
            var transition = plan.Revision!.Owner!;
            if (intent.Binding!.Direction == "repair")
            {
                if (!GhRegistryWriteSession.MalformedJson(intent.Before.Bytes)
                    || intent.BeforeEntryFingerprint is not null
                    || !intents.TryGetValue(intent.Binding.ParentWriteId!, out var parent)
                    || intent.Binding.RecoveryDirection != directions[intent.Binding.ParentWriteId!]
                    || Terminal(intent.Binding.ParentWriteId!))
                    throw new IOException("Repair intent is not bound to an interrupted raw write.");
                var expected = RepairImage(intent.Binding.ParentWriteId!, chain, token);
                var attempted = restores.TryGetValue(intent.Binding.ParentWriteId!, out var restore) ? restore.DesiredBytes : parent.DesiredBytes;
                if (!Prefix(intent.Before.Bytes, attempted) || !intent.DesiredBytes.AsSpan().SequenceEqual(expected)
                    || GhRegistryEditor.CaptureEntry(expected, transition.Selector, token).Fingerprint != intent.DesiredEntryFingerprint)
                    throw new IOException("Repair image changed outside the reviewed ancestry.");
                return expected;
            }
            var forward = intent.Binding.Direction == "forward";
            var before = forward ? transition.BeforeFingerprint : transition.DesiredFingerprint;
            var desired = forward ? transition.DesiredEntry : transition.BeforeEntry;
            var patch = GhRegistryEditor.ReplaceEntry(intent.Before.Bytes, transition.Selector, before, desired, token);
            if (intent.BeforeEntryFingerprint != before || intent.DesiredEntryFingerprint != patch.DesiredEntryFingerprint
                || !patch.DesiredBytes.AsSpan().SequenceEqual(intent.DesiredBytes))
                throw new IOException("Owner intent changes bytes outside the selected saved transition.");
            return intent.Before.Bytes;
        }

        private static bool Prefix(byte[] current, byte[] attempted) => current.Length <= attempted.Length
            && current.AsSpan().SequenceEqual(attempted.AsSpan(0, current.Length));

        private bool Terminal(string id) => names.Contains($"registry-{id}-restore-complete.json")
            || names.Contains($"registry-{id}-complete.json") && !names.Contains($"registry-{id}-restore-intent.json");
        internal void Reserve(bool undo)
        {
            // Keep half the attempt/byte budget available for undo. A failed
            // forward write can never consume the last recovery allowance.
            var direction = undo ? "undo" : "forward";
            if (directions.Count >= MaxAttempts || directions.Values.Count(value => value == direction || (!undo && value is ("unknown" or "repair"))) >= MaxAttempts / 2
                || bytes + ReservedBytes > (undo ? MaxBytes : MaxBytes / 2))
                throw new IOException("Owner journal capacity for the entire transition and recovery is unavailable; no payload move was admitted.");
        }
        public void Dispose() { try { writer.Dispose(); } finally { Directory.Dispose(); } }
    }
}
