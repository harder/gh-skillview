using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Preferences;
using SkillView.Threading;

namespace SkillView.Ui;

internal enum ApprovalAction { Preview, Capture, Inspect, SetPolicy, Plan }
internal sealed record ApprovalRequest(ApprovalAction Action, string? Id, string? Fingerprint,
    ApprovalResponse? Review, bool AllowHidden, bool Consent);
internal sealed record ApprovalResponse(string Payload, DesiredPolicyObservation Policy,
    ApprovedPayloadReference? Reference = null, ApprovedPayloadHeader? Header = null, ReplacementResult? Plan = null);

internal sealed partial class SkillViewWorkflowCoordinator
{
    internal void ShowApprovedPayload(InstalledSkill selected, CancellationToken lifetimeToken)
    {
        if (_getApp() is not { } app) return;
        var prepared = ApprovedPayloadScreen.Show(app, _services.Logger, selected,
            (request, token) => RunApprovalAsync(selected, request, token), lifetimeToken);
        if (prepared is not null && !lifetimeToken.IsCancellationRequested)
            LocalReimportScreen.Show(app, _services.Logger, selected,
                (request, token) => RunApprovedLifecycleAsync(selected, request, token), lifetimeToken,
                approvedRestore: true, initialPlan: prepared);
        if (!lifetimeToken.IsCancellationRequested) _refreshActiveTab();
    }

    internal async Task<ApprovalResponse> RunApprovalAsync(InstalledSkill selected, ApprovalRequest request, CancellationToken token)
    {
        var services = _services.CaptureContext();
        var inventory = await services.InventoryService.CaptureAsync(services.GhLocator.FindOnPath(),
            new(_options.ScanRoots, request.AllowHidden, WorkspaceDirectories: _options.WorkspaceDirectories, Profiles: _options.Profiles), token).ConfigureAwait(false);
        var matches = inventory.Skills.Where(skill => skill.InstallationId == selected.InstallationId && PathIdentity.Equals(skill.ResolvedPath, selected.ResolvedPath)).Take(2).ToArray();
        if (matches.Length != 1) throw new IOException("The selected installation changed; refresh Installed. No sibling was selected.");
        var target = matches[0];
        var exclusions = inventory.ProtectedRoots.Select(root => root.Path).Concat(inventory.Skills.Select(skill => skill.ResolvedPath)).ToArray();
        var store = ApprovedPayloadStore.For(services.Runner, services.Logger);
        var policies = DesiredRevisionPolicyStore.For(services.Runner);
        var policy = policies.Observe(target, exclusions, token);
        if (request.Action == ApprovalAction.Preview)
        {
            var evidence = await Task.Run(() => services.SavedUpdatePlans.PreviewApprovedPayload(target, inventory, token), token).ConfigureAwait(false);
            return new(evidence.Payload, policy);
        }
        ApprovedPayloadReference reference;
        if (request.Action == ApprovalAction.Capture)
        {
            if (!request.Consent || request.Review is not { Reference: null } review) throw new IOException("Preview actual installed bytes and separately consent to private capture.");
            reference = await Task.Run(() => store.CaptureCustom(target, inventory, review.Payload, token), token).ConfigureAwait(false);
        }
        else
        {
            if (!QuarantineStore.ValidId(request.Id!) || !QuarantineStore.ValidHash(request.Fingerprint!)) throw new IOException("Supply the exact retained approval ID and fingerprint.");
            reference = new(store.Root, request.Id!, request.Fingerprint!);
        }
        ApprovedPayloadHeader header;
        using (var held = store.Open(reference, exclusions, token)) { held.RequireTarget(target, services.Runner, token); header = held.Header; }
        if (request.Action == ApprovalAction.SetPolicy)
        {
            if (!request.Consent || request.Review?.Reference != reference) throw new IOException("Inspect this completed approval and separately consent to desired intent.");
            if (!request.Review.Policy.Known || request.Review.Policy.Fingerprint is not { } expectedPolicy) throw new IOException("Desired policy is unknown; refresh before approval.");
            policy = await Task.Run(() => store.SetPolicy(reference, target, inventory, expectedPolicy, approved: true, token), token).ConfigureAwait(false);
        }
        ReplacementResult? plan = null;
        if (request.Action == ApprovalAction.Plan)
        {
            if (request.Review?.Reference != reference) throw new IOException("Inspect this exact approval before staging restore.");
            plan = await Task.Run(() => services.SavedUpdatePlans.PlanApproved(reference, target, inventory, token), token).ConfigureAwait(false);
        }
        return new(header.Captured.Payload, policy, reference, header, plan);
    }

