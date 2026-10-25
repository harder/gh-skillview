using SkillView.Maintenance;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

internal sealed partial class UpdatesTabView
{
    private readonly Func<ReplacementResult, bool, CancellationToken, Task<ReplacementResult>>? _preserveRunner;
    private Button _preserveButton = null!;

    private void InitializePreserveControl()
    {
        _preserveButton = new SingleRowButton { Text = "Preserve installed", X = 0, Y = Pos.AnchorEnd(7), Enabled = false };
        _preserveButton.Accepting += (_, args) => { args.Handled = true; _runTask(PreserveAsync, "updates.preserve"); };
        TuiHelpers.ApplyScheme(Theming.SchemeNames.Base, _preserveButton);
    }

    private async Task PreserveAsync()
    {
        if (_review is not { PinPolicy: null } plan || _preserveRunner is null || _loadCancellations.HasActive) return;
        using var ownership = _operationCancellations.TryBegin(_lifetimeToken);
        if (ownership is null) return;
        var token = ownership.Token;
        var generation = Interlocked.Increment(ref _reviewGeneration);
        _retainBox.Value = _sourceReviewBox.Value = CheckState.UnChecked;
        SetBusy(true);
        SetOperationControlsEnabled(false);
        _status.Text = " verifying installed payload and owner…";
        try
        {
            var result = await _preserveRunner(plan, _queueOptions?.AllowHiddenDirectories ?? false, token).ConfigureAwait(false);
            await _runOnUi(() =>
            {
                if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                SaveOutcomes([new(result.Subject.InstallationId, result.OriginalPath, "preserved", null, null, [], 0, [], result.Message)]);
                ShowPlanPreview(result, awaitingReview: false);
                _review = null;
                _status.Text = " installed payload preserved; no maintenance preference recorded · Next / skip continues";
                SetBusy(false);
                ownership.Dispose();
                SetOperationControlsEnabled(true);
                if (_table.Visible) _table.SetFocus();
                else if (_preview.Visible) _preview.SetFocus();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { _logger.Debug("update.tab", "preserve canceled during deactivation or teardown"); }
        catch (Exception ex)
        {
            _logger.Error("update.tab", ex.Message);
            try
            {
                await _runOnUi(() =>
                {
                    if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                    _status.Text = $" preserve refused: {TuiHelpers.ErrorSnippet(ex.Message)}; refresh or Next / skip";
                    SetBusy(false);
                    ownership.Dispose();
                    SetOperationControlsEnabled(true);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    internal Task PreserveForTestsAsync() => PreserveAsync();
}
