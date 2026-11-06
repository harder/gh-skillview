using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed record RetirementReview(string Id, string Kind, string Fingerprint, bool Resuming, bool Superseded = false, bool MissingInstalledCapture = false)
{
    internal string Action => Kind == "abandon-review" ? "Abandon unapplied review" : "Discard Undo data";
    internal string Consequence => BaseConsequence + (MissingInstalledCapture ? " Abandonment also discards the saved installed capture. It may be the only remaining copy." : "") + (Superseded ? " The live copy changed since this operation; Undo was already unavailable." : "");
    private string BaseConsequence => Kind == "abandon-review"
        ? "Delete this saved candidate and review. The current live location stays unchanged."
        : "Permanently delete the retained payloads for this operation. You will lose Undo for this update. The live copy stays unchanged.";
}

internal sealed record RetirementFile(string Name, SecureFileIdentity Identity, long Bytes, string Hash);
internal sealed record RetirementDirectory(string Name, ReplacementEvidence Evidence);
internal sealed record RetirementMarker(int Version, string Id, string PlanFingerprint, string Kind,
    SecureFileIdentity State, SecureFileIdentity Operation, ReplacementObservation Before, ImmutableArray<RetirementFile> Files,
    ImmutableArray<RetirementDirectory> Directories,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementEvidence? CurrentLive = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RetirementMarker))]
[JsonSerializable(typeof(RetirementReview))]
internal partial class RetirementJsonContext : JsonSerializerContext;

internal sealed partial class ReplacementPlanStore
{
    private const string RetirementName = "retiring.json";
    private const int MaxRetirementEntries = MaxSteps + 20;
    private const int MaxRetirementBytes = 16 * 1024 * 1024;
    private static readonly string[] RetirementPayloads = ["candidate", "original", "displaced", "recorded-base"];

    // Inventory capture is deliberately inside the root -> operation lock
    // session. The first capture supplies placement exclusions only.
    internal async Task<ReplacementResult> InspectHistoryAsync(string id,
        Func<CancellationToken, Task<InventorySnapshot>> capture, IEnumerable<string> activeIds, CancellationToken token)
    {
        var active = activeIds.ToImmutableHashSet(StringComparer.Ordinal);
        var placement = await capture(token).ConfigureAwait(false);
        using var session = Open(id, placement, token, retirement: true);
        var inventory = await capture(token).ConfigureAwait(false);
        var marker = HasRetirementMarker(session.Operation, token)
            ? ReadRetirementMarker(session.Operation, token) : null;
        var observation = marker is null ? InspectPrivateRetirement(session, inventory, token).Observation
            : ObserveRetirement(session, marker, inventory, token);
        var result = Result(session.Plan, observation.Topology, true, false, false, ObservationHash(session.Plan, observation),
            "Saved private payloads and the current live location were inspected for retirement. Live files, registry records and current consumers will be left unchanged. Inspection grants no mutation approval.");
        try
        {
            RequireRetirementAdmission(session, observation, active, inventory, token, marker);
            marker ??= CaptureRetirementMarker(session, observation, token);
            var fingerprint = RetirementFingerprint(session, marker, observation, inventory, token);
            var superseded = RetirementLiveChanged(session.Plan, observation, MarkerLive(session.Plan, marker));
            return result with
            {
                Message = result.Message + (superseded ? " The live copy changed since this operation; Undo was already unavailable." : ""),
                Retirement = new(id, marker.Kind, fingerprint, HasRetirementMarker(session.Operation, token), superseded,
                    session.Plan.LocalImport is not null && observation.Original is null && marker.Directories.Any(item => item.Name == "installed-capture"))
            };
        }
        catch (IOException ex) { return result with { Message = result.Message + " Retirement protected: " + ex.Message }; }
    }

