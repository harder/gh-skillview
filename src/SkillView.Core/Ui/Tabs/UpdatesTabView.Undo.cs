using SkillView.Gh.Models;
using SkillView.Maintenance;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

internal sealed partial class UpdatesTabView
{
    private readonly Func<ReplacementResult, bool, CancellationToken, Task<ReplacementResult>>? _inspectUndoRunner;
    private readonly Func<ReplacementResult, bool, CancellationToken, Task<UpdateResult>>? _undoRunner;
    private Button _undoButton = null!;
    private Button _confirmUndoButton = null!;
    private Button _cancelUndoButton = null!;
    private CheckBox _undoApproval = null!;
    private UpdateResult? _undoResult;
    private ReplacementResult? _undoReview;
    private bool _undoHidden;

    private void InitializeUndoControls()
    {
        _undoButton = new SingleRowButton { Text = "Undo this update...", Enabled = false };
        _confirmUndoButton = new SingleRowButton { Text = "Undo update", Enabled = false };
        _cancelUndoButton = new SingleRowButton { Text = "Cancel undo", Enabled = false };
        _undoApproval = new CheckBox { Text = "Restore the retained original for every shown consumer", Enabled = false };
        _undoButton.Accepting += (_, args) => { args.Handled = true; _runTask(() => RunUndoOperationAsync(false), "updates.inspect-undo"); };
        _confirmUndoButton.Accepting += (_, args) => { args.Handled = true; _runTask(() => RunUndoOperationAsync(true), "updates.undo"); };
        _cancelUndoButton.Accepting += (_, args) => { args.Handled = true; CancelUndoReview(); };
        _undoApproval.ValueChanged += (_, _) => RefreshReviewControls();
        TuiHelpers.ApplyScheme(Theming.SchemeNames.Base, _undoButton, _confirmUndoButton, _cancelUndoButton, _undoApproval);
    }

    private void ClearUndo()
    {
        _undoResult = null;
        _undoReview = null;
        _undoApproval.Value = CheckState.UnChecked;
    }

    private void RememberUndo(UpdateResult result, UpdateResult displayResult, bool applying)
    {
        if (!applying || !result.Succeeded || result.Cancelled || result.ReconciliationPartial
            || !result.MutationAttempted || result.SavedPlan is not { Succeeded: true, Canceled: false, Status: "installed", MutationObserved: true } plan
            || !SavedUpdateCoordinator.IsRevisionUpdate(plan)) return;
        // Eligibility belongs to this apply. Keep preparation/apply diagnostics in
        // the result restored when the user cancels undo review.
        _undoResult = displayResult with { SavedPlan = plan };
        _undoHidden = _options?.AllowHiddenDirectories ?? false;
    }

    private void RefreshUndoControls(bool idle)
    {
        if (_undoButton is null) return;
        _undoButton.Enabled = idle && _undoResult is not null && _undoReview is null && _review is null
            && _inspectUndoRunner is not null && _undoRunner is not null;
        _undoApproval.Enabled = idle && _undoReview is not null;
        _confirmUndoButton.Enabled = _undoApproval.Enabled && _undoApproval.Value == CheckState.Checked && _undoRunner is not null;
        _cancelUndoButton.Enabled = idle && _undoReview is not null;
    }

    private void CancelUndoReview()
    {
        if (_undoReview is null || _operationCancellations.HasActive || _loadCancellations.HasActive) return;
        _undoReview = null;
        _undoApproval.Value = CheckState.UnChecked;
        if (_undoResult is { } result) ShowResultPreview(result, false);
        _status.Text = " undo canceled; installed files unchanged; review undo again or Refresh inventory";
        SetOperationControlsEnabled(true);
        _preview.SetFocus();
        _logger.Debug("update.undo", $"Undo review canceled; retryVisible={_undoButton.Visible}; retryEnabled={_undoButton.Enabled}.");
    }

