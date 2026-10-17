using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Preferences;

namespace SkillView.Maintenance;

internal sealed record ApprovedCaptureSubject(InstalledSkill Target, ApprovedSelectedResult? Selected, ApprovedOwnerCapture? Owner);

internal sealed partial class ReplacementPlanStore
{
    // The approval store already owns policy -> approval root. This method
    // obtains only the existing replacement/registry/payload authorities and
    // captures through held native handles; it never resolves source metadata.
    internal ApprovedCaptureSubject CaptureApprovedPayload(InstalledSkill target, InventorySnapshot inventory,
        NativeStateDirectory capture, string expectedPayload, string? selectedId, string? selectedPlan,
        CancellationToken token)
    {
        var canonical = Canonical(target.ResolvedPath);
        var containing = inventory.ScannedRoots.Select(scan => Canonical(scan.Path))
            .Where(path => !PathIdentity.Equals(path, canonical) && PathIdentity.IsInside(canonical, path))
            .OrderByDescending(path => path.Length).FirstOrDefault()
            ?? throw new IOException("Approval capture requires the actual containing scan root.");
        target = target with { ResolvedPath = canonical, ScanRoot = containing };
        if (selectedId is not null)
            return CaptureCompletedSelected(target, inventory, capture, expectedPayload, selectedId, selectedPlan!, token);
        var ownerPlan = PrepareOwner(target, null, inventory, token, initializeState: false, approvedMetadata: target.FrontMatter);
        using var owner = OpenOwner(ownerPlan, token, requireBefore: true, exactEntryBytes: true);
        inventory = Admit(target, inventory, token, owner: owner, approvedCapture: true);
        using var held = NativeDirectoryMoveLease.Open(containing, canonical, token);
        var before = held.Capture(token);
        if (before.Fingerprint != expectedPayload) throw new IOException("Installed bytes differ from the independently reviewed approval capture.");
        var context = Context(target, inventory, token, owner: owner);
        var archive = CapturedPayloadArchive.Capture(capture, "payload", held, token);
        VerifyPayload(ReplacementEvidence.From(before), archive.Snapshot);
        if (archive.Snapshot.FrontMatter is not { } metadata || !metadata.Diagnostics.IsEmpty
            || !SameParsedMetadata(target.FrontMatter, metadata))
            throw new IOException("Installed authored/source metadata changed before immutable approval capture.");
        held.Revalidate(token); VerifyPayload(ReplacementEvidence.From(before), held.Capture(token));
        if (Context(target, inventory, token, owner: owner) != context)
            throw new IOException("Approval source/owner/consumer context changed during capture.");
        return new(target, null, CaptureApprovalOwner(owner, target, token));
    }

    private ApprovedCaptureSubject CaptureCompletedSelected(InstalledSkill target, InventorySnapshot inventory,
        NativeStateDirectory capture, string expectedPayload, string selectedId, string selectedPlan, CancellationToken token)
    {
        using var session = Open(selectedId, inventory, token);
        var plan = session.Plan; RequirePurpose(plan, SelectedRevisionPurpose);
        if (plan.ReviewFingerprint != selectedPlan || plan.Selection is null || plan.Revision is null
            || target.InstallationId != plan.Subject.InstallationId || !PathIdentity.Equals(target.ResolvedPath, plan.OriginalPath)
            || target.Scope != plan.Subject.Scope || !target.Agents.OrderBy(agent => agent.Path, StringComparer.Ordinal).ThenBy(agent => agent.AgentId, StringComparer.Ordinal)
                .SequenceEqual(plan.Subject.Agents.OrderBy(agent => agent.Path, StringComparer.Ordinal).ThenBy(agent => agent.AgentId, StringComparer.Ordinal)))
            throw new IOException("Remote approval requires the exact completed selected plan at this installation.");
        using var owner = OpenOwner(EffectiveOwner(plan), token, recoverySession: session, inventory: inventory);
        var observation = Observe(plan, session.Operation, inventory, token, owner);
        if (session.StepCount == 0 || session.IncompleteJournal || session.RecoveryJournal is not null
            || observation.Topology != "installed" || observation.Original?.Payload != expectedPayload
            || owner is not null && !OwnerSatisfied(observation, owner, false))
            throw new IOException("Selected preparation/tracking alone is not a completed installed-result witness.");
        var last = Read(session.Operation, $"step-{session.StepCount - 1:D4}.json", ReplacementJsonContext.Default.ReplacementStep, token);
        if (last.Status is not ("install-complete" or "owner-forward-complete")
            || DesiredRevisionPolicyStore.Bind(target) != DesiredRevisionPolicyStore.Bind(Subject(plan)))
            throw new IOException("Selected approval requires durable completion and the same current source/owner namespace.");
        // Observe already admitted the original saved subject with owner
        // normalization. Re-admitting new metadata against that normalized old
        // owner would incorrectly compare the installed C tree with old B.
        using var held = NativeDirectoryMoveLease.Open(plan.RootPath, plan.OriginalPath, token);
        VerifyPayload(plan.Candidate, held.Capture(token));
        var archive = CapturedPayloadArchive.Capture(capture, "payload", held, token);
        VerifyPayload(plan.Candidate, archive.Snapshot);
        if (archive.Snapshot.FrontMatter is not { } metadata || !SameParsedMetadata(target.FrontMatter, metadata)
            || !SameParsedMetadata(plan.Revision.DesiredMetadata, metadata))
            throw new IOException("Installed selected metadata differs from actual captured result.");
        foreach (var name in SelectedArchives)
        {
            using var source = session.Operation.OpenExistingDirectory(name, token);
            var expected = name == "selected-source-capture" ? plan.Selection.Provider.SourceArchiveFingerprint : plan.Selection.Provider.OutputArchiveFingerprint;
            CapturedPayloadArchive.CopyInto(source, capture, name, expected, token);
        }
        held.Revalidate(token); VerifyPayload(plan.Candidate, held.Capture(token));
        return new(target, new(Path.GetDirectoryName(session.Operation.CanonicalPath)!, plan.Id, plan.ReviewFingerprint, plan.Selection.Provider, archive.Snapshot.Fingerprint),
            CaptureApprovalOwner(owner, target, token));
    }

    private static ApprovedOwnerCapture? CaptureApprovalOwner(OwnerContext? owner, InstalledSkill target, CancellationToken token)
    {
        if (owner is null) return null;
        var snapshot = owner.Held.Read(token);
        var entry = GhRegistryEditor.CaptureEntry(snapshot.Bytes, owner.Transition.Selector, token);
        if (target.AllOwnershipClaims.Any(claim => claim.EntryFingerprint != entry.Fingerprint || !owner.Matches(claim.LockFilePath)))
            throw new IOException("Approval owner observation differs from the exact installed captured claims.");
        return new(owner.Held.CanonicalPath, owner.Transition.Selector, snapshot.Identity, owner.Held.ParentIdentity, entry.Bytes, entry.Fingerprint);
    }
}
