using SkillView.Inventory;
using SkillView.Maintenance;
using SkillView.Preferences;

namespace SkillView.Ui;

internal sealed partial class SkillViewWorkflowCoordinator
{
    internal async Task ShowIntentionalDifferenceResetAsync(ChangesSelectionGate.Lease request)
    {
        var context = SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner);
        var store = IntentionalDifferenceStore.For(_services.Runner);
        var report = await GetOrProbeReportAsync(request.Token).ConfigureAwait(false);
        var snapshot = await CaptureInventoryAsync(report, request.Token, bypassGhCache: true).ConfigureAwait(false);
        DifferenceResetReview? review = null;
        string? refusal = null;
        try
        {
            review = await Task.Run(() =>
            {
                ManagedSkillDiscovery.RefusePaths([store.Root], request.Token, _services.Runner);
                return store.InspectReset(snapshot, context, request.Token);
            }, request.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
        { refusal = ex.Message; _services.Logger.Error("issues.reset-review", ex.ToString()); }
        await _invokeAsync(() =>
        {
            if (!request.TryEnterDispatch() || _getApp() is not { } app
                || context != SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner)) return;
            IntentionalDifferenceResetScreen.Show(app, _services.Logger, review, store.Root, refusal, async (selected, token) =>
            {
                var inventory = await CaptureInventoryAsync(report, token, bypassGhCache: true).ConfigureAwait(false);
                return await Task.Run(() =>
                {
                    if (context != SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner)) throw new IOException("Captured HOME/CWD/state context changed; reopen Reset.");
                    ManagedSkillDiscovery.RefusePaths([store.Root], token, _services.Runner);
                    var changed = false;
                    try { return store.Reset(selected, inventory, context, token, () => changed = true); }
                    finally { if (changed) _services.ListAdapter.Invalidate(); }
                }, token).ConfigureAwait(false);
            }, request.Token, contextDetails: "Captured CWD: " + _services.Runner.EffectiveWorkingDirectory
                + "\nCaptured HOME: " + (_services.Runner.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") ?? "(unset)"));
        }, request.Token).ConfigureAwait(false);
    }

    internal async Task<string> SetIntentionalDifferenceAsync(CapturedFinding selected, ChangesSelectionGate.Lease request)
    {
        if (selected.Difference is null || selected.Subject is not FindingCopies) throw new IOException("This observation cannot be acknowledged.");
        var context = SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner);
        var report = await GetOrProbeReportAsync(request.Token).ConfigureAwait(false);
        var snapshot = await CaptureInventoryAsync(report, request.Token, bypassGhCache: true).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            request.Token.ThrowIfCancellationRequested();
            if (!request.IsCurrent || context != SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner))
                throw new IOException("This family or installation context changed; refresh Issues before Keep both / Restore suggestions.");
            var store = IntentionalDifferenceStore.For(_services.Runner);
            ManagedSkillDiscovery.RefusePaths([store.Root], request.Token, _services.Runner);
            var changed = false;
            try
            {
                var receipt = store.SetObserved(selected, snapshot, request.Token, () => changed = true);
                _services.Logger.Info("issues.preference", (selected.Intentional ? "Restored suggestions: " : "Kept intentional difference: ") + receipt);
                return (selected.Intentional ? "Suggestions restored. " : "Difference marked intentional. ") + "Preference: " + receipt;
            }
            finally
            {
                // Publication may have happened even when cancellation or a
                // durability failure prevents delivery to the owning view.
                if (changed) _services.ListAdapter.Invalidate();
            }
        }, request.Token).ConfigureAwait(false);
    }
}