    internal async Task<LocalReimportResponse> RunApprovedLifecycleAsync(InstalledSkill selected, LocalReimportRequest request, CancellationToken token)
    {
        var services = _services.CaptureContext();
        Task<InventorySnapshot> Capture(CancellationToken ct) => services.InventoryService.CaptureAsync(services.GhLocator.FindOnPath(),
            new(_options.ScanRoots, request.AllowHidden, WorkspaceDirectories: _options.WorkspaceDirectories, Profiles: _options.Profiles), ct);
        var inventory = await Capture(token).ConfigureAwait(false);
        const string purpose = ReplacementPlanStore.ApprovedRestorePurpose;
        if (request.Action == LocalReimportAction.Prepare) throw new IOException("Capture/inspect an approval before preparing an approved restore.");
        if (request.Action == LocalReimportAction.Inspect)
        {
            var inspected = await Task.Run(() => services.SavedUpdatePlans.Inspect(request.Id!, inventory, token, purpose), token).ConfigureAwait(false);
            RequireSelected(inspected); return new(Plan: inspected);
        }
        var review = request.Review ?? throw new IOException("Inspect the exact saved approved restore first.");
        RequireSelected(review);
        if (request.Id != review.Id) throw new IOException("Saved ID changed after review.");
        if (request.Action == LocalReimportAction.Apply)
        {
            try { return new(Preparation: await Task.Run(() => services.SavedUpdates.ApplyAsync(review, ReplacementPlanStore.ApprovedRestoreDisposition, inventory, Capture, token), token).ConfigureAwait(false)); }
            finally { _services.ListAdapter.Invalidate(); }
        }
        if (request.Action is not (LocalReimportAction.Recover or LocalReimportAction.Undo)) throw new ArgumentException("Unsupported approved lifecycle action.");
        if (request.Action == LocalReimportAction.Recover && review.AcknowledgeSourceReview && review.SourceReview is { } source)
            await Task.Run(() => services.SavedUpdatePlans.AcknowledgeSourceReview(review.Id, review.PlanFingerprint, source.Fingerprint, inventory, token, purpose), token).ConfigureAwait(false);
        ReplacementResult result; string? warning = null;
        try { result = await Task.Run(() => services.SavedUpdatePlans.Recover(review.Id, review.ReviewFingerprint, inventory, request.Action == LocalReimportAction.Undo, token, purpose), token).ConfigureAwait(false); }
        finally
        {
            _services.ListAdapter.Invalidate(); services.ListAdapter.Invalidate();
            using var deadline = new CancellationSource(TimeSpan.FromSeconds(10), CancellationCallbackReporter.For(services.Logger, "approved restore reconciliation"));
            try { var fresh = await Capture(deadline.Token).ConfigureAwait(false); if (fresh.IsPartial || !fresh.ConfiguredRootsComplete) throw new IOException("Post-attempt inventory is partial; inspect retained native evidence."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { warning = ex.Message; services.Logger.Warn("approval.reconcile", ex.ToString()); }
        }
        return new(Plan: result, ReconciliationWarning: warning);

        void RequireSelected(ReplacementResult plan)
        {
            if (plan.Approval is null || plan.Subject.InstallationId != selected.InstallationId || !PathIdentity.Equals(plan.OriginalPath, selected.ResolvedPath))
                throw new IOException("Approved restore belongs to another purpose or selected installation.");
        }
    }
}
