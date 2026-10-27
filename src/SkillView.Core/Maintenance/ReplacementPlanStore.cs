using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SkillView.Inventory;
using SkillView.Subprocess;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Threading;

namespace SkillView.Maintenance;

/// Saved, exact-payload replacement. Both old and displaced directories are
/// retained by exclusive native rename. Explicit terminal-operation retirement
/// removes only verified private retained evidence; live payloads are never deleted.
internal sealed partial class ReplacementPlanStore(string root, Logger logger, Action<string>? checkpoint = null, ProcessRunner? runner = null,
    Action? beforeStateOpenForTests = null, Action<OperationImpact>? subjectObserved = null, bool sourceReviewRequired = false)
{
    internal string Root { get; } = Path.GetFullPath(root);
    private const int MaxJournalBytes = 16 * 1024 * 1024;
    private const int MaxSteps = 32;
    private const int MaxPlans = 16;

    internal async Task<ReplacementResult> PlanAsync(InstalledSkill target, InventorySnapshot inventory,
        Func<NativeStateDirectory, CancellationToken, Task> stage, CancellationToken token) =>
        (await PlanCoreAsync(target, inventory, stage, null, null, token).ConfigureAwait(false)).Plan!;

    private async Task<ReplacementPreparation> PlanCoreAsync(InstalledSkill target, InventorySnapshot inventory,
        Func<NativeStateDirectory, CancellationToken, Task> stage, ReplacementRevision? revision,
        Func<NativeStateDirectory, CancellationToken, Task>? stageBase, CancellationToken token, bool allowUnchanged = false)
    {
        token.ThrowIfCancellationRequested();
        RequireHistoryCapacity(inventory, token);
        var canonicalPath = Canonical(target.ResolvedPath);
        var containingRoot = inventory.ScannedRoots.Select(scan => Canonical(scan.Path))
            .Where(path => !PathIdentity.Equals(path, canonicalPath) && PathIdentity.IsInside(canonicalPath, path))
            .OrderByDescending(path => path.Length).FirstOrDefault()
            ?? throw new IOException("The physical installation needs a containing scan root; add --scan-root for its actual directory.");
        target = target with { ResolvedPath = canonicalPath, ScanRoot = containingRoot };
        using (var initialOwner = OpenOwner(revision?.Owner, token, requireBefore: true))
            inventory = Admit(target, inventory, token, owner: initialOwner);
        CheckPlacement(inventory, token);
        using var original = NativeDirectoryMoveLease.Open(Canonical(target.ScanRoot), target.ResolvedPath, token);
        var before = original.Capture(token);
        var live = PayloadSnapshot.Capture(target.ResolvedPath, token);
        if (live.FrontMatter is not { } liveMetadata || !liveMetadata.Diagnostics.IsEmpty
            || live.Fingerprint != before.Fingerprint || live.ObjectStateFingerprint != before.ObjectStateFingerprint)
            throw new IOException("Live tracking metadata could not be bound to the captured installation; refresh before planning.");
        SameTracking(target.FrontMatter, liveMetadata);
        original.Revalidate(token);
        var afterMetadata = original.Capture(token);
        if (afterMetadata.Fingerprint != before.Fingerprint || afterMetadata.ObjectStateFingerprint != before.ObjectStateFingerprint)
            throw new IOException("The installation changed while reading tracking metadata; refresh before planning.");
        string context;
        using (var initialOwner = OpenOwner(revision?.Owner, token, requireBefore: true))
            context = Context(target, inventory, token, owner: initialOwner);
        beforeStateOpenForTests?.Invoke();
        CheckPlacement(inventory, token);
        using var state = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token);
        using var writer = state.OpenFile("writer.lock", true, token);
        CheckWriter(writer);
        CheckPlanCapacity(state, token);
        var id = Guid.NewGuid().ToString("N");
        var operationPath = Path.Combine(state.CanonicalPath, id);
        OwnedDirectoryLease? ownedOperation = null;
        var unchanged = false;
        try
        {
            var plan = await StagePlanAsync().ConfigureAwait(false);
            if (!unchanged)
                return ReplacementPreparation.Saved(Result(plan, "planned", true, false, false, plan.ReviewFingerprint,
                    "Saved exact staged payload. Review every changed path, affected consumer and pin before applying with --disposition retain-original."));
            var warning = RemoveUnchangedStaging(operationPath, ownedOperation!, state);
            token.ThrowIfCancellationRequested();
            using var owner = OpenOwner(revision?.Owner, token, requireBefore: true);
            inventory = Admit(target, inventory, token, owner: owner);
            var current = original.Capture(token);
            if (current.Fingerprint != before.Fingerprint || current.ObjectStateFingerprint != before.ObjectStateFingerprint
                || Context(target, inventory, token, owner: owner) != context)
                throw new IOException("The installation or context changed during no-op staging cleanup; refresh before checking again.");
            return ReplacementPreparation.Current(new(target.InstallationId, target.ResolvedPath, before.Fingerprint,
                "The source revision, tracking policy and payload content are current. Existing YAML presentation and delimiter whitespace were preserved. Installed objects and owner bytes were left unchanged; no saved plan was created.", warning));
        }
        finally { ownedOperation?.Dispose(); }

        async Task<ReplacementPlan> StagePlanAsync()
        {
            using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, id), Exclusions(inventory), token);
            operationPath = operation.CanonicalPath;
            if (allowUnchanged) ownedOperation = operation.CaptureOwnership(token);
            using var operationWriter = operation.OpenFile("writer.lock", true, token);
            CheckWriter(operationWriter);
            var candidatePath = Path.Combine(operation.CanonicalPath, "candidate");
            // Admission and the writer lock precede any remote staging. Staging is
            // private and never invokes a manager against the live installation.
            using var staging = operation.CreateStagingDirectory("candidate", token);
            try { await stage(staging, token).ConfigureAwait(false); staging.SealStagingDirectory(493, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { throw StagingFailure($"Staging failed; installation unchanged. Private evidence retained at {operation.CanonicalPath}. {ex.Message}", ex); }
            // No data-file lock is held while remote staging runs.
            operation.ValidateLocation(token);
            using var candidate = NativeDirectoryMoveLease.Open(operation.CanonicalPath, candidatePath, token);
            var desired = candidate.FlushPayload(token);
            checkpoint?.Invoke("candidate-metadata-read");
            var stagedMetadata = PayloadSnapshot.Capture(candidatePath, token);
            if (stagedMetadata.Fingerprint != desired.Fingerprint || stagedMetadata.ObjectStateFingerprint != desired.ObjectStateFingerprint)
                throw new IOException("Staged metadata could not be bound to the captured candidate.");
            var metadata = stagedMetadata.FrontMatter
                ?? throw new IOException("Staged skill has no supported metadata.");
            SameTracking(revision?.DesiredMetadata ?? target.FrontMatter, metadata);
            if (revision is not null)
            {
                if (metadata.Name != target.FrontMatter.Name)
                    throw new IOException("The candidate changed the authored skill name; review a source migration.");
                using var baseline = operation.CreateStagingDirectory("recorded-base", token);
                try { await stageBase!(baseline, token).ConfigureAwait(false); baseline.SealStagingDirectory(493, token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { throw StagingFailure($"Recorded-base staging failed; installation unchanged. Private evidence retained at {operation.CanonicalPath}. {ex.Message}", ex); }
                using var baseLease = NativeDirectoryMoveLease.Open(operation.CanonicalPath, baseline.CanonicalPath, token);
                var recorded = baseLease.FlushPayload(token);
                var baseMetadata = PayloadSnapshot.Capture(baseline.CanonicalPath, token).FrontMatter
                    ?? throw new IOException("Recorded base has no supported metadata.");
                SameTracking(target.FrontMatter, baseMetadata);
                revision = revision with
                {
                    DesiredMetadata = metadata,
                    RecordedBase = ReplacementEvidence.From(recorded),
                    LocalChanges = PayloadSnapshot.Differences(recorded, before)
                };
            }
            if (desired.Entries.Any(entry => Path.GetFileName(entry.Path) is ".skill-lock.json" or "skills-lock.json"))
                throw new IOException("Staged payload contains owner records; use an owner transition plan.");
            original.Revalidate(token);
            using var finalOwner = OpenOwner(revision?.Owner, token, requireBefore: true);
            inventory = Admit(target, inventory, token, owner: finalOwner);
            var current = original.Capture(token);
            if (before.ObjectStateFingerprint != current.ObjectStateFingerprint || before.Fingerprint != current.Fingerprint
                || Context(target, inventory, token, owner: finalOwner) != context)
                throw new IOException("Installed payload or context changed while staging; no replacement was admitted.");
            var subject = new ReplacementSubject(target.InstallationId, target.Name, target.Scope, target.Agents,
                target.FrontMatter, target.AllOwnershipClaims.ToImmutableArray());
            var plan = new ReplacementPlan(revision is null ? 1 : 2, id, revision is null ? "restore-recorded" : "update-revision", DateTimeOffset.UtcNow, before.Path,
                original.RootIdentity.CanonicalPath, original.RootIdentity, original.ParentIdentity,
                subject, context, ReplacementEvidence.From(before), ReplacementEvidence.From(desired),
                PayloadSnapshot.Differences(before, desired, false).AddRange(before.RootUnixMode != desired.RootUnixMode ? new string[] { "/" } : Array.Empty<string>()), "", revision);
            plan = plan with { ReviewFingerprint = PlanHash(plan) };
            var payloadsMatch = live.ComparisonFingerprint == stagedMetadata.ComparisonFingerprint
                || revision?.Source.Tree == target.TreeSha && live.GhPresentationFingerprint is { } presentation
                    && presentation == stagedMetadata.GhPresentationFingerprint;
            unchanged = allowUnchanged && IsUnchangedRevision(plan, payloadsMatch);
            if (!unchanged)
            {
                plan = FreezeSourceReview(plan, operation, original, candidate, token);
                plan = plan with { ReviewFingerprint = PlanHash(plan) };
                Write(operation, "plan.json", plan, ReplacementJsonContext.Default.ReplacementPlan, token);
            }
            return plan;
        }
    }

    private static IOException StagingFailure(string message, Exception cause) => cause is GhSourceRequestException source
        ? new GhSourceRequestException(message, source.StdOut, source.StdErr) : new IOException(message, cause);

    internal ReplacementResult Inspect(string id, InventorySnapshot inventory, CancellationToken token, string? expectedPurpose = null)
    {
        using var session = Open(id, inventory, token);
        var plan = session.Plan;
        RequirePurpose(plan, expectedPurpose);
        using var owner = OpenOwner(EffectiveOwner(plan), token, recoverySession: session, inventory: inventory, exactEntryBytes: plan.PinPolicy is not null);
        var observation = Observe(plan, session.Operation, inventory, token, owner);
        var acknowledged = plan.SourceReview is not null && session.Operation.EnumerateEntries(token).Any(entry => entry.Name == ReceiptName(plan));
        string? sourceWarning = null;
        if (acknowledged)
        {
            try { RequireSourceReceipt(session, token); }
            catch (Exception ex) when (ex is IOException or JsonException)
            { acknowledged = false; sourceWarning = "Saved source-review receipt is invalid: " + ex.Message; }
        }
        return Result(plan, observation.Topology, true, false, false, ObservationHash(plan, observation),
            (session.IncompleteJournal ? "Incomplete journal entries remain unchanged; recovery uses the reviewed current locations. " : "") + "Current locations verified against the saved original and candidate. This fingerprint is required for recovery or undo; agent session state is unknown."
            + (observation.Owner is null ? "" : " Registry owner state: " + observation.Owner.State + ".")
            + (observation.Owner?.Repair is not { } repair ? "" : $" Explicit owner repair would restore the entire retained registry image {repair.ImageHash} ({repair.Bytes} bytes, selected entry {repair.EntryState}, {repair.Direction} write {repair.WriteId}). Review its retained intent before authorizing repair. A matching prefix is evidence, not proof of which process changed the file."), observation.Owner)
            with
        { LocalSource = InspectLocalSource(plan, session.Operation, observation.Topology, inventory, token), SourceReviewAcknowledged = acknowledged, SourceReviewWarning = sourceWarning };
    }

    internal ReplacementResult Apply(string id, string expectedPlan, InventorySnapshot inventory, CancellationToken token,
        string disposition = "retain-original", string? expectedPurpose = null)
    {
        using var session = Open(id, inventory, token);
        var plan = session.Plan;
        RequirePurpose(plan, expectedPurpose);
        if (disposition != Disposition(plan))
            throw new IOException("Apply requires the exact saved disposition: " + Disposition(plan) + ".");
        using var owner = OpenOwner(EffectiveOwner(plan), token, requireBefore: true, exactEntryBytes: plan.PinPolicy is not null);
        if (expectedPlan != plan.ReviewFingerprint) throw new IOException("Saved plan fingerprint does not match the reviewed plan.");
        if (plan.LocalDisposition is null && session.Operation.EnumerateEntries(token).Any(entry => entry.Name == "local-review"))
            throw new IOException("A local merge review has started; complete its separately reviewed merged plan or create a new revision plan. The earlier replacement approval is closed.");
        if (session.StepCount != 0) throw new IOException("This plan has started. Inspect it and use explicit recovery with the current review fingerprint.");
        var observation = Observe(plan, session.Operation, inventory, token, owner);
        if (observation.Topology != "ready" || observation.Original?.Objects != plan.Original.Objects
            || observation.Candidate?.Objects != plan.Candidate.Objects
            || observation.Root != plan.RootIdentity || observation.Parent != plan.ParentIdentity)
            throw new IOException("The reviewed installation or stage changed; create a new plan.");
        if (IsUnchangedRevision(plan))
            return Result(plan, "unchanged", true, false, false, ObservationHash(plan, observation),
                "The verified payload, tracking policy and owner record are already current; no directory or registry write was attempted.", observation.Owner);
        RequireSourceReceipt(session, token);
        return Execute(session, inventory, observation, undo: false, token, owner) with { SourceReviewAcknowledged = plan.SourceReview is not null };
    }

    internal ReplacementResult Recover(string id, string expectedObservation, InventorySnapshot inventory,
        bool undo, CancellationToken token, string? expectedPurpose = null)
    {
        using var session = Open(id, inventory, token);
        RequirePurpose(session.Plan, expectedPurpose);
        using var owner = OpenOwner(EffectiveOwner(session.Plan), token, recoverySession: session, inventory: inventory, exactEntryBytes: session.Plan.PinPolicy is not null);
        if (session.Plan.LocalDisposition is null && session.StepCount == 0
            && session.Operation.EnumerateEntries(token).Any(entry => entry.Name == "local-review"))
            throw new IOException("A local merge review has started; it grants no recovery or replacement authority.");
        var observation = Observe(session.Plan, session.Operation, inventory, token, owner);
        if (expectedObservation != ObservationHash(session.Plan, observation))
            throw new IOException("Recovery locations, object evidence or context changed; inspect again before proceeding.");
        if (!undo && session.StepCount == 0 && !session.IncompleteJournal && session.RecoveryJournal is null
            && observation.Topology == "ready" && IsUnchangedRevision(session.Plan)
            && (owner is null || OwnerSatisfied(observation, owner, false)))
            return Result(session.Plan, "unchanged", true, false, false, expectedObservation,
                "The never-started plan is already current; no payload move, registry write or journal was attempted.", observation.Owner);
        if (!undo) RequireSourceReceipt(session, token);
        return Execute(session, inventory, observation, undo, token, owner) with { SourceReviewAcknowledged = !undo && session.Plan.SourceReview is not null };
    }

    private ReplacementResult Execute(Session session, InventorySnapshot inventory, ReplacementObservation current,
        bool undo, CancellationToken token, OwnerContext? owner = null)
    {
        var plan = session.Plan;
        var changed = false;
        if (!undo && (session.RecoveryJournal is not null || session.StepCount > MaxSteps - 8))
            throw new IOException("Forward journal budget is exhausted; inspect and undo before creating a new plan.");
        if (undo && (session.StepCount > MaxSteps - 6 || session.RecoveryJournal is not null))
            session.UseRecoveryJournal(Exclusions(inventory), token);
        var needed = undo ? current.Topology == "installed" ? 4 : current.Topology is "replacement-retained" or "original-retained" ? 2 : 0
            : current.Topology == "ready" ? 4 : current.Topology == "original-retained" ? 2 : 0;
        var needsOwnerWrite = owner is not null && !OwnerSatisfied(current, owner, undo);
        if (owner is not null && current.Owner?.State is not ("before" or "desired"))
            throw new IOException("The owner state needs explicit review before payload or registry changes.");
        if (owner is not null && current.Topology is ("ready" or "original-retained") && current.Owner?.State != "before")
            throw new IOException("Owner/payload phases disagree before candidate promotion; no move was admitted.");
        if (needsOwnerWrite) needed += 2;
        if (session.StepCount > MaxSteps - needed)
            throw new IOException("Journal capacity for the entire remaining transition is unavailable; no move was admitted.");
        using var ownerJournal = OpenOwnerJournal(session, inventory, token, create: needsOwnerWrite);
        if (needsOwnerWrite) ownerJournal!.Reserve(undo);
        try
        {
            if (!undo && current.Topology == "restored") throw new IOException("This plan was undone; create a new plan to install again.");
            if (undo && current.Topology == "ready")
                return Result(plan, "ready", true, false, false, ObservationHash(plan, current), "Replacement has not started; the original is still installed.", current.Owner);
            if (!undo && current.Topology == "ready")
                Move("retain", plan.RootPath, plan.OriginalPath, "original", false);
            if (!undo && current.Topology == "original-retained")
                Move("install", session.Operation.CanonicalPath, Path.Combine(session.Operation.CanonicalPath, CandidateName(plan)), null, true);
            if (undo && current.Topology == "installed")
                Move("displace", plan.RootPath, plan.OriginalPath, "displaced", false);
            if (undo && current.Topology is "replacement-retained" or "original-retained")
                Move("restore", session.Operation.CanonicalPath, Path.Combine(session.Operation.CanonicalPath, "original"), null, true);
            if (needsOwnerWrite && (undo ? current.Topology == "restored" : current.Topology == "installed")) PublishOwner();
            var success = (undo ? current.Topology is "restored" or "ready" : current.Topology == "installed")
                && (owner is null || OwnerSatisfied(current, owner, undo));
            return Result(plan, current.Topology, success, changed, token.IsCancellationRequested, ObservationHash(plan, current),
                success ? "Payload and native continuity verified. Retained directories remain available; agent session state is unknown."
                    : "No further transition was admitted; inspect the retained locations.", current.Owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or EntryPointNotFoundException)
        {
            if (!changed && ex is OperationCanceledException) throw;
            return Result(plan, "needs-recovery", false, changed, token.IsCancellationRequested, "",
                "Inspect the saved plan before recovery. All original/candidate/displaced directories are retained. " + ex.Message);
        }

        void Move(string action, string sourceRoot, string sourcePath, string? stateName, bool toOriginal)
        {
            token.ThrowIfCancellationRequested();
            // Every transition revalidates ownership and topology. Publication
            // changes the private parent generation, so open a fresh move lease.
            var admitted = Observe(plan, session.Operation, inventory, token, owner);
            if (admitted != current) throw new IOException("Locations changed between reviewed transitions.");
            if (session.StepCount > MaxSteps - 2)
                throw new IOException("Journal capacity for intent and completion is unavailable; no move was admitted.");
            Append(action + "-intent", admitted, token);
            using var lease = NativeDirectoryMoveLease.Open(sourceRoot, sourcePath, token);
            var before = lease.Capture(token);
            var expected = action is "retain" or "restore" ? plan.Original : plan.Candidate;
            VerifyPayload(expected, before);
            var sourceSlot = action switch
            {
                "retain" or "displace" => admitted.Original,
                "install" => admitted.Candidate,
                _ => admitted.Retained
            };
            if (sourceSlot?.Objects != before.ObjectStateFingerprint) throw new IOException("Move source changed after intent publication.");
            if (action == "retain")
            {
                if (plan.LocalImport is not null) checkpoint?.Invoke("local-retain-intent");
                RequireFreshPreviousPlacement(plan, inventory, token);
                RequireFreshLocalSource(plan, session.Operation, token);
            }
            try
            {
                if (toOriginal) lease.MoveToSlot(plan.RootPath, plan.OriginalPath, admitted.Root, admitted.Parent, token);
                else lease.MoveToState(session.Operation, stateName!, token);
            }
            finally { changed |= lease.MutationCompleted; }
            // Once a move commits, finish observing/journaling it even when the
            // caller cancels. Never start another forward move on this token.
            using var reconciliation = new CancellationSource(TimeSpan.FromSeconds(30),
                CancellationCallbackReporter.For(logger, "Replacement reconciliation"));
            lease.FlushSourceNamespace(reconciliation.Token);
            session.Operation.FlushPublication(reconciliation.Token);
            var after = lease.Capture(reconciliation.Token);
            VerifyPayload(expected, after);
            if (before.Fingerprint != after.Fingerprint || !before.Objects.SequenceEqual(after.Objects))
                throw new IOException("Objects changed during relocation; review recovery.");
            checkpoint?.Invoke(action + "-moved");
            current = Observe(plan, session.Operation, inventory, reconciliation.Token, owner);
            Append(action + "-complete", current, reconciliation.Token);
        }

        void PublishOwner()
        {
            var admitted = Observe(plan, session.Operation, inventory, token, owner);
            if (admitted != current) throw new IOException("Owner or payload evidence changed before owner publication.");
            checkpoint?.Invoke("owner-publish-reviewed");
            var snapshot = owner!.Held.Read(token);
            if (snapshot.ContentHash != admitted.Owner!.Hash || snapshot.Identity != admitted.Owner.Identity)
                throw new IOException("Registry changed after transition review; inspect again.");
            var writeId = Guid.NewGuid().ToString("N");
            Append(undo ? "owner-undo-intent" : "owner-forward-intent", admitted, token, writeId);
            var result = owner.Held.WriteSelection(undo ? owner.Transition.DesiredFingerprint : owner.Transition.BeforeFingerprint,
                undo ? owner.Transition.BeforeEntry : owner.Transition.DesiredEntry, ownerJournal!.Directory, writeId, token,
                snapshot, new(plan.ReviewFingerprint, undo ? "undo" : "forward", writeId));
            changed |= result.MutationAttempted;
            if (!result.Succeeded) throw new IOException("Owner publication needs recovery. " + result.Error);
            checkpoint?.Invoke("owner-written");
            using var reconciliation = new CancellationSource(TimeSpan.FromSeconds(30), CancellationCallbackReporter.For(logger, "Owner reconciliation"));
            current = Observe(plan, session.Operation, inventory, reconciliation.Token, owner);
            Append(undo ? "owner-undo-complete" : "owner-forward-complete", current, reconciliation.Token, writeId);
        }

        void Append(string status, ReplacementObservation observation, CancellationToken ct, string? ownerWriteId = null)
        {
            if (session.StepCount >= MaxSteps) throw new IOException("Recovery journal step limit reached; retain evidence for inspection.");
            var step = new ReplacementStep(session.StepCount, plan.ReviewFingerprint, status, observation, ownerWriteId);
            Write(session.Journal, $"step-{session.StepCount:D4}.json", step, ReplacementJsonContext.Default.ReplacementStep, ct);
            session.StepCount++;
            if (ownerWriteId is not null) { session.LatestOwnerStep = step; session.OwnerWriteIds.Add(ownerWriteId); }
        }
    }

    private ReplacementObservation Observe(ReplacementPlan plan, NativeStateDirectory operation,
        InventorySnapshot inventory, CancellationToken token, OwnerContext? owner = null)
    {
        var subject = Subject(plan);
        inventory = Admit(subject, inventory, token, plan, owner);
        if (Context(subject, inventory, token, plan.Subject.InstallationId, owner) != plan.ContextFingerprint)
            throw new IOException("Ownership, exposures or configured roots changed; the saved plan needs renewed review.");
        operation.ValidateLocation(token);
        if (plan.Revision?.RecordedBase is { } baseline)
        {
            using var baseLease = NativeDirectoryMoveLease.Open(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "recorded-base"), token);
            VerifyPayload(baseline, baseLease.Capture(token));
        }
        foreach (var privateName in plan.Revision is null ? new[] { "candidate", "original", "displaced" }
            : new[] { "candidate", "original", "displaced", "recorded-base" })
            QuarantineStore.EnsureNoConsumers(Path.Combine(operation.CanonicalPath, privateName), inventory, token);
        foreach (var attempt in operation.EnumerateEntries(token).Where(entry => IsMergeSlot(entry.Name)))
            QuarantineStore.EnsureNoConsumers(Path.Combine(operation.CanonicalPath, attempt.Name), inventory, token);
        var original = Slot(plan.RootPath, plan.OriginalPath);
        var retained = Slot(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "original"));
        var candidate = Slot(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, CandidateName(plan)));
        var displaced = Slot(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "displaced"));
        var topology = (original?.Payload == plan.Original.Payload && Is(original, plan.Original), retained, candidate, displaced) switch
        {
            (true, null, not null, null) when Is(candidate, plan.Candidate) => "ready",
            (_, not null, not null, null) when original is null && Is(retained, plan.Original) && Is(candidate, plan.Candidate) => "original-retained",
            (_, not null, null, null) when Is(original, plan.Candidate) && Is(retained, plan.Original) => "installed",
            (_, not null, null, not null) when original is null && Is(retained, plan.Original) && Is(displaced, plan.Candidate) => "replacement-retained",
            (true, null, null, not null) when Is(displaced, plan.Candidate) => "restored",
            _ => throw new IOException("Saved replacement locations disagree, are occupied or contain an unexpected object; no transition was admitted.")
        };
        if (!SecureRemovalBackend.TryCaptureIdentity(plan.RootPath, out var rootIdentity, out var rootError)
            || !SecureRemovalBackend.TryCaptureIdentity(Path.GetDirectoryName(plan.OriginalPath)!, out var parentIdentity, out rootError)
            || !NativeDirectoryMoveLease.SameObject(plan.RootIdentity, rootIdentity)
            || !NativeDirectoryMoveLease.SameObject(plan.ParentIdentity, parentIdentity)
            || rootIdentity.CanonicalPath != plan.RootPath || parentIdentity.CanonicalPath != plan.ParentIdentity.CanonicalPath)
            throw new IOException("Original root/parent authority changed: " + rootError);
        VerifyLocalDisposition(plan, operation, inventory, topology, original, retained, candidate, displaced, token);
        VerifyPinPolicy(plan, operation, topology, original, retained, candidate, displaced, token);
        VerifyLocalImport(plan, operation, topology, original, retained, candidate, displaced, token);
        return new(topology, original, retained, candidate, displaced, rootIdentity, parentIdentity, owner?.Observe(token));

        ReplacementSlot? Slot(string scanRoot, string path)
        {
            token.ThrowIfCancellationRequested();
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            using var lease = NativeDirectoryMoveLease.Open(scanRoot, path, token);
            var payload = lease.Capture(token);
            var expected = NativeDirectoryMoveLease.SameObject(plan.Original.Identity, payload.RootIdentity!.Value)
                ? plan.Original : plan.Candidate;
            VerifyPayload(expected, payload);
            return new(path, payload.Fingerprint, payload.ObjectStateFingerprint!, payload.RootUnixMode, payload.RootIdentity.Value);
        }
    }

    private static bool Is(ReplacementSlot? slot, ReplacementEvidence expected) => slot is not null
        && slot.Payload == expected.Payload && slot.Mode == expected.Mode
        && NativeDirectoryMoveLease.SameObject(slot.Identity, expected.Identity);

    private static void VerifyPayload(ReplacementEvidence expected, PayloadSnapshot payload)
    {
        if (expected.Payload != payload.Fingerprint || expected.Mode != payload.RootUnixMode
            || !NativeDirectoryMoveLease.SameObject(expected.Identity, payload.RootIdentity!.Value)
            || !expected.Children.SequenceEqual(payload.Objects))
            throw new IOException("Original or staged payload/native children changed; no replacement or undo was admitted.");
    }

    private static InstalledSkill Subject(ReplacementPlan plan) => new()
    {
        Name = plan.Subject.Name,
        ResolvedPath = plan.OriginalPath,
        ScanRoot = plan.RootPath,
        Scope = plan.Subject.Scope,
        Agents = plan.Subject.Agents,
        FrontMatter = plan.Subject.Metadata,
        OwnershipClaims = plan.Subject.Owners,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = plan.Subject.Agents.Any(agent => agent.IsSymlink),
        InstalledAt = null
    };

    private InventorySnapshot Admit(InstalledSkill target, InventorySnapshot inventory, CancellationToken token,
        ReplacementPlan? activePlan = null, OwnerContext? owner = null, bool localReimport = false)
    {
        token.ThrowIfCancellationRequested();
        inventory = owner?.RecoveryInventory(inventory, token) ?? inventory;
        if (inventory.IsPartial || !inventory.ConfiguredRootsComplete || inventory.ConfiguredRoots.Length > 512
            || target.Validity != ValidityState.Valid || !target.MetadataConflicts.IsEmpty
            || !target.OwnershipDiagnostics.IsEmpty || !target.FrontMatter.Diagnostics.IsEmpty)
            throw new IOException("Saved replacement requires complete inventory and unambiguous metadata.");
        if (localReimport || activePlan?.LocalImport is not null)
        {
            RequireLocalSourceMetadata(target.FrontMatter, true);
            if (target.AllOwnershipClaims.Any()) throw new IOException("Local reimport requires an ownerless installation.");
        }
        else if (target.FrontMatter.LocalPath is not null || string.IsNullOrWhiteSpace(target.FrontMatter.Upstream)
            || target.TreeSha is not { Length: 40 } tree || !tree.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(target.FrontMatter.GithubRef))
            throw new IOException("Recorded restore requires complete inventory and an unambiguous gh remote revision. Use --allow-hidden-dirs when hidden agent containers make coverage partial.");
        if (!inventory.ScannedRoots.Any(scan => OwnershipPath.Same(scan.Path, target.ScanRoot)))
            throw new IOException("The original scan root must be included in the current inventory.");
        if (target.AllOwnershipClaims.Any(claim => claim.Provider != "gh-vercel-global" || claim.ReadOnly || claim.IsExclusiveOwner)
            || inventory.OwnershipRecords.Any(claim => claim.Diagnostic is not null))
            throw new IOException("Native/exclusive or uncertain owner evidence requires an owner transition plan.");
        ValidateOwners(target, token, owner);
        var capturedClaims = target.AllOwnershipClaims.ToArray();
        var freshClaims = ReadOwners(target, token, owner).Select(claim => owner?.Normalize(claim) ?? claim).ToImmutableArray();
        if (!freshClaims.IsEmpty)
        {
            var records = freshClaims.Where(claim => claim.Provider == "gh-vercel-global" && claim.Selector is not null)
                .ToImmutableDictionary(claim => claim.Selector!, claim => new GhSharedLockGuard.Record(claim.SourceUrl,
                    claim.Source, claim.SourceType, claim.SkillPath, claim.ContentHash, claim.PinnedRef), StringComparer.Ordinal);
            GhSharedLockGuard.ValidateUpdateRecords(records, [target]);
        }
        if (freshClaims.Any(claim => !capturedClaims.Any(captured => captured.LockFilePath == claim.LockFilePath
                && captured.Selector == claim.Selector && captured.EntryFingerprint == claim.EntryFingerprint
                && captured.Provider == claim.Provider && captured.ReadOnly == claim.ReadOnly
                && captured.IsExclusiveOwner == claim.IsExclusiveOwner)))
            throw new IOException("Live contextual owner evidence changed; refresh before replacement.");
        var inaccessible = new List<string>();
        var locations = new List<DiscoveryLocation>();
        var liveRoots = new List<ScanRoot>();
        foreach (var configured in inventory.ConfiguredRoots.Concat(inventory.ScannedRoots).Distinct())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(configured.Path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                    throw new IOException("Configured skill root is no longer a directory.");
                liveRoots.Add(configured);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        var fresh = new LocalSkillScanner(logger).ScanWithCancellation(liveRoots, new LocalSkillScanner.Options(AllowHiddenDirs: true), token, inaccessible, locations);
        if (inaccessible.Count != 0 || locations.Any(location => location.State == "partial"))
            throw new IOException("Live consumer discovery is incomplete; replacement is refused.");
        var matching = inventory.Skills.Concat(fresh).Where(skill => OwnershipPath.Same(skill.ResolvedPath, target.ResolvedPath)).ToArray();
        if (matching.Any(skill => skill.Agents.Any(agent => !target.Agents.Any(saved =>
                saved.AgentId == agent.AgentId && OwnershipPath.Same(saved.Path, agent.Path)
                && saved.IsSymlink == agent.IsSymlink && saved.Profile == agent.Profile && saved.Context == agent.Context))))
            throw new IOException("The installation's discovered consumers changed; create a newly reviewed plan.");
        if (inventory.OwnershipRecords.Where(claim => claim.Describes(target)).Select(claim => owner?.Normalize(claim) ?? claim).Any(claim =>
                !capturedClaims.Any(captured => captured.LockFilePath == claim.LockFilePath
                    && captured.Selector == claim.Selector && captured.EntryFingerprint == claim.EntryFingerprint
                    && captured.Provider == claim.Provider && captured.ReadOnly == claim.ReadOnly
                    && captured.IsExclusiveOwner == claim.IsExclusiveOwner)))
            throw new IOException("A new or changed contextual owner claims this installation; review its ownership.");
        var knownPaths = target.Agents.Select(agent => Path.GetFullPath(agent.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var agent in target.Agents)
        {
            token.ThrowIfCancellationRequested();
            if (OwnershipPath.EntryAddress(Path.TrimEndingDirectorySeparator(agent.Path)) is not { } entry)
                throw new IOException("Captured consumer entry cannot be inspected.");
            if (agent.IsSymlink)
            {
                if (!PathResolver.IsSymlink(agent.Path) || !OwnershipPath.Same(
                    QuarantineStore.ResolveExposureAddress(agent.Path, token), target.ResolvedPath))
                    throw new IOException("Captured consumer link changed its intended destination.");
            }
            else if (!OwnershipPath.Same(entry, target.ResolvedPath))
                throw new IOException("A consumer is nested in the replacement payload; review its topology first.");
        }
        // Broken captured links may become separate inventory rows while the
        // original slot is vacant. They are expected only at captured addresses.
        var others = inventory with
        {
            Skills = inventory.Skills.Concat(fresh).Where(skill => !OwnershipPath.Same(skill.ResolvedPath, target.ResolvedPath)
                && !(skill.Validity == ValidityState.BrokenSymlink && !skill.Agents.IsEmpty
                    && skill.Agents.All(agent => knownPaths.Contains(Path.GetFullPath(agent.Path))))
                && !ExpectedNamespace(skill)).Append(target).ToImmutableArray()
        };
        QuarantineStore.EnsureNoConsumers(target.ResolvedPath, others, token, target);
        return others;

        bool ExpectedNamespace(InstalledSkill skill)
        {
            // Removing the only payload makes the scanner report its namespace
            // parent as malformed, even with a README present. That observation
            // can also remain in the caller's inventory after child restoration.
            // Only the saved native parent without its own skill/Git metadata
            // can be this container; all other consumer rows remain in policy.
            return activePlan is not null && skill.Validity == ValidityState.MissingSkillMd
                && !skill.IsSymlinked && !skill.AllOwnershipClaims.Any() && skill.OwnershipDiagnostics.IsEmpty
                && OwnershipPath.Same(skill.ResolvedPath, activePlan.ParentIdentity.CanonicalPath)
                && SecureRemovalBackend.TryCaptureDirectoryValidationWithinRoot(activePlan.RootPath,
                    activePlan.ParentIdentity.CanonicalPath, out var snapshot, out _, token)
                && !snapshot.Directory.HasSkillFile && !snapshot.Directory.HasGitDirectory
                && NativeDirectoryMoveLease.SameObject(activePlan.ParentIdentity, snapshot.Directory.Identity);
        }
    }

    private string Context(InstalledSkill target, InventorySnapshot inventory, CancellationToken token, string? fixedId = null, OwnerContext? owner = null)
    {
        token.ThrowIfCancellationRequested();
        ValidateOwners(target, token, owner);
        var roots = inventory.ConfiguredRoots.OrderBy(scan => scan.Path, StringComparer.Ordinal).ToArray();
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
        {
            writer.Write("SkillView.replacement-context.v1"); writer.Write(fixedId ?? target.InstallationId);
            writer.Write(runner?.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") ?? "");
            writer.Write(runner?.GetEffectiveEnvironmentVariable("XDG_STATE_HOME") ?? "");
            foreach (var scan in roots)
            { writer.Write(scan.Path); writer.Write(scan.Scope.ToString()); writer.Write(scan.AgentHint ?? ""); writer.Write(scan.Context ?? ""); writer.Write(scan.Profile ?? ""); }
            foreach (var agent in target.Agents.OrderBy(agent => agent.Path, StringComparer.Ordinal))
            {
                writer.Write(agent.AgentId); writer.Write(agent.Path); writer.Write(agent.IsSymlink);
                writer.Write(agent.Context ?? ""); writer.Write(agent.Profile ?? "");
                writer.Write(agent.IsSymlink ? new DirectoryInfo(agent.Path).LinkTarget ?? "" : "");
            }
            foreach (var claim in target.AllOwnershipClaims.OrderBy(claim => claim.LockFilePath, StringComparer.Ordinal).ThenBy(claim => claim.Selector, StringComparer.Ordinal))
            { writer.Write(claim.LockFilePath ?? ""); writer.Write(claim.Selector ?? ""); writer.Write(claim.EntryFingerprint ?? ""); }
        }
        return Hash(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
    }

    private ImmutableArray<SkillPackage> ReadOwners(InstalledSkill target, CancellationToken token, OwnerContext? owner = null)
    {
        ManagedSkillDiscovery.RefuseTarget(target, token, runner);
        var diagnostics = new List<LockReadDiagnostic>();
        var destinations = target.Agents.Select(agent => agent.DiscoveryRoot ?? Path.GetDirectoryName(agent.Path)!)
            .Append(target.ScanRoot).Append(Path.GetDirectoryName(target.ResolvedPath)!)
            .Distinct(StringComparer.Ordinal).ToArray();
        var home = runner?.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        var stateHome = runner?.GetEffectiveEnvironmentVariable("XDG_STATE_HOME");
        var cwd = runner?.EffectiveWorkingDirectory ?? Environment.CurrentDirectory;
        if (home is not null) home = Path.GetFullPath(home, cwd);
        if (stateHome is not null) stateHome = Path.GetFullPath(stateHome, cwd);
        var observed = new SkillLockFileReader(logger).LoadObservations(destinations, token, new(home, stateHome), diagnostics,
            owner is null ? null : path => owner.ReadFor(path, token));
        if (diagnostics.Any(diagnostic => diagnostic.CoveredRoots.IsEmpty || diagnostic.CoveredRoots.Any(rootPath =>
                destinations.Any(destination => OwnershipPath.Same(rootPath, destination)))))
            throw new IOException("Live owner discovery is incomplete; replacement is refused.");
        return observed.Select(observation => observation.Package).Where(claim => claim.Describes(target)).ToImmutableArray();
    }

    internal static void SameTracking(SkillFrontMatter original, SkillFrontMatter desired)
    {
        if (original.Upstream != desired.Upstream || original.GithubTreeSha != desired.GithubTreeSha
            || original.GithubRef != desired.GithubRef || original.GithubPath != desired.GithubPath
            || original.PinnedRef != desired.PinnedRef || original.LocalPath != desired.LocalPath || original.Pinned != desired.Pinned)
            throw new IOException("Recorded restore cannot change source, revision, pin policy or owner records.");
    }

    private void CheckPlacement(InventorySnapshot inventory, CancellationToken token)
    {
        ManagedSkillDiscovery.RefusePaths([Root], token, runner);
        NativeStateDirectory.CheckExcluded(Canonical(Root), Exclusions(inventory).Select(Canonical).ToArray());
        if (ScanRootResolver.FindGitRoot(Root) is not null) throw new IOException("Replacement state must remain outside Git working trees.");
    }
    private static string Canonical(string path) => OwnershipPath.CanonicalCandidate(path) ?? Path.GetFullPath(path);
    private static string[] Exclusions(InventorySnapshot inventory) => inventory.ProtectedRoots
        .Select(scan => scan.Path).Concat(inventory.Skills.Select(skill => skill.ResolvedPath)).ToArray();
    private static void CheckWriter(FileStream writer) { if (writer.Length != 0) throw new IOException("Replacement writer lock must be empty."); }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string PlanHash(ReplacementPlan plan)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(plan with { ReviewFingerprint = "" }, ReplacementJsonContext.Default.ReplacementPlan));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) WriteCanonical(document.RootElement, writer);
        return Hash(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
    }

    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(item, writer);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
    private static string ObservationHash(ReplacementPlan plan, ReplacementObservation observation) => Hash(
        System.Text.Encoding.UTF8.GetBytes(plan.ReviewFingerprint + "\n" + JsonSerializer.Serialize(observation, ReplacementJsonContext.Default.ReplacementObservation)));

    private ReplacementResult Result(ReplacementPlan plan, string status, bool succeeded, bool changed, bool canceled, string review, string message, ReplacementOwnerObservation? owner = null) =>
        new(plan.Id, status, succeeded, changed, canceled, plan.OriginalPath, Path.Combine(Root, plan.Id, "original"),
            Path.Combine(Root, plan.Id, CandidateName(plan)), Path.Combine(Root, plan.Id, "displaced"),
            plan.ReviewFingerprint, review, plan.ChangedPaths, message, plan.Subject, Path.Combine(Root, plan.Id, PlanName(plan)),
            plan.Original.Mode, plan.Candidate.Mode, plan.Revision, owner, plan.LocalDisposition, plan.PinPolicy, plan.LocalImport, plan.LocalSourceSwitch)
        {
            SourceReview = plan.SourceReview,
            Impact = PlanImpact(plan, status),
            Purpose = plan.Purpose,
            Comparison = MaintenancePlanComparison.From(plan)
        };

    private OperationImpact PlanImpact(ReplacementPlan plan, string? status = null) => OperationImpact.Payload(
        OperationSubject.FromRecorded(plan.Subject.InstallationId, plan.OriginalPath, plan.Subject.Name,
            plan.Subject.Scope, plan.Subject.Metadata, plan.Subject.Agents, plan.Subject.Owners, plan.CreatedAt,
            NativeObjectObservation.From(plan.Original.Identity, plan.CreatedAt, "saved-original-evidence"),
            plan.Original.Payload), plan.Purpose) with
    {
        PayloadRetained = true,
        RetainedPayloadPath = status switch
        {
            "planned" or "ready" or "unchanged" or "restored" or "preserved" => plan.OriginalPath,
            "original-retained" or "installed" or "replacement-retained" => Path.Combine(Root, plan.Id, "original"),
            _ => null
        },
        RetentionEvidence = status is "planned" or "ready" or "unchanged" or "restored" or "preserved" or "original-retained" or "installed" or "replacement-retained"
            ? "result-observed-original-location; later changes unknown" : "saved preservation contract; current location requires inspection"
    };

    private void Write<T>(NativeStateDirectory operation, string name, T value, JsonTypeInfo<T> type, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (bytes.Length > MaxJournalBytes) throw new IOException("Replacement journal byte budget exceeded.");
        using (var file = operation.OpenFile(name, false, token)) { file.Write(bytes); file.Flush(true); }
        operation.FlushPublication(token);
        checkpoint?.Invoke(name);
    }

    private static T Read<T>(NativeStateDirectory operation, string name, JsonTypeInfo<T> type, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = operation.OpenExistingFile(name, false, token);
        var length = file.Length;
        if (length is <= 0 or > MaxJournalBytes) throw new JournalDataException("Invalid replacement journal length.");
        var bytes = new byte[checked((int)length)]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new IOException("Replacement journal changed while reading.");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 48 });
        var properties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.StartObject) properties.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) properties.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !properties.Peek().Add(reader.GetString()!))
                throw new JournalDataException("Duplicate replacement journal property.");
        }
        return JsonSerializer.Deserialize(bytes, type) ?? throw new IOException("Empty replacement journal.");
    }

    private Session Open(string id, InventorySnapshot inventory, CancellationToken token, bool retirement = false)
    {
        token.ThrowIfCancellationRequested();
        if (!QuarantineStore.ValidId(id)) throw new IOException("A lowercase 32-character plan ID is required.");
        CheckPlacement(inventory, token);
        NativeStateDirectory? state = null, operation = null;
        FileStream? writer = null, operationWriter = null;
        try
        {
            state = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token, allowCreate: false);
            writer = state.OpenExistingFile("writer.lock", true, token); CheckWriter(writer);
            operation = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, id), Exclusions(inventory), token, allowCreate: false);
            operationWriter = operation.OpenExistingFile("writer.lock", true, token); CheckWriter(operationWriter);
            var operationNames = operation.EnumerateEntries(token).Take(MaxSteps + 12 + 2 * MaxMergeAttempts + MaxSourceReviewEntries).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            var retiring = operationNames.Contains("retiring.json");
            if (retiring && !retirement) throw new IOException("Retirement was started; use History Inspect and explicit retirement approval. Recovery/apply/undo remain closed.");
            var attemptCount = 0;
            while (attemptCount < MaxMergeAttempts && (operationNames.Contains($"merged-{attemptCount:D2}")
                || operationNames.Contains($"merge-plan-{attemptCount:D2}.json"))) attemptCount++;
            var attemptIds = operationNames.Where(IsMergeSlot).Select(name => name[7..])
                .Concat(operationNames.Where(IsMergePlan).Select(name => name[11..13])).Distinct(StringComparer.Ordinal).Count();
            if (attemptIds != attemptCount)
                throw new IOException("Merged attempt history has an unknown entry or gap.");
            var latestPlanName = $"merge-plan-{attemptCount - 1:D2}.json";
            var hasMergePlan = attemptCount > 0 && operationNames.Contains(latestPlanName);
            ReplacementPlan? mergedPlan = null;
            if (hasMergePlan)
            {
                try { mergedPlan = Read(operation, latestPlanName, ReplacementJsonContext.Default.ReplacementPlan, token); }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // Never revive an older completed attempt. The base plan is
                    // inspectable for a private retry, but local-review keeps its
                    // former apply/recovery approval permanently closed.
                    logger.Warn("replacement", $"Replacement {id}: incomplete merged plan retained: {ex.Message}");
                    hasMergePlan = false;
                }
            }
            ReplacementPlan plan;
            try { plan = mergedPlan ?? Read(operation, "plan.json", ReplacementJsonContext.Default.ReplacementPlan, token); }
            catch (Exception ex) when (retiring && ex is IOException or JsonException)
            {
                throw new IOException("Retirement cleanup left an unavailable saved plan. Small-file progress cannot be proved safely; preserve this operation for specialist inspection at " + operation.CanonicalPath + ". " + ex.Message, ex);
            }
            if (!((plan.LocalSourceSwitch is null && plan.LocalImport is null && (plan.SchemaVersion == 1 && plan.Purpose == "restore-recorded" && plan.Revision is null && plan.LocalDisposition is null && plan.PinPolicy is null && !hasMergePlan
                    || plan.SchemaVersion == 2 && plan.Purpose == "update-revision" && plan.Revision is not null && plan.LocalDisposition is null && plan.PinPolicy is null && !hasMergePlan
                    || plan.SchemaVersion == 3 && plan.Purpose == "update-revision" && plan.Revision is not null && plan.LocalDisposition is not null && plan.PinPolicy is null && hasMergePlan
                    || plan.SchemaVersion == 4 && plan.Purpose == "unpin-only" && plan.Revision is null && plan.LocalDisposition is null && plan.PinPolicy is not null && !hasMergePlan))
                    || plan.SchemaVersion == 5 && plan.Purpose == LocalReimportPurpose && plan.LocalSourceSwitch is null && plan.Revision is null && plan.LocalDisposition is null && plan.PinPolicy is null && plan.LocalImport is not null && !hasMergePlan
                    || plan.SchemaVersion == 6 && plan.Purpose == LocalSourceSwitchPurpose && plan.LocalSourceSwitch is not null && plan.Revision is null && plan.LocalDisposition is null && plan.PinPolicy is null && plan.LocalImport is not null && !hasMergePlan)
                || plan.Id != id
                || plan.LocalDisposition is { } disposition && (!IsMergeSlot(disposition.MergedSlot) || PlanName(plan) != latestPlanName)
                || plan.Subject is null || plan.Original is null || plan.Candidate is null || plan.Subject.Metadata is null
                || PlanHash(plan) != plan.ReviewFingerprint
                || !QuarantineStore.ValidHash(plan.ReviewFingerprint) || !QuarantineStore.ValidHash(plan.ContextFingerprint)
                || !Path.IsPathFullyQualified(plan.OriginalPath) || !Path.IsPathFullyQualified(plan.RootPath)
                || !PathIdentity.IsInside(plan.OriginalPath, plan.RootPath)
                || plan.RootIdentity.CanonicalPath != plan.RootPath || plan.Original.Identity.CanonicalPath != plan.OriginalPath
                || plan.Candidate.Identity.CanonicalPath != Path.Combine(operation.CanonicalPath, CandidateName(plan))
                || plan.Subject.Agents.IsDefault || plan.Subject.Owners.IsDefault || plan.ChangedPaths.IsDefault
                || plan.Subject.Agents.Any(agent => agent is null || string.IsNullOrWhiteSpace(agent.AgentId)
                    || string.IsNullOrWhiteSpace(agent.Path) || !Path.IsPathFullyQualified(agent.Path))
                || plan.Subject.Owners.Any(owner => owner is null))
                throw new IOException("Replacement plan schema, fingerprint or address binding is invalid.");
            ValidateEvidence(plan.Original); ValidateEvidence(plan.Candidate);
            if (plan.LocalDisposition is { } local)
            {
                if (local.SourceCandidate is null) throw new IOException("Merged plan lacks immutable source candidate evidence.");
                ValidateEvidence(local.SourceCandidate);
                var sourcePlan = Read(operation, "plan.json", ReplacementJsonContext.Default.ReplacementPlan, token);
                if (sourcePlan.SchemaVersion != 2 || sourcePlan.LocalDisposition is not null
                    || PlanHash(sourcePlan) != local.SourcePlanFingerprint || sourcePlan.ReviewFingerprint != local.SourcePlanFingerprint
                    || PlanHash(plan with
                    {
                        SchemaVersion = 2,
                        Candidate = local.SourceCandidate,
                        ChangedPaths = sourcePlan.ChangedPaths,
                        LocalDisposition = null,
                        SourceReview = sourcePlan.SourceReview,
                        ReviewFingerprint = ""
                    }) != local.SourcePlanFingerprint
                    || !QuarantineStore.ValidHash(local.HeaderFingerprint) || !QuarantineStore.ValidHash(local.SelectionFingerprint))
                    throw new IOException("Merged plan is not bound to its immutable original revision plan.");
            }
            subjectObserved?.Invoke(PlanImpact(plan));
            ValidateRevision(plan, operation.CanonicalPath, token);
            // Only retirement can read a marker-bearing operation whose approved
            // archives may already be gone. It verifies all surviving native
            // evidence separately; this grants no apply/recovery authority.
            if (!retiring)
            {
                ValidatePinPolicy(plan, operation, token);
                ValidateLocalImport(plan, operation, token);
                ValidateSourceReview(plan, operation, token);
            }
            var stepCount = 0;
            ReplacementStep? latestOwnerStep = null;
            var ownerWriteIds = new HashSet<string>(StringComparer.Ordinal);
            var incompleteJournal = false;
            var entries = operation.EnumerateEntries(token).Take(MaxSteps + 13 + 2 * MaxMergeAttempts + MaxSourceReviewEntries).ToArray();
            if (entries.Length > MaxSteps + 11 + 2 * MaxMergeAttempts + MaxSourceReviewEntries) throw new IOException("Replacement operation entry budget exceeded.");
            var legacyReviewArchives = ValidateLegacyReviewAttemptArchives(plan, operation, operationNames, token);
            if (entries.Any(entry => entry.Error is not null && !(plan.SourceReview is not null && IsSourceReviewEntry(entry.Name) || legacyReviewArchives.Contains(entry.Name)) && !IsMergeSlot(entry.Name) && entry.Name is not ("candidate" or "original" or "displaced" or "undo-recovery" or "recorded-base" or "owner-journal" or "local-review" or "installed-capture" or "local-source-capture" or "gh-input-capture" or "gh-output-capture")))
                throw new IOException("Unsafe replacement journal entry.");
            var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            while (names.Contains($"step-{stepCount:D4}.json"))
            {
                if (stepCount >= MaxSteps) throw new IOException("Replacement journal step limit exceeded.");
                try
                {
                    var step = Read(operation, $"step-{stepCount:D4}.json", ReplacementJsonContext.Default.ReplacementStep, token);
                    if (step.Sequence != stepCount || step.PlanFingerprint != plan.ReviewFingerprint
                        || step.Status is not ("retain-intent" or "retain-complete" or "install-intent" or "install-complete"
                            or "displace-intent" or "displace-complete" or "restore-intent" or "restore-complete"
                            or "owner-forward-intent" or "owner-forward-complete" or "owner-undo-intent" or "owner-undo-complete"
                            or "owner-repair-intent" or "owner-repair-complete")
                        || !ValidOwnerStep(step, plan))
                        throw new IOException("Replacement journal sequence or identity is invalid.");
                    if (step.OwnerWriteId is not null) { latestOwnerStep = step; ownerWriteIds.Add(step.OwnerWriteId); }
                }
                catch (Exception ex) when (ex is JsonException or JournalDataException or EndOfStreamException)
                {
                    // A crash/disk-full can leave a create-new file partially
                    // written. Keep it untouched. Explicit live-slot review is
                    // the authority for recovery; subsequent steps append.
                    incompleteJournal = true;
                    // An incomplete step grants no new write authority. Keep
                    // the last durable owner marker; raw recovery additionally
                    // refuses any unbound later write/completion evidence.
                    logger.Warn("replacement", $"Replacement {id}: preserving incomplete step {stepCount}: {ex.Message}");
                }
                stepCount++;
            }
            if (!retiring && names.Contains("installed-capture") != (plan.PinPolicy is not null || plan.LocalImport is not null)) throw new IOException("Installed capture presence disagrees with the saved plan.");
            if (!retiring && new[] { "local-source-capture", "gh-input-capture", "gh-output-capture" }.Any(name => names.Contains(name) != (plan.LocalImport is not null)))
                throw new IOException("Local source witness presence disagrees with the saved plan.");
            if (!retiring && names.Contains("recorded-base") != (plan.Revision is not null)) throw new IOException("Recorded base presence disagrees with the saved plan.");
            if (names.Contains("owner-journal") && EffectiveOwner(plan) is null) throw new IOException("Unexpected owner journal for this plan.");
            if (plan.LocalDisposition is not null && !names.Contains("local-review")) throw new IOException("Merged plan lacks its immutable local review.");
            if ((names.Any(IsMergeSlot) || names.Any(IsMergePlan)) && !names.Contains("local-review")) throw new IOException("Unexpected merged stage without local review.");
            if (names.Any(name => name is not ("writer.lock" or "plan.json" or "candidate" or "original" or "displaced" or "undo-recovery" or "recorded-base" or "owner-journal" or "local-review" or "installed-capture" or "local-source-capture" or "gh-input-capture" or "gh-output-capture")
                && !(retiring && name == "retiring.json")
                && !(plan.SourceReview is not null && IsSourceReviewEntry(name) || legacyReviewArchives.Contains(name))
                && !IsMergeSlot(name) && !IsMergePlan(name)
                && !Enumerable.Range(0, stepCount).Any(index => name == $"step-{index:D4}.json")))
                throw new IOException("Unknown replacement operation entry or journal gap.");
            var session = new Session(state, writer, operation, operationWriter, plan, stepCount, incompleteJournal) { LatestOwnerStep = latestOwnerStep };
            session.OwnerWriteIds.UnionWith(ownerWriteIds);
            using var ownerJournal = OpenOwnerJournal(session, inventory, token, create: false);
            if (names.Contains("undo-recovery")) session.OpenRecoveryJournal(Exclusions(inventory), token, false);
            return session;
        }
        catch { operationWriter?.Dispose(); operation?.Dispose(); writer?.Dispose(); state?.Dispose(); throw; }
    }

    private static void ValidateEvidence(ReplacementEvidence evidence)
    {
        if (!QuarantineStore.ValidHash(evidence.Payload) || !QuarantineStore.ValidHash(evidence.Objects)
            || !evidence.Identity.IsDirectory || evidence.Identity.IsReparsePoint
            || evidence.Mode is < 0 or > 4095 || evidence.Entries.IsDefaultOrEmpty || evidence.Children.IsDefaultOrEmpty
            || evidence.Entries.Length > PayloadSnapshot.MaxEntries || evidence.Children.Length != evidence.Entries.Length
            || evidence.Entries.Any(entry => entry is null || string.IsNullOrEmpty(entry.Path) || entry.Bytes < 0
                || entry.Kind is not ("file" or "link" or "directory") || entry.ContentHash is null || entry.ComparisonHash is null)
            || evidence.Children.Any(child => child is null)
            || evidence.Entries.Sum(entry => entry.Bytes) > PayloadSnapshot.MaxBytes
            || PayloadSnapshot.Digest(evidence.Entries, false) != evidence.Payload)
            throw new IOException("Saved replacement payload evidence is invalid or exceeds its budget.");
    }

    private sealed class JournalDataException(string message) : IOException(message);

    private static bool ValidOwnerStep(ReplacementStep step, ReplacementPlan plan) => step.Status.StartsWith("owner-", StringComparison.Ordinal)
        ? EffectiveOwner(plan) is not null && QuarantineStore.ValidId(step.OwnerWriteId!)
        : step.OwnerWriteId is null;

    private sealed class Session(NativeStateDirectory state, FileStream writer, NativeStateDirectory operation,
        FileStream operationWriter, ReplacementPlan plan, int stepCount, bool incompleteJournal) : IDisposable
    {
        internal NativeStateDirectory Operation { get; } = operation;
        internal ReplacementPlan Plan { get; } = plan;
        internal bool IncompleteJournal { get; } = incompleteJournal;
        internal int StepCount { get; set; } = stepCount;
        internal ReplacementStep? LatestOwnerStep { get; set; }
        internal HashSet<string> OwnerWriteIds { get; } = new(StringComparer.Ordinal);
        internal NativeStateDirectory? RecoveryJournal { get; private set; }
        internal NativeStateDirectory Journal => RecoveryJournal ?? Operation;
        private int recoverySteps;
        private FileStream? recoveryWriter;
        internal void UseRecoveryJournal(string[] exclusions, CancellationToken token)
        {
            if (RecoveryJournal is null) OpenRecoveryJournal(exclusions, token, true);
            recoveryWriter = RecoveryJournal!.OpenFile("writer.lock", true, token);
            CheckWriter(recoveryWriter);
            StepCount = recoverySteps;
        }
        internal void OpenRecoveryJournal(string[] exclusions, CancellationToken token, bool create)
        {
            var recovery = NativeStateDirectory.OpenOrCreate(Path.Combine(Operation.CanonicalPath, "undo-recovery"), exclusions, token, allowCreate: create);
            try
            {
                var entries = recovery.EnumerateEntries(token).Take(MaxSteps + 2).ToArray();
                if (entries.Length > MaxSteps + 1 || entries.Any(entry => entry.Error is not null)) throw new IOException("Unsafe undo recovery journal.");
                var names = entries.Where(entry => entry.Name != "writer.lock").Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
                if (entries.Any(entry => entry.Name == "writer.lock" && entry.Bytes != 0)) throw new IOException("Recovery writer lock must be empty.");
                var count = 0;
                while (names.Contains($"step-{count:D4}.json"))
                {
                    try
                    {
                        var step = Read(recovery, $"step-{count:D4}.json", ReplacementJsonContext.Default.ReplacementStep, token);
                        if (step.Sequence != count || step.PlanFingerprint != Plan.ReviewFingerprint
                            || step.Status is not ("displace-intent" or "displace-complete" or "restore-intent" or "restore-complete" or "owner-undo-intent" or "owner-undo-complete" or "owner-repair-intent" or "owner-repair-complete")
                            || !ValidOwnerStep(step, Plan))
                            throw new IOException("Invalid undo recovery journal identity.");
                        if (step.OwnerWriteId is not null) { LatestOwnerStep = step; OwnerWriteIds.Add(step.OwnerWriteId); }
                    }
                    catch (Exception ex) when (ex is JsonException or JournalDataException or EndOfStreamException) { }
                    count++;
                }
                if (count != names.Count) throw new IOException("Undo recovery journal has unknown entries or gaps.");
                recoverySteps = count;
                RecoveryJournal = recovery;
            }
            catch { recovery.Dispose(); throw; }
        }
        internal FileStream OperationWriter => operationWriter;
        internal NativeStateDirectory State => state;
        internal void CloseOperation() { operationWriter.Dispose(); Operation.Dispose(); }
        public void Dispose() { recoveryWriter?.Dispose(); RecoveryJournal?.Dispose(); operationWriter.Dispose(); Operation.Dispose(); writer.Dispose(); state.Dispose(); }
    }
}
