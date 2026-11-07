using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Threading;

namespace SkillView.Ui;

internal sealed record StandaloneMoveResult(QuarantineResult Native, string Verification);

internal sealed partial class SkillViewWorkflowCoordinator
{
    private QuarantineStore Quarantine(Action<string>? afterJournal = null) => new(Path.Combine(Path.GetDirectoryName(MaintenanceBackupStore.ResolveRoot(_services.Runner))!, "quarantine"),
        _services.Logger, afterJournal: afterJournal, runner: _services.Runner);

    internal void ShowMoveAside(InstalledSkill selected, CancellationToken lifetime)
    {
        if (_getApp() is not { } app || lifetime.IsCancellationRequested) return;
        var intent = ExactCopyIntent.Capture(selected);
        var context = SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner);
        QuarantineReview.Show(app, _services.Logger, intent.Subject, lifetime,
            async token =>
            {
                var snapshot = await FreshStandaloneInventoryAsync(context, token).ConfigureAwait(false);
                var target = intent.Resolve(snapshot) ?? throw new IOException("Selected copy changed, disappeared or became ambiguous. Refresh and select it again.");
                return await Task.Run(() => Quarantine().Preview(target, snapshot, token), token).ConfigureAwait(false);
            },
            async (review, standalone, loss, token) =>
            {
                if (!standalone || !loss) throw new IOException("Explicit standalone and effect approvals are required.");
                var snapshot = await FreshStandaloneInventoryAsync(context, token).ConfigureAwait(false);
                var target = intent.Resolve(snapshot) ?? throw new IOException("Selected copy changed; no peer was selected.");
                return await RunStandaloneMutationAsync(context,
                    journal => Quarantine(journal).Apply(target, snapshot, review.ReviewFingerprint, standalone, token), FreshStandaloneInventoryAsync).ConfigureAwait(false);
            },
            async (id, execute, token) =>
            {
                var snapshot = await FreshStandaloneInventoryAsync(context, token).ConfigureAwait(false);
                if (!execute) return new(await Task.Run(() => Quarantine().Undo(id, snapshot, false, token), token).ConfigureAwait(false), "Preview only; no restore attempted.");
                return await RunStandaloneMutationAsync(context, journal => Quarantine(journal).Undo(id, snapshot, true, token), FreshStandaloneInventoryAsync).ConfigureAwait(false);
            });
    }

    private async Task<InventorySnapshot> FreshStandaloneInventoryAsync(string context, CancellationToken token)
    {
        if (context != SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner)) throw new IOException("Child context changed; reopen this exact review.");
        var report = await GetOrProbeReportAsync(token).ConfigureAwait(false);
        var snapshot = await CaptureInventoryCoreAsync(report, token, bypassGhCache: true).ConfigureAwait(false);
        if (context != SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner)) throw new IOException("Child context changed during inventory capture.");
        return snapshot;
    }

    internal async Task<StandaloneMoveResult> RunStandaloneMutationAsync(string context, Func<Action<string>, QuarantineResult> execute,
        Func<string, CancellationToken, Task<InventorySnapshot>> capture)
    {
        QuarantineResult? result = null;
        string verification = "No new native move reported; inspect the backend result.";
        var journaled = false;
        void RecordJournal(string name) { if (name is "prepared.json" or "undo-prepared.json") journaled = true; }
        try { result = await Task.Run(() => execute(RecordJournal)).ConfigureAwait(false); }
        finally
        {
            // Only a durable preparation journal can precede native movement in
            // these backends. Pure admission refusals leave display facts alone.
            // Reconcile possible execution independently of canceled delivery.
            if (journaled || result?.MutationObserved == true || result?.Status is "prepared" or "undo-prepared")
            {
                _services.ListAdapter.Invalidate();
                try
                {
                    using var deadline = new CancellationSource(TimeSpan.FromSeconds(30), CancellationCallbackReporter.For(_services.Logger, "standalone reconciliation"));
                    var generation = _services.ListAdapter.Generation;
                    var snapshot = await capture(context, deadline.Token).ConfigureAwait(false);
                    _displayInventory.PublishFresh(snapshot, generation);
                    verification = snapshot.IsPartial || !snapshot.ConfiguredRootsComplete
                        ? "Native result retained; post-action inventory verification incomplete. Other consumers may exist."
                        : "Post-action inventory captured completely within configured bounds. Running sessions remain unknown.";
                }
                catch (Exception ex)
                {
                    _services.Logger.Error("standalone.reconcile", ex.ToString());
                    verification = "Native result retained; post-action inventory verification failed: "
                        + TuiHelpers.ErrorSnippet(TerminalEscapeSanitizer.Sanitize(ex.Message), 160) + ". Refresh Installed before relying on views.";
                }
            }
        }
        return new(result, verification);
    }
}