    private async Task RunUndoOperationAsync(bool execute)
    {
        if (_undoResult?.SavedPlan is not { } applied || _inspectUndoRunner is null || _undoRunner is null
            || _review is not null || _loadCancellations.HasActive
            || execute && (_undoReview is null || _undoApproval.Value != CheckState.Checked)
            || !execute && _undoReview is not null) return;
        using var ownership = _operationCancellations.TryBegin(_lifetimeToken);
        if (ownership is null) return;
        var token = ownership.Token;
        var generation = Interlocked.Increment(ref _reviewGeneration);
        var inspected = _undoReview;
        var invalidated = false;
        _undoApproval.Value = CheckState.UnChecked;
        SetBusy(true);
        SetOperationControlsEnabled(false);
        _status.Text = execute ? " undoing the reviewed update…" : " inspecting this update and its retained original…";
        try
        {
            if (!execute)
            {
                var review = await _inspectUndoRunner(applied, _undoHidden, token).ConfigureAwait(false);
                SavedUpdateCoordinator.RequireUndoInspection(applied, review);
                await _runOnUi(() =>
                {
                    if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                    _undoReview = review;
                    ShowPreview(UpdateUndoPresentation.Review(review), "## Undo inspection\n\n" + UpdateScreen.RenderPlan(review));
                    _status.Text = " inspect the restoration and all consumers; approve before Undo update";
                    SetBusy(false);
                    ownership.Dispose();
                    SetOperationControlsEnabled(true);
                    _preview.SetFocus();
                }).ConfigureAwait(false);
            }
            else
            {
                var result = await _undoRunner(inspected!, _undoHidden, token).ConfigureAwait(false);
                if (result.MutationAttempted) { _onUpdateApplied(); invalidated = true; }
                await _runOnUi(() =>
                {
                    if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                    _inventoryNeedsRefresh |= result.MutationAttempted;
                    ClearUndo(); // Every attempt closes this approval, including a refusal.
                    ShowPreview(UpdateUndoPresentation.Result(result), "## Undo result\n\n" + UpdateScreen.RenderResult(result));
                    _status.Text = result.Cancelled ? " undo canceled; inspect saved evidence before recovery"
                        : UpdateUndoPresentation.RestoredWithIncompleteInventory(result) ? " original restored; verification incomplete — Refresh inventory"
                        : result.ReconciliationPartial ? " undo needs review; verification incomplete — Refresh inventory and inspect Details"
                        : result.Succeeded ? " original restored; Refresh inventory to inspect current installed metadata"
                        : " undo failed/refused; inspect F2 Details before recovery";
                    SetBusy(false);
                    ownership.Dispose();
                    SetOperationControlsEnabled(true);
                    _preview.SetFocus();
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { _logger.Debug("update.undo", "undo work canceled during deactivation or teardown"); }
        catch (Exception ex)
        {
            _logger.Error("update.undo", ex.ToString());
            if (execute && !invalidated) _onUpdateApplied();
            await _runOnUi(() =>
            {
                if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                _undoReview = null;
                if (execute) { _inventoryNeedsRefresh = true; ClearUndo(); }
                ShowPreview(UpdateUndoPresentation.Refusal(applied, ex.Message, execute),
                    UpdateUndoPresentation.FailureDetails(inspected ?? applied, ex.Message));
                _status.Text = execute ? " undo needs review; a write may have started — Refresh inventory and inspect F2 Details"
                    : $" undo inspection refused: {TuiHelpers.ErrorSnippet(ex.Message)}; inspect F2 Details";
                SetBusy(false);
                ownership.Dispose();
                SetOperationControlsEnabled(true);
            }).ConfigureAwait(false);
        }
    }

    internal Button UndoButtonForTests => _undoButton;
    internal Button ConfirmUndoButtonForTests => _confirmUndoButton;
    internal CheckBox UndoApprovalForTests => _undoApproval;
    internal Task InspectUndoForTestsAsync() => RunUndoOperationAsync(false);
    internal Task UndoForTestsAsync() => RunUndoOperationAsync(true);
    internal void CancelUndoForTests() => CancelUndoReview();
}
