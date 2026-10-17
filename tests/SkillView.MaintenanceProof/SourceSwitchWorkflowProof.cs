using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;

internal static class SourceSwitchWorkflowProof
{
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