    internal async Task RetireAsync(RetirementReview review, string acknowledgement,
        Func<CancellationToken, Task<InventorySnapshot>> capture, IEnumerable<string> activeIds, CancellationToken token)
    {
        var active = activeIds.ToImmutableHashSet(StringComparer.Ordinal);
        if (acknowledgement != review.Kind || !QuarantineStore.ValidId(review.Id) || !QuarantineStore.ValidHash(review.Fingerprint))
            throw new IOException("Retirement requires the exact ID, current inspection fingerprint and separate action acknowledgement.");
        var placement = await capture(token).ConfigureAwait(false);
        using var session = Open(review.Id, placement, token, retirement: true);
        var inventory = await capture(token).ConfigureAwait(false);
        var marker = HasRetirementMarker(session.Operation, token) ? ReadRetirementMarker(session.Operation, token) : null;
        var observation = marker is null ? InspectPrivateRetirement(session, inventory, token).Observation
            : ObserveRetirement(session, marker, inventory, token);
        RequireRetirementAdmission(session, observation, active, inventory, token, marker);
        bool ownerCompleted;
        using (var completionJournal = OpenOwnerJournal(session, inventory, token, create: false))
            ownerCompleted = RetirementOwnerCompleted(session, observation.Topology, completionJournal, token);
        marker ??= CaptureRetirementMarker(session, observation, token);
        if (marker.Kind != review.Kind || RetirementFingerprint(session, marker, observation, inventory, token) != review.Fingerprint)
            throw new IOException("Retirement inspection changed. Inspect this exact operation again and approve the new fingerprint.");
        using var ownership = session.Operation.CaptureOwnership(token);
        if (!HasRetirementMarker(session.Operation, token))
        {
            // Creating the sidecar changes its parent's generation. Capture that
            // change through the already-held operation before serializing it;
            // writing the new file does not change the directory namespace.
            using (var file = session.Operation.OpenFile(RetirementName, false, token))
            {
                checkpoint?.Invoke("retirement-marker-created");
                marker = marker with { Operation = ownership.Capture(token).Identity };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, RetirementJsonContext.Default.RetirementMarker);
                if (bytes.Length > MaxRetirementBytes) throw new IOException("Retirement evidence exceeds its bounded marker budget; retained protected.");
                file.Write(bytes); file.Flush(true);
            }
            session.Operation.FlushPublication(token);
        }
        // A partial/failed marker publication is never consumed in this process.
        // Re-read and bind its exact bytes before the first native removal.
        var persisted = ReadRetirementMarker(session.Operation, token);
        if (JsonSerializer.Serialize(persisted, RetirementJsonContext.Default.RetirementMarker)
            != JsonSerializer.Serialize(marker, RetirementJsonContext.Default.RetirementMarker))
            throw new IOException("Retirement marker changed during publication; retained for fresh inspection.");
        var markerFile = CaptureRetirementFile(session, RetirementName, token);
        checkpoint?.Invoke("retirement-marker-written");
        using var liveGuard = HoldRetirementLive(session.Plan, marker, token);
        foreach (var directory in marker.Directories.OrderBy(item => Array.IndexOf(RetirementPayloads, item.Name) is var index && index >= 0 ? index : 4)
            .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            // Revalidate the ENTIRE surviving set, including native children,
            // after every completed slot. Missing whole slots are permitted;
            // partial trees, replaced children and all new names are protected.
            inventory = await capture(token).ConfigureAwait(false);
            ObserveRetirement(session, marker, inventory, token, ownerCompleted);
            RequireMarkerUnchanged();
            if (!RetirementEntries(session.Operation, token).Any(entry => entry.Name == directory.Name)) continue;
            var path = Path.Combine(session.Operation.CanonicalPath, directory.Name);
            using (var held = OwnedDirectoryLease.Open(path))
            {
                var identity = held.Capture(token).Identity;
                if (identity with { DirectoryFingerprint = null } != directory.Evidence.Identity) throw new IOException("Retained directory identity changed; inspect again.");
                checkpoint?.Invoke("retirement-before-" + directory.Name);
                // Checkpoint may cancel or race the retained tree in proof hosts.
                inventory = await capture(token).ConfigureAwait(false);
                ObserveRetirement(session, marker, inventory, token, ownerCompleted);
                VerifyRetirementDirectory(session.Operation, directory, token);
                token.ThrowIfCancellationRequested();
                RemoveRetirementTree(path, identity, directory.Evidence.Children, directory.Evidence.Entries, token, liveGuard.Revalidate);
            }
            session.Operation.FlushPublication(token);
            checkpoint?.Invoke("retirement-removed-" + directory.Name);
        }
        inventory = await capture(token).ConfigureAwait(false);
        ObserveRetirement(session, marker, inventory, token, ownerCompleted);
        RequireMarkerUnchanged();
        if (RetirementEntries(session.Operation, token).Any(entry => entry.Directory))
            throw new IOException("Retained directories remain; retirement cleanup refused.");
        checkpoint?.Invoke("retirement-before-operation");
        inventory = await capture(token).ConfigureAwait(false);
        ObserveRetirement(session, marker, inventory, token, ownerCompleted);
        RequireMarkerUnchanged();
        var finalIdentity = ownership.Capture(token).Identity;
        var operationPath = session.Operation.CanonicalPath;
        var files = marker.Files.Add(markerFile).Select(file => new PayloadObjectEvidence(file.Name, file.Identity, null)).ToImmutableArray();
        // Close Windows lock/state handles while retaining the exact owned lease
        // and root writer. Legacy disposition can complete on the final close.
        session.CloseOperation();
        token.ThrowIfCancellationRequested();
        RemoveRetirementTree(operationPath, finalIdentity, files,
            marker.Files.Add(markerFile).Select(file => new PayloadEntry(file.Name, "file", file.Hash, file.Hash, file.Bytes, null, null)).ToImmutableArray(), token, liveGuard.Revalidate);
        ownership.Dispose();
        try
        {
            checkpoint?.Invoke("retirement-before-root-flush");
            session.State.FlushPublication(token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The operation folder was removed, but parent publication was not verified. Refresh History; no complete success is claimed. " + ex.Message, ex);
        }

        void RequireMarkerUnchanged()
        {
            if (CaptureRetirementFile(session, RetirementName, token) != markerFile)
                throw new IOException("Retirement marker was replaced or changed; no further deletion admitted.");
        }
    }

