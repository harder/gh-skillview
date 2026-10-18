using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal static class SelectedCleanupHandoff
{
    internal static async Task RunAsync(MaintenanceQueueEntry selected, ChangesSelectionGate.Lease request,
        Func<CancellationToken, Task<InventorySnapshot>> capture,
        Func<Action, CancellationToken, Task<bool>> dispatch,
        Action<InventorySnapshot, ImmutableArray<CleanupClassifier.Candidate>> show,
        Action refused)
    {
        if (!request.IsCurrent) return;
        var snapshot = await capture(request.Token).ConfigureAwait(false);
        if (!request.IsCurrent) return;
        var candidates = CleanupClassifier.ClassifyWithCancellation(snapshot, snapshot.ScannedRoots, null, request.Token)
            .Where(candidate => SkillViewWorkflowCoordinator.MatchesCleanupSelection(candidate, selected)).ToImmutableArray();
        // The caller retains the request lease until this awaited callback
        // returns. Recheck ownership in the callback, not just before queuing.
        await dispatch(() =>
        {
            if (!request.TryEnterDispatch()) return;
            if (candidates.Length != 1) refused();
            else show(snapshot, candidates);
        }, request.Token).ConfigureAwait(false);
    }
}
