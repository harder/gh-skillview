using SkillView.Inventory.Models;
using SkillView.Inventory;
using SkillView.Maintenance;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{
    private SingleRowButton? _historyButton;
    private long _historyHintGeneration;

    private ReplacementPlanStore HistoryStore() => new(ReplacementPlanStore.ResolveRoot(_services.Runner), _services.Logger, runner: _services.Runner);

    private async Task<ReplacementHistory> LoadHistoryAsync(CancellationToken token) =>
        await Task.Run(() => HistoryStore().ReadHistory(token, _options.ScanRoots), token).ConfigureAwait(false);

    private void PublishHistoryHint(ReplacementHistory history)
    {
        if (_historyButton is null) return;
        _historyButton.Text = HistoryHintText(history);
    }

    internal static string HistoryHintText(ReplacementHistory history)
    {
        if (history.Error is not null) return "History ? (F4)";
        var recovery = history.Entries.Count(entry => entry.MayNeedRecovery);
        var protectedAttention = history.Entries.Any(entry => !entry.MayNeedRecovery
            && entry.Hint.StartsWith("Protected —", StringComparison.Ordinal));
        // Presentation only: protected entries attract attention without being
        // reclassified as recovery evidence or granting new-review admission.
        if (protectedAttention) return recovery > 0 ? "History: protected +hint F4" : "History: protected (F4)";
        return recovery > 0 ? $"History: {recovery}{(history.HasOmissions ? "+" : "")} recovery hints F4"
            : history.HasOmissions ? "History 16+ (F4)" : "History (F4)";
    }

    private void StartHistoryHint()
    {
        // Called from the first rendered shell iteration, independently of gh
        // probing or inventory. Task admission/dispatch belong to the app.
        var generation = Interlocked.Increment(ref _historyHintGeneration);
        TryRunBackground(async token =>
        {
            var history = await LoadHistoryAsync(token).ConfigureAwait(false);
            await InvokeAsync(() => { if (generation == Interlocked.Read(ref _historyHintGeneration)) PublishHistoryHint(history); }, token).ConfigureAwait(false);
        }, "history.startup");
    }

    private void OpenHistory()
    {
        if (_app is not { } app || GetRunLifetimeToken().IsCancellationRequested) return;
        NoteUserInteraction();
        var generation = Interlocked.Increment(ref _historyHintGeneration);
        void PublishOwnedHint(ReplacementHistory history)
        {
            if (generation == Interlocked.Read(ref _historyHintGeneration)) PublishHistoryHint(history);
        }
        RetainedHistoryScreen.Show(app, _services.Logger, LoadHistoryAsync, async (id, token) =>
        {
            var active = await ActiveHistoryPlanIdsAsync(token).ConfigureAwait(false);
            return await HistoryStore().InspectHistoryAsync(id, CaptureHistoryInventoryAsync, active, token).ConfigureAwait(false);
        }, (work, name) => RunOwnedTask(work, name, runOnThreadPool: false), GetRunLifetimeToken(), PublishOwnedHint,
            retire: async (review, token) =>
            {
                var active = await ActiveHistoryPlanIdsAsync(token).ConfigureAwait(false);
                try { await HistoryStore().RetireAsync(review, review.Kind, CaptureHistoryInventoryAsync, active, token).ConfigureAwait(false); }
                finally
                {
                    // Badge reconciliation survives modal cancellation. App task
                    // ownership remains held until refresh/publication quiesces.
                    var lifetime = GetRunLifetimeToken();
                    var history = await LoadHistoryAsync(lifetime).ConfigureAwait(false);
                    await InvokeAsync(() => PublishOwnedHint(history), lifetime).ConfigureAwait(false);
                }
            });
    }

    private Task<InventorySnapshot> CaptureHistoryInventoryAsync(CancellationToken token) =>
        _services.InventoryService.CaptureAsync(_ghPath ?? _services.GhLocator.FindOnPath(token),
            new LocalInventoryService.Options(_options.ScanRoots, true,
                WorkspaceDirectories: _options.WorkspaceDirectories, Profiles: _options.Profiles)
            { BypassGhCache = true }, token);

    private async Task<System.Collections.Immutable.ImmutableArray<string>> ActiveHistoryPlanIdsAsync(CancellationToken token)
    {
        System.Collections.Immutable.ImmutableArray<string> ids = [];
        await InvokeAsync(() => ids = _updatesTab?.CaptureActivePlanIds() ?? [], token).ConfigureAwait(false);
        return ids;
    }

    private bool IsHistoryShortcut(Key key) => key.KeyCode == KeyCode.F4;
}
