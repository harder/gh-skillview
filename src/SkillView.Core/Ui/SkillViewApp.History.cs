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
        Interlocked.Increment(ref _historyHintGeneration);
        RetainedHistoryScreen.Show(app, _services.Logger, LoadHistoryAsync, async (id, token) =>
        {
            // Explicit user request only: fresh native inventory and the store's
            // existing full Observe path, never cached row metadata as authority.
            var snapshot = await _services.InventoryService.CaptureAsync(_ghPath ?? _services.GhLocator.FindOnPath(token),
                new LocalInventoryService.Options(_options.ScanRoots, true,
                    WorkspaceDirectories: _options.WorkspaceDirectories, Profiles: _options.Profiles)
                { BypassGhCache = true }, token).ConfigureAwait(false);
            return await Task.Run(() => HistoryStore().Inspect(id, snapshot, token), token).ConfigureAwait(false);
        }, (work, name) => RunOwnedTask(work, name, runOnThreadPool: false), GetRunLifetimeToken(), PublishHistoryHint);
    }

    private bool IsHistoryShortcut(Key key) => key.KeyCode == KeyCode.F4;
}
