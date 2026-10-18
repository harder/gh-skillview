using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;

internal static class SourceSwitchWorkflowProof
{
    internal static async Task<ReplacementResult> ApplyAsync(TuiServices services, string root, string gh,
        string id, string expected, InventorySnapshot inventory, bool switching, CancellationToken token)
    {
        var purpose = switching ? ReplacementPlanStore.LocalSourceSwitchPurpose : ReplacementPlanStore.LocalReimportPurpose;
        var disposition = switching ? ReplacementPlanStore.LocalSourceSwitchDisposition : ReplacementPlanStore.LocalReimportDisposition;
        var review = services.SavedUpdatePlans.Inspect(id, inventory, token, purpose);
        if (review.Status != "ready" || review.SourceReview is null || review.SourceReviewAcknowledged
            || review.ReviewFingerprint != expected)
            throw new IOException("Coordinator apply proof needs an exact ready local plan without a source-review receipt.");
        var before = PayloadSnapshot.Capture(review.OriginalPath, token);
        Task<InventorySnapshot> Reconcile(CancellationToken cancellation) => services.InventoryService.CaptureAsync(gh,
            new(ScanRoots: [Path.Combine(root, "custom")], AllowHiddenDirs: true), cancellation);
        foreach (var (candidate, approval) in new[]
        {
            (review with { AcknowledgeSourceReview = true }, ""),
            (review, disposition),
            (review with { AcknowledgeSourceReview = true, SourceReview = review.SourceReview with { Fingerprint = new('0', 64) } }, disposition)
        })
        {
            var refused = await services.SavedUpdates.ApplyAsync(candidate, approval, inventory, Reconcile, token).ConfigureAwait(false);
            var current = services.SavedUpdatePlans.Inspect(id, inventory, token, purpose);
            if (refused.Succeeded || refused.SavedPlan?.MutationObserved == true || current.Status != "ready" || current.SourceReviewAcknowledged
                || Directory.Exists(review.RetainedPath)
                || PayloadSnapshot.Capture(review.OriginalPath, token).ObjectStateFingerprint != before.ObjectStateFingerprint)
                throw new IOException("Missing retention, missing source approval or wrong source fingerprint did not refuse without mutation.");
        }
        var applied = await services.SavedUpdates.ApplyAsync(review with { AcknowledgeSourceReview = true }, disposition,
            inventory, Reconcile, token).ConfigureAwait(false);
        var result = applied.SavedPlan ?? throw new IOException("Coordinator apply returned no local plan evidence.");
        if (!applied.Succeeded || !result.Succeeded || result.Status != "installed" || !result.SourceReviewAcknowledged)
            throw new IOException("Coordinator local apply acknowledgement failed: " + applied.ErrorMessage + " " + result.Message);
        return result;
    }

    // Program admits the marked private native HOME/CWD/state before this call.
    internal static async Task<ReplacementResult> RunAsync(TuiServices services, string root, string gh,
        string id, string expected, InventorySnapshot inventory, CancellationToken token)
    {
        var review = services.SavedUpdatePlans.Inspect(id, inventory, token, ReplacementPlanStore.LocalSourceSwitchPurpose);
        if (review.Status != "ready" || review.SourceReview is null || review.SourceReviewAcknowledged
            || review.ReviewFingerprint != expected)
            throw new IOException("Workflow proof needs an exact real ready source-switch plan without a source-review receipt.");
        var selected = inventory.Skills.Single(skill => skill.InstallationId == review.Subject.InstallationId
            && SkillView.Inventory.PathIdentity.Equals(skill.ResolvedPath, review.OriginalPath));
        var coordinator = new SkillViewWorkflowCoordinator(services,
            ArgParser.Parse("proof", ["--scan-root", Path.Combine(root, "custom")]),
            () => null, () => gh, () => null,
            _ => throw new IOException("Unexpected environment probe in workflow proof."),
            _ => { }, () => { }, _ => { }, (_, _) => { },
            _ => throw new IOException("Unexpected UI invocation in workflow proof."),
            (_, _) => throw new IOException("Unexpected async UI invocation in workflow proof."),
            (_, _) => throw new IOException("Unexpected background work in workflow proof."),
            () => { }, () => { });
        var response = await coordinator.RunLocalSourceSwitchAsync(selected,
            new(LocalReimportAction.Recover, id, review with { AcknowledgeSourceReview = true }, true), token).ConfigureAwait(false);
        var result = response.Plan ?? throw new IOException("Workflow did not return its source-switch lifecycle evidence.");
        if (!result.Succeeded || result.Status != "installed" || !result.SourceReviewAcknowledged)
            throw new IOException("Workflow source-review acknowledgement/recovery failed: " + result.Message);
        return result;
    }
}
