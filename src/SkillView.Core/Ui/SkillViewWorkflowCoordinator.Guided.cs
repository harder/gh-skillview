using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal sealed partial class SkillViewWorkflowCoordinator
{
    internal async Task ReviewGuidedAsync(GuidedRepairSequence sequence, GuidedRepairItem item,
        ChangesSelectionGate.Lease request, Action<OperationSubject> reviewUpdate)
    {
        bool Current() => request.IsCurrent && ReferenceEquals(sequence.Active, item);
        if (!Current()) return;
        var report = await GetOrProbeReportAsync(request.Token).ConfigureAwait(false);
        var snapshot = await CaptureInventoryCoreAsync(report, request.Token, bypassGhCache: true).ConfigureAwait(false);
        if (!Current()) return;
        var contextMatches = sequence.Context == SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner);
        var targetMatches = item.CopyIntent is null || item.CopyIntent.Resolve(snapshot) is not null;
        if (!contextMatches || !targetMatches)
        { sequence.Complete(item, "Skipped/refused: original exact copy/source/native/context intent changed"); return; }
        if (item.Entry.Cleanup is not null)
        {
            var matches = CleanupClassifier.ClassifyWithCancellation(snapshot, snapshot.ScannedRoots, null, request.Token)
                .Where(candidate => MatchesCleanupSelection(candidate, item.Entry)).Take(2).ToArray();
            CleanupReviewOutcome? outcome = null;
            var started = false;
            var status = "Failed/uncertain review; inspect the native result before continuing.";
            try
            {
                await _invokeAsync(() =>
                {
                    if (!Current() || !request.TryEnterDispatch()) return;
                    if (matches.Length != 1 || !SameObservedObject(item.Entry.Subject.NativeObject, matches[0].Impact?.Subject.NativeObject))
                    { sequence.Complete(item, "Skipped/refused: exact cleanup issue changed, missing, ambiguous or native evidence unknown"); return; }
                    sequence.CleanupWorkStarted();
                    started = true;
                    outcome = ShowCleanupReview(_getApp()!, report, snapshot, [matches[0]], request.Token);
                }, request.Token).ConfigureAwait(false);
                if (outcome is not null)
                    status = outcome.Changed ? await ReconcileCleanupAsync(outcome).ConfigureAwait(false) : outcome.Summary;
            }
            finally
            {
                if (started) sequence.CompleteWork(item, status);
                else sequence.Complete(item, "Skipped: review request retired before dispatch.");
            }
        }
        else
        {
            var dispatched = false;
            await _invokeAsync(() =>
            {
                if (!Current() || !request.TryEnterDispatch()) return;
                if (!sequence.DispatchUpdate(item, item.Entry.Subject)) return;
                dispatched = true;
                reviewUpdate(item.Entry.Subject);
            }, request.Token).ConfigureAwait(false);
            if (!dispatched) sequence.Complete(item, "Skipped: update handoff retired before dispatch.");
        }
    }

    private static bool SameObservedObject(NativeObjectObservation original, NativeObjectObservation? current) =>
        original.Status == "observed" && current?.Status == "observed"
        && original with { CapturedAt = null } == current with { CapturedAt = null };
}