    private static bool HasRetirementMarker(NativeStateDirectory operation, CancellationToken token) =>
        RetirementEntries(operation, token).Any(entry => entry.Name == RetirementName);

    private static (string Name, long Bytes, string? Error, bool Directory)[] RetirementEntries(NativeStateDirectory operation, CancellationToken token)
    {
        var entries = operation.EnumerateHistoryEntries(token).Take(MaxRetirementEntries + 1).ToArray();
        if (entries.Length > MaxRetirementEntries || entries.Any(entry => entry.Error is not null))
            throw new IOException("Unsafe or excessive retirement entries; preserve the operation for specialist inspection.");
        return entries;
    }

    private static RetirementMarker ReadRetirementMarker(NativeStateDirectory operation, CancellationToken token)
    {
        var file = RetirementEntries(operation, token).Single(entry => entry.Name == RetirementName);
        if (file.Directory || file.Bytes is <= 0 or > MaxRetirementBytes)
            throw new IOException("Malformed or oversized retirement marker; retain all remaining evidence.");
        return Read(operation, RetirementName, RetirementJsonContext.Default.RetirementMarker, token);
    }

    private void RequireRetirementAdmission(Session session, ReplacementObservation observation, ImmutableHashSet<string> active,
        InventorySnapshot inventory, CancellationToken token, RetirementMarker? marker, bool ownerCompleted = false)
    {
        if (active.Contains(session.Plan.Id)) throw new IOException("This operation is held for review or Undo. Close that review/result before retiring it.");
        if (session.IncompleteJournal || session.RecoveryJournal is not null
            || session.LatestOwnerStep is { } latest && !latest.Status.EndsWith("-complete", StringComparison.Ordinal)
            || observation.Topology is not ("ready" or "installed" or "restored"))
            throw new IOException("Only an unapplied review or a verified terminal installed/restored operation can be retired. Inspect specialist recovery first.");
        var entries = RetirementEntries(session.Operation, token);
        var plan = session.Plan;
        CheckPlacement(inventory, token);
        NativeStateDirectory.CheckExcluded(session.Operation.CanonicalPath, Exclusions(inventory));
        ValidateRetirementArchives(plan, session.Operation, entries.Where(entry => entry.Directory).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal), token);
        if (plan.SourceReview is not null && entries.Any(entry => entry.Name == ReceiptName(plan))) RequireSourceReceipt(session, token);
        if (plan.LocalDisposition is not null || entries.Any(entry => entry.Name == "local-review" || IsMergeSlot(entry.Name) || IsMergePlan(entry.Name)))
            throw new IOException("Historical local merge evidence stays protected.");
        using var journal = OpenOwnerJournal(session, inventory, token, create: false);
        journal?.RequireRetirementTerminal(observation.Topology == "ready");
        _ = RequireRetirementOwner(session, observation.Topology, journal, token, ownerCompleted);
        foreach (var entry in entries)
        {
            var expectedDirectory = RetirementPayloads.Contains(entry.Name) || entry.Name == "owner-journal"
                || plan.PinPolicy is not null && entry.Name == "installed-capture"
                || plan.LocalImport is not null && entry.Name is ("installed-capture" or "local-source-capture" or "gh-input-capture" or "gh-output-capture")
                || plan.SourceReview is { } source && (entry.Name == source.InstalledArchive || entry.Name == source.CandidateArchive);
            var expectedFile = entry.Name is "writer.lock" or "plan.json" || entry.Name == RetirementName && marker is not null
                || plan.SourceReview is not null && entry.Name == ReceiptName(plan)
                || Enumerable.Range(0, session.StepCount).Any(index => entry.Name == $"step-{index:D4}.json");
            if (entry.Directory ? !expectedDirectory : !expectedFile)
                throw new IOException("Unknown entry/type remains protected: " + entry.Name + ". No retirement was admitted.");
        }
        if (observation.Topology == "ready" && session.StepCount != 0)
            throw new IOException("An unapplied review must have no steps or owner intents; inspect specialist recovery first.");

    }

    private static void ValidateRetirementArchives(ReplacementPlan plan, NativeStateDirectory operation, HashSet<string> present, CancellationToken token)
    {
        // Marker evidence never blesses a mutable archive or arbitrary name.
        // Reapply each surviving archive's immutable format/manifest binding,
        // even when another whole approved archive was already removed.
        if (plan.SourceReview is { } review)
        {
            if (review.Version != 1 || plan.PinPolicy is not null
                || review.InstalledArchive != "review-installed-candidate" || review.CandidateArchive != "review-source-candidate"
                || !QuarantineStore.ValidHash(review.InstalledManifest) || !QuarantineStore.ValidHash(review.CandidateManifest)
                || review.Report is null || SourceUpdateReview.Hash(review.Report) != review.Fingerprint)
                throw new IOException("Retirement source-review contract is unsupported; retain protected.");
            VerifyArchive(review.InstalledArchive, review.InstalledManifest, plan.Original, false);
            VerifyArchive(review.CandidateArchive, review.CandidateManifest, plan.Candidate, false);
            if (present.Contains(review.InstalledArchive) && present.Contains(review.CandidateArchive))
                ValidateSourceReview(plan, operation, token, verifyBytes: true);
        }
        if (plan.PinPolicy is not null && present.Contains("installed-capture")) ValidatePinPolicy(plan, operation, token);
        if (plan.LocalImport is { } local)
        {
            VerifyArchive("installed-capture", local.InstalledArchiveFingerprint, plan.Original, true);
            VerifyArchive("local-source-capture", local.SourceArchiveFingerprint, null, false);
            VerifyArchive("gh-input-capture", local.Gh.InputArchiveFingerprint, null, false);
            VerifyArchive("gh-output-capture", local.Gh.OutputArchiveFingerprint, null, false);
            if (new[] { "installed-capture", "local-source-capture", "gh-input-capture", "gh-output-capture" }.All(present.Contains))
                ValidateLocalImport(plan, operation, token);
        }

        void VerifyArchive(string name, string fingerprint, ReplacementEvidence? expected, bool exactOriginal)
        {
            if (!present.Contains(name)) return;
            using var state = OpenArchive(operation, name, token);
            var archive = CapturedPayloadArchive.Load(state, fingerprint, token).Archive;
            if (expected is not null)
            {
                if (exactOriginal) VerifyUnpinOriginal(expected, archive, token);
                else if (LegacyProjection(archive.Snapshot, token).Fingerprint != expected.Payload)
                    throw new IOException("Retirement archive differs from immutable payload evidence.");
            }
            var reader = archive.OpenReader(state);
            foreach (var entry in archive.Entries.Where(entry => entry.Blob is not null)) reader.CopyEntry(entry.Entry.Path, Stream.Null, token);
        }
    }

    private RetirementMarker CaptureRetirementMarker(Session session, ReplacementObservation observation, CancellationToken token)
    {
        var entries = RetirementEntries(session.Operation, token);
        var directories = entries.Where(entry => entry.Directory).Select(entry =>
            new RetirementDirectory(entry.Name, ReplacementEvidence.From(CaptureRetirementDirectory(session.Operation, entry.Name, token))))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToImmutableArray();
        var files = entries.Where(entry => !entry.Directory).Select(entry => CaptureRetirementFile(session, entry.Name, token))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToImmutableArray();
        using var owned = session.Operation.CaptureOwnership(token);
        var live = CaptureRetirementLive(session.Plan, token);
        if (live.Slot != observation.Original || live.Parent != observation.Parent)
            throw new IOException("Current live observation changed while capturing private evidence; Inspect again.");
        RequireUnsharedRetirementChildren(live.Evidence, directories, files);
        var marker = new RetirementMarker(2, session.Plan.Id, session.Plan.ReviewFingerprint, observation.Topology == "ready" ? "abandon-review" : "discard-undo",
            RetirementStateIdentity(session, token), owned.Capture(token).Identity, observation, files, directories, live.Evidence);
        // Reserve room for the marker-create generation change, before offering
        // an action or creating any sidecar.
        if (JsonSerializer.SerializeToUtf8Bytes(marker, RetirementJsonContext.Default.RetirementMarker).Length > MaxRetirementBytes - 4096)
            throw new IOException("Retirement evidence exceeds its bounded marker budget; retain this operation for specialist inspection.");
        return marker;
    }

    private static SecureFileIdentity RetirementStateIdentity(Session session, CancellationToken token)
    {
        using var state = session.State.CaptureOwnership(token);
        return state.Capture(token).Identity with { DirectoryFingerprint = null };
    }

    private static RetirementFile CaptureRetirementFile(Session session, string name, CancellationToken token)
    {
        var operation = session.Operation;
        operation.ValidateLocation(token);
        using var opened = name == "writer.lock" ? null : operation.OpenExistingFile(name, false, token);
        var file = opened ?? session.OperationWriter;
        RetirementFile? result = null;
        file.Position = 0;
        RegularFileInspection.Capture(file, item =>
        {
            var bytes = file.Length;
            if (bytes is < 0 or > MaxJournalBytes || name == "writer.lock" && bytes != 0)
                throw new IOException("Retirement metadata file byte bound exceeded.");
            if (item.Identity.CanonicalPath != Path.Combine(operation.CanonicalPath, name))
                throw new IOException("Retirement metadata file moved or was aliased.");
            var hash = HashRetirementContent(file, bytes, token);
            result = new(name, item.Identity, bytes, hash);
            return new(hash, bytes);
        }, token);
        operation.ValidateLocation(token);
        return result!;
    }

    private ReplacementObservation ObserveRetirement(Session session, RetirementMarker marker, InventorySnapshot inventory,
        CancellationToken token, bool ownerCompleted = false)
    {
        var plan = session.Plan;
        if (marker.Version is not (1 or 2) || marker.Id != plan.Id || marker.PlanFingerprint != plan.ReviewFingerprint
            || marker.Kind is not ("abandon-review" or "discard-undo") || marker.Before is null
            || marker.Before.Topology is not ("ready" or "installed" or "restored")
            || (marker.Kind == "abandon-review") != (marker.Before.Topology == "ready")
            || marker.Files.IsDefaultOrEmpty || marker.Directories.IsDefaultOrEmpty
            || marker.Files.Length + marker.Directories.Length > MaxRetirementEntries
            || marker.Files.Any(file => file is null || file.Name == RetirementName)
            || marker.Directories.Any(directory => directory is null || directory.Evidence is null)
            || marker.Files.Select(file => file.Name).Concat(marker.Directories.Select(item => item.Name)).Distinct(StringComparer.Ordinal).Count()
                != marker.Files.Length + marker.Directories.Length)
            throw new IOException("Malformed/unsupported retirement intent. Retain all remaining evidence for specialist inspection.");
        if (marker.Version == 2 && (marker.Before.Root != (marker.CurrentLive?.Identity ?? default)
            || (marker.CurrentLive is null) != (marker.Before.Original is null)))
            throw new IOException("Retirement live evidence is inconsistent; preserve this marker unchanged.");
        foreach (var name in marker.Files.Select(file => file.Name).Concat(marker.Directories.Select(item => item.Name))) NativeStateDirectory.CheckName(name);
        foreach (var directory in marker.Directories) ValidateEvidence(directory.Evidence);

        using var owned = session.Operation.CaptureOwnership(token);
        var operation = owned.Capture(token).Identity;
        // Removing complete slots changes the operation generation. Stable plan
        // and lock identities PLUS exact surviving trees are required below;
        // the parent ID alone never authorizes changed descendants.
        if (RetirementStateIdentity(session, token) != marker.State
            || !NativeDirectoryMoveLease.SameObject(marker.Operation, operation) || marker.Operation.CanonicalPath != operation.CanonicalPath)
            throw new IOException("Retirement operation was replaced or aliased; preserve it for specialist inspection.");
        var entries = RetirementEntries(session.Operation, token);
        if (entries.Count(entry => entry.Directory) == marker.Directories.Length
            && operation with { DirectoryFingerprint = null } != marker.Operation with { DirectoryFingerprint = null })
            throw new IOException("Retirement operation generation changed without a completed slot removal; preserve protected.");
        foreach (var file in marker.Files)
            if (!entries.Any(entry => entry.Name == file.Name && !entry.Directory) || CaptureRetirementFile(session, file.Name, token) != file)
                throw new IOException("Retirement metadata changed or cleanup was interrupted within small files; automatic or name-only resume is unsafe. Preserve this operation for specialist inspection.");
        foreach (var entry in entries.Where(entry => entry.Name != RetirementName))
        {
            if (entry.Directory)
            {
                var directory = marker.Directories.SingleOrDefault(item => item.Name == entry.Name)
                    ?? throw new IOException("New retained directory is protected: " + entry.Name);
                VerifyRetirementDirectory(session.Operation, directory, token);
            }
            else if (!marker.Files.Any(file => file.Name == entry.Name)) throw new IOException("New retained file is protected: " + entry.Name);
        }
        // Bind marker topology and payload slots to the immutable plan, rather
        // than treating claimed JSON state as authority.
        var expectedSlot = marker.Before.Topology switch { "ready" => "candidate", "installed" => "original", _ => "displaced" };
        var expectedPayload = expectedSlot == "original" ? plan.Original : plan.Candidate;
        if (!marker.Directories.Any(item => item.Name == expectedSlot)
            || marker.Directories.Where(item => RetirementPayloads.Contains(item.Name)).Any(item => item.Name != expectedSlot && item.Name != "recorded-base")
            || marker.Directories.Any(item => item.Name == "recorded-base") != (plan.Revision is not null))
            throw new IOException("Retirement slot intent disagrees with the immutable terminal plan.");
        foreach (var item in marker.Directories.Where(item => RetirementPayloads.Contains(item.Name)))
        {
            var expected = item.Name == "recorded-base" ? plan.Revision!.RecordedBase! : expectedPayload;
            if (item.Evidence.Payload != expected.Payload || item.Evidence.Mode != expected.Mode
                || !item.Evidence.Children.SequenceEqual(expected.Children) || !NativeDirectoryMoveLease.SameObject(item.Evidence.Identity, expected.Identity))
                throw new IOException("Retirement slot evidence differs from the immutable plan; retained protected.");
        }
        RequireRetirementInventory(session, inventory, token);
        RequireRetirementAdmission(session, marker.Before, ImmutableHashSet<string>.Empty, inventory, token, marker, ownerCompleted);
        foreach (var directory in marker.Directories)
            RequireNoRetirementConsumers(session, directory.Name, inventory, token);
        var live = CaptureRetirementLive(plan, token);
        var expectedLive = MarkerLive(plan, marker);
        if (live.Slot != marker.Before.Original || !SameRetirementLive(expectedLive, live.Evidence)
            || live.Parent != marker.Before.Parent)
            throw new IOException("Current live copy or native parent changed since retirement started; preserve the operation for specialist inspection.");
        if (marker.Version == 1 && (!SecureRemovalBackend.TryCaptureIdentity(plan.RootPath, out var legacyRoot, out _)
            || legacyRoot != marker.Before.Root))
            throw new IOException("Legacy retirement root evidence changed; preserve the operation for specialist inspection.");
        if (marker.Before.Topology != "ready" && live.Evidence is null)
            throw new IOException("Discard Undo requires a current live copy; the retained original may be the only copy.");
        RequireUnsharedRetirementChildren(live.Evidence, marker.Directories, marker.Files.Add(CaptureRetirementFile(session, RetirementName, token)));
        return marker.Before;
    }

    private static ReplacementEvidence? MarkerLive(ReplacementPlan plan, RetirementMarker marker)
    {
        if (marker.Version == 2) return marker.CurrentLive;
        if (marker.Before.Original is not { } slot) throw new IOException("Legacy retirement lacks its captured live evidence.");
        var expected = marker.Before.Topology == "installed" ? plan.Candidate : plan.Original;
        return expected with { Identity = slot.Identity, Objects = slot.Objects, Payload = slot.Payload, Mode = slot.Mode };
    }

    private static bool SameRetirementLive(ReplacementEvidence? expected, ReplacementEvidence? current) =>
        expected is null ? current is null : current is not null && expected.Identity == current.Identity && expected.Objects == current.Objects
            && expected.Payload == current.Payload && expected.Mode == current.Mode && expected.Children.SequenceEqual(current.Children);

    private static bool RetirementLiveChanged(ReplacementPlan plan, ReplacementObservation observation, ReplacementEvidence? liveEvidence)
    {
        var expected = observation.Topology == "installed" ? plan.Candidate : plan.Original;
        return observation.Original is not { } live || !Is(live, expected) || liveEvidence is null || !liveEvidence.Children.SequenceEqual(expected.Children);
    }


    private static PayloadSnapshot CaptureRetirementDirectory(NativeStateDirectory operation, string name, CancellationToken token)
    {
        operation.ValidateLocation(token);
        var path = Path.Combine(operation.CanonicalPath, name);
        using var held = OwnedDirectoryLease.Open(path);
        var before = held.Capture(token).Identity;
        if (before.CanonicalPath != path || !before.IsDirectory || before.IsReparsePoint)
            throw new IOException("Retained directory is aliased or unsafe.");
        var snapshot = PayloadSnapshot.CaptureEvidence(path, token);
        if (snapshot.RootIdentity != before with { DirectoryFingerprint = null } || held.Capture(token).Identity != before)
            throw new IOException("Retained native directory changed during capture.");
        operation.ValidateLocation(token);
        return snapshot;
    }

    private static void RequireUnsharedRetirementChildren(ReplacementEvidence? live, ImmutableArray<RetirementDirectory> directories, ImmutableArray<RetirementFile> files)
    {
        var liveIds = live is null ? [] : live.Children.Select(child => Key(child.Identity)).Append(Key(live.Identity)).ToHashSet();
        var retainedIds = new HashSet<(ulong, ulong, ulong, long)>();
        foreach (var identity in directories.SelectMany(directory => directory.Evidence.Children.Select(child => child.Identity).Prepend(directory.Evidence.Identity)).Concat(files.Select(file => file.Identity)))
            if (liveIds.Contains(Key(identity)) || !retainedIds.Add(Key(identity)))
                throw new IOException("Hard-linked/aliased retained entries remain protected: unlinking would change shared native metadata. Preserve the operation for specialist inspection.");

        static (ulong, ulong, ulong, long) Key(SecureFileIdentity identity) => (identity.Volume, identity.FileIdLow, identity.FileIdHigh, identity.WindowsCreationTime);
    }

    private static void VerifyRetirementDirectory(NativeStateDirectory operation, RetirementDirectory directory, CancellationToken token)
    {
        var current = CaptureRetirementDirectory(operation, directory.Name, token);
        if (current.RootIdentity != directory.Evidence.Identity || current.ObjectStateFingerprint != directory.Evidence.Objects
            || current.Fingerprint != directory.Evidence.Payload || current.RootUnixMode != directory.Evidence.Mode
            || !current.Objects.SequenceEqual(directory.Evidence.Children))
            throw new IOException("Retained tree changed or is partially removed: " + directory.Name
                + ". Existing native primitives cannot prove a mid-tree resume; retain it for specialist inspection. No new content will be deleted.");
    }

    private string RetirementFingerprint(Session session, RetirementMarker marker, ReplacementObservation observation, InventorySnapshot inventory, CancellationToken token)
    {
        using var ownership = session.Operation.CaptureOwnership(token);
        var entries = RetirementEntries(session.Operation, token);
        using var journal = OpenOwnerJournal(session, inventory, token, create: false);
        var ownerEvidence = RequireRetirementOwner(session, observation.Topology, journal, token);
        var text = ownerEvidence + "\n" + JsonSerializer.Serialize(marker, RetirementJsonContext.Default.RetirementMarker) + "\n"
            + ObservationHash(session.Plan, observation) + "\n" + ownership.Capture(token).Identity + "\n"
            + string.Join('\n', entries.Select(entry => entry.Name).Order(StringComparer.Ordinal));
        if (entries.Any(entry => entry.Name == RetirementName)) text += "\n" + CaptureRetirementFile(session, RetirementName, token);
        return Hash(Encoding.UTF8.GetBytes(text));
    }

    private static SecureFileIdentity CaptureRetirementDirectoryIdentity(string path)
    {
        if (!SecureRemovalBackend.TryCaptureIdentity(path, out var identity, out var error)) throw new IOException(error);
        return identity;
    }

    private static SecureFileIdentity CaptureRetirementChild(string path, SecureFileIdentity expected, PayloadEntry content, CancellationToken token)
    {
        if (expected.IsReparsePoint)
        {
            if (!SecureRemovalBackend.TryCaptureLinkIdentity(path, out var link, out var error)) throw new IOException(error);
            return link.LinkIdentity;
        }
        if (expected.IsDirectory) return CaptureRetirementDirectoryIdentity(path);
        SecureFileIdentity identity = default;
        using var opened = RegularFileInspection.Open(path);
        RegularFileInspection.Capture(opened, item =>
        {
            identity = item.Identity;
            var file = item.Content!;
            if (file.Length != content.Bytes) throw new IOException("Retirement file content changed.");
            var hash = HashRetirementContent(file, content.Bytes, token);
            if (hash != content.ContentHash) throw new IOException("Retirement file content changed.");
            return new(hash, content.Bytes);
        }, token);
        return identity;
    }

    private static string HashRetirementContent(Stream file, long expectedBytes, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var remaining = expectedBytes;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) throw new IOException("Retirement file became shorter during capture.");
            hash.AppendData(buffer.AsSpan(0, count));
            remaining -= count;
        }
        token.ThrowIfCancellationRequested();
        if (file.ReadByte() != -1) throw new IOException("Retirement file grew during bounded capture.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void RemoveRetirementTree(string path, SecureFileIdentity identity, ImmutableArray<PayloadObjectEvidence> children, ImmutableArray<PayloadEntry> contents, CancellationToken token, Action revalidateLive)
    {
        // Payload child evidence deliberately stores an empty canonical address;
        // bind its relative name to this held, verified root for this removal.
        // All native ID/generation fields remain exact.
        var expected = children.ToDictionary(item => Path.Combine(path, item.Path.Replace('/', Path.DirectorySeparatorChar)),
            item => item with { Identity = item.Identity with { CanonicalPath = Path.Combine(path, item.Path.Replace('/', Path.DirectorySeparatorChar)) } }, StringComparer.Ordinal);
        var content = contents.ToDictionary(item => Path.Combine(path, item.Path.Replace('/', Path.DirectorySeparatorChar)), StringComparer.Ordinal);
        var observedDirectories = new HashSet<string>(StringComparer.Ordinal) { path };
        var removed = false;
        token.ThrowIfCancellationRequested();
        try
        {
            SecureRemovalBackend.RemoveTree(path, identity, false, PayloadSnapshot.MaxDepth + 2,
            child =>
            {
                token.ThrowIfCancellationRequested();
                if (!expected.TryGetValue(child, out var item)
                    || CaptureRetirementChild(child, item.Identity, content[child], token) != item.Identity)
                    throw new IOException("Unrecognized/replaced native retirement child; remaining tree protected: " + child);
                if (item.Identity.IsDirectory && !item.Identity.IsReparsePoint) observedDirectories.Add(child);
            },
            (child, directory) =>
            {
                checkpoint?.Invoke("retirement-deleting-entry");
                token.ThrowIfCancellationRequested();
                var before = child == path ? identity : expected.TryGetValue(child, out var item) ? item.Identity
                    : throw new IOException("Unknown native retirement child.");
                var current = child == path || directory ? CaptureRetirementDirectoryIdentity(child)
                    : CaptureRetirementChild(child, before, content[child], token);
                if (directory && observedDirectories.Contains(child) ? !NativeDirectoryMoveLease.SameObject(before, current) : current != before)
                    throw new IOException("Native retirement child changed before deletion; remaining evidence protected.");
                revalidateLive();
            },
            (deleted, directory) =>
            {
                if (directory && PathIdentity.Equals(deleted, path)) removed = true;
                checkpoint?.Invoke("retirement-entry-deleted");
            },
                // The general backend can continue after a reported entry
                // error. Retirement must stop at its first error; a private
                // sentinel escapes its IOException aggregation, with all held
                // frames still disposed by the backend's finally block.
                (_, error) => throw new RetirementRemovalFailure(error), token);
        }
        catch (RetirementRemovalFailure ex)
        {
            throw new IOException("Retirement removal stopped: " + ex.Message + ". Inspect History again; preserve remaining evidence. No automatic retry is permitted.", ex);
        }
        if (!removed) throw new IOException("Retirement root removal was not verified. Inspect History again; no automatic retry is permitted.");
    }

    private sealed class RetirementRemovalFailure(string message) : Exception(message);
}
