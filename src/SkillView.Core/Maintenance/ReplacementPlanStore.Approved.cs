using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Preferences;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    internal const int ApprovedRestoreSchema = 8;
    internal const string ApprovedRestorePurpose = "restore-approved";
    internal const string ApprovedRestoreDisposition = "restore-approved-retain-original";
    private static readonly string[] ApprovedArchiveNames = ["approved-payload", "approved-selected-source", "approved-selected-provider"];

    internal ReplacementResult PlanApproved(ApprovedPayloadReference reference, InstalledSkill target,
        InventorySnapshot inventory, CancellationToken token)
    {
        var process = (runner ?? throw new IOException("Approved restore needs an explicit captured process context.")).CaptureContext();
        return new ReplacementPlanStore(Root, logger, checkpoint, process, beforeStateOpenForTests, subjectObserved)
            .PlanApprovedCore(reference, target, inventory, token);
    }

    private ReplacementResult PlanApprovedCore(ApprovedPayloadReference reference, InstalledSkill target,
        InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ManagedSkillDiscovery.RefuseTarget(target, token, runner);
        var canonical = Canonical(target.ResolvedPath);
        var containing = inventory.ScannedRoots.Select(scan => Canonical(scan.Path))
            .Where(path => !PathIdentity.Equals(path, canonical) && PathIdentity.IsInside(canonical, path))
            .OrderByDescending(path => path.Length).FirstOrDefault()
            ?? throw new IOException("Approved restore requires an existing exact installation under its actual scan root.");
        target = target with { ResolvedPath = canonical, ScanRoot = containing };
        var approvals = ApprovedPayloadStore.For(runner!, logger);
        ApprovedPayloadHeader reviewed;
        using (var initial = approvals.Open(reference, Exclusions(inventory), token))
        { initial.RequireTarget(target, runner!, token); reviewed = initial.Header; }
        var desiredMetadata = reviewed.Subject.Metadata;
        if (target.Pinned && !desiredMetadata.Pinned)
            throw new IOException("Approved restore cannot implicitly clear an actual installed pin; review standalone unpin first.");
        using var policy = AcquireDesiredPolicy(target, inventory, ApprovedRestorePurpose, token,
            approval: reference, approvedMetadata: desiredMetadata);
        using var approval = approvals.Open(reference, Exclusions(inventory), token);
        approval.RequireTarget(target, runner!, token);
        if (approval.Header.Fingerprint != reviewed.Fingerprint) throw new IOException("Approval changed during policy admission.");
        CheckPlacement(inventory, token);
        using var state = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token);
        using var writer = state.OpenFile("writer.lock", true, token); CheckWriter(writer);
        var entries = state.EnumerateEntries(token).Take(MaxPlans + 2).ToArray();
        if (entries.Length > MaxPlans || entries.Any(entry => entry.Name == "writer.lock" ? entry.Error is not null : !QuarantineStore.ValidId(entry.Name)))
            throw new IOException("Replacement plan quota or namespace refuses approved restore; its approval archive remains independently retained.");
        var id = Guid.NewGuid().ToString("N");
        using var operation = state.CreateStagingDirectory(id, token);
        using var operationWriter = operation.OpenFile("writer.lock", true, token); CheckWriter(operationWriter);
        var transition = PrepareOwner(target, null, inventory, token, initializeState: false, approvedMetadata: desiredMetadata);
        using var owner = OpenOwner(transition, token, requireBefore: true, exactEntryBytes: true);
        inventory = Admit(target, inventory, token, owner: owner, approvedCapture: true);
        var context = Context(target, inventory, token, owner: owner);
        using var original = NativeDirectoryMoveLease.Open(containing, canonical, token);
        var before = original.Capture(token);
        try
        {
            var installed = CapturedPayloadArchive.Capture(operation, "approved-original", original, token);
            VerifyPayload(ReplacementEvidence.From(before), installed.Snapshot);
            if (installed.Snapshot.FrontMatter is not { } live || !SameParsedMetadata(target.FrontMatter, live))
                throw new IOException("Current installed authored/source metadata changed before approved restore planning.");
            using (var installedState = operation.OpenExistingDirectory("approved-original", token))
            using (PayloadMetadataReview.PreserveInstalled(installed, installed.OpenReader(installedState), token)) { }
            approval.CopyInto(operation, token);
            using var archivedState = operation.OpenExistingDirectory("approved-payload", token);
            var archived = CapturedPayloadArchive.Load(archivedState, reviewed.ArchiveFingerprint, token).Archive;
            var reader = archived.OpenReader(archivedState);
            using var metadata = PayloadMetadataReview.PreserveInstalled(archived, reader, token);
            var staged = InstalledPayloadMaterializer.Stage(operation, archived, reader, metadata,
                GhLocalImportStager.ReadSkill(reader, token), token);
            using var candidate = NativeDirectoryMoveLease.Open(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "candidate"), token);
            var desired = candidate.FlushPayload(token);
            VerifyPayload(ReplacementEvidence.From(LegacyProjection(staged, token)), desired);
            if (desired.Fingerprint != reviewed.Captured.Payload || desired.RootUnixMode != reviewed.Captured.Mode)
                throw new IOException("Materialized approved bytes/modes differ from the immutable archive.");
            original.Revalidate(token); VerifyPayload(ReplacementEvidence.From(before), original.Capture(token));
            if (Context(target, inventory, token, owner: owner) != context) throw new IOException("Original owner/consumer context changed during approved staging.");
            var subject = new ReplacementSubject(target.InstallationId, target.Name, target.Scope, target.Agents,
                target.FrontMatter, target.AllOwnershipClaims.ToImmutableArray());
            var proof = new ReplacementApproval(reference, reviewed, runner!.CaptureContextFingerprint(), transition);
            var plan = new ReplacementPlan(ApprovedRestoreSchema, id, ApprovedRestorePurpose, DateTimeOffset.UtcNow,
                canonical, original.RootIdentity.CanonicalPath, original.RootIdentity, original.ParentIdentity, subject,
                context, ReplacementEvidence.From(before), ReplacementEvidence.From(desired), PayloadSnapshot.Differences(before, desired, false)
                    .AddRange(before.RootUnixMode == desired.RootUnixMode ? [] : new[] { "/" }), "", Approval: proof);
            ValidateApproval(plan, operation, token);
            plan = FreezeSourceReview(plan, operation, original, candidate, token);
            plan = plan with { ReviewFingerprint = PlanHash(plan) };
            Write(operation, "plan.json", plan, ReplacementJsonContext.Default.ReplacementPlan, token);
            WriteDesiredPolicyReceipt(operation, plan, policy, token);
            return Result(plan, "planned", true, false, false, plan.ReviewFingerprint,
                "Exact approved bytes and supported metadata staged without source access. Review the current original, captured provenance, consumers and pins; explicit original retention is required. Session state is unknown.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new IOException("Approved restore refused; immutable approval and partial replacement evidence remain retained for " + id + ". " + ex.Message, ex); }
    }

    private ApprovedPayloadStore.ReadLease? OpenApprovedAdmission(ReplacementPlan plan, InventorySnapshot inventory, CancellationToken token)
    {
        if (plan.Approval is not { } approved) return null;
        var process = (runner ?? throw new IOException("Approved admission needs its process context.")).CaptureContext();
        var held = ApprovedPayloadStore.For(process, logger).Open(approved.Reference, Exclusions(inventory), token);
        try
        {
            held.RequireTarget(Subject(plan), process, token);
            if (approved.ContextFingerprint != process.CaptureContextFingerprint()) throw new IOException("Approved restore process context changed after review.");
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    private static void ValidateApproval(ReplacementPlan plan, NativeStateDirectory operation, CancellationToken token)
    {
        if (plan.Approval is not { } approval) return;
        _ = EffectiveOwner(plan);
        var header = approval.Header;
        if (approval.Reference.Id != header.Id || approval.Reference.Fingerprint != header.Fingerprint
            || !Path.IsPathFullyQualified(approval.Reference.StoreAddress) || !QuarantineStore.ValidHash(approval.ContextFingerprint)
            || DesiredRevisionPolicyStore.Bind(Subject(plan)) != header.Binding
            || plan.Subject.Name != header.Subject.Name || plan.Subject.Metadata.Pinned && !header.Subject.Metadata.Pinned)
            throw new IOException("Approved plan reference, source binding, pin or context is inconsistent.");
        using var archive = operation.OpenExistingDirectory("approved-payload", token);
        var loaded = CapturedPayloadArchive.Load(archive, header.ArchiveFingerprint, token);
        ApprovedPayloadStore.Validate(header, loaded.Archive, loaded.Archive.OpenReader(archive), operation, token, savedOperation: true);
        using var original = operation.OpenExistingDirectory("approved-original", token);
        var captured = CapturedPayloadArchive.Load(original, null, token).Archive;
        VerifyPayload(plan.Original, captured.Snapshot);
        if (captured.Snapshot.FrontMatter is not { } prior || !SameParsedMetadata(plan.Subject.Metadata, prior)
            || plan.Candidate.Payload != header.Captured.Payload || plan.Candidate.Mode != header.Captured.Mode)
            throw new IOException("Approved plan original or candidate is not its retained captured subject.");
        if (approval.Owner is { } owner)
            ValidateOwnerTransition(plan.Subject.Metadata, plan.Subject.Owners, null, owner, token, approvedMetadata: header.Subject.Metadata);
        else if (!plan.Subject.Owners.IsEmpty) throw new IOException("Approved restore lacks a real owner transition contract.");
    }

    private static void VerifyApprovalPayload(ReplacementPlan plan, NativeStateDirectory operation, string topology,
        ReplacementSlot? original, ReplacementSlot? retained, ReplacementSlot? candidate, ReplacementSlot? displaced, CancellationToken token)
    {
        if (plan.Approval is not { } approval) return;
        using var archived = operation.OpenExistingDirectory("approved-payload", token);
        var payload = CapturedPayloadArchive.Load(archived, approval.Header.ArchiveFingerprint, token).Archive;
        using var metadata = PayloadMetadataReview.PreserveInstalled(payload, payload.OpenReader(archived), token);
        var selected = topology is "ready" or "original-retained" ? candidate! : topology == "installed" ? original! : displaced!;
        using (var held = NativeDirectoryMoveLease.Open(PathIdentity.IsInside(selected.Path, operation.CanonicalPath) ? operation.CanonicalPath : plan.RootPath, selected.Path, token))
            held.CaptureInto((item, _) => metadata.Verify(item, token), item => metadata.Verify(item, token), token);
        using var initial = operation.OpenExistingDirectory("approved-original", token);
        var source = CapturedPayloadArchive.Load(initial, null, token).Archive;
        using var priorMetadata = PayloadMetadataReview.PreserveInstalled(source, source.OpenReader(initial), token);
        var prior = topology is "ready" or "restored" ? original! : retained!;
        using var before = NativeDirectoryMoveLease.Open(PathIdentity.IsInside(prior.Path, operation.CanonicalPath) ? operation.CanonicalPath : plan.RootPath, prior.Path, token);
        before.CaptureInto((item, _) => priorMetadata.Verify(item, token), item => priorMetadata.Verify(item, token), token);
    }
}
