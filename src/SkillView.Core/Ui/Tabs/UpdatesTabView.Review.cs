using System.Collections.Immutable;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

internal sealed partial class UpdatesTabView
{
    private readonly Func<ReplacementResult, bool, CancellationToken, Task<UpdateResult>>? _applyRunner;
    private CheckBox _sourceReviewBox = null!;
    private CheckBox _retainBox = null!;
    private Button _applyButton = null!;
    private Button _finishButton = null!;
    private ReplacementResult? _review;
    private GhSkillUpdateService.Options? _options;
    private readonly List<InstallationUpdateOutcome> _outcomes = [];
    private UpdateResultAccumulator _results = new();
    private long _reviewGeneration;

    private void InitializeReviewControls()
    {
        _sourceReviewBox = new CheckBox
        {
            X = 0,
            Y = Pos.AnchorEnd(8),
            Text = "I reviewed F2 source changes/omissions (no safety/authenticity claim)",
            Enabled = false
        };
        _sourceReviewBox.ValueChanged += (_, _) => RefreshReviewControls();
        _retainBox = new CheckBox
        {
            X = 0,
            Y = Pos.AnchorEnd(6),
            Text = "Retain original, including local edits; apply this reviewed plan",
            Enabled = false
        };
        _applyButton = new SingleRowButton
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Text = "Replace reviewed",
            Enabled = false
        };
        _finishButton = new SingleRowButton
        {
            X = Pos.Right(_applyButton) + 2,
            Y = Pos.AnchorEnd(2),
            Text = "Skip review",
            Enabled = false
        };
        _retainBox.ValueChanged += (_, _) => RefreshReviewControls();
        _applyButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            _runTask(ApplyReviewAsync, "updates.apply-reviewed");
        };
        _finishButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            _runTask(FinishReviewAsync, "updates.close-review");
        };
        TuiHelpers.ApplyScheme(Theming.SchemeNames.Base, _sourceReviewBox, _retainBox, _applyButton, _finishButton);
    }

    private void RefreshReviewControls()
    {
        var idle = !_operationCancellations.HasActive && !_loadCancellations.HasActive;
        var applicable = _review is not null;
        _retainBox.Enabled = idle && applicable;
        _sourceReviewBox.Enabled = idle && applicable && _review?.SourceReview is not null;
        _applyButton.Enabled = idle && applicable && _applyRunner is not null && _retainBox.Value == CheckState.Checked
            && (_review?.SourceReview is null || _sourceReviewBox.Value == CheckState.Checked);
        _applyButton.Text = _review?.PinPolicy is not null ? "Apply unpin only" : _review?.LocalDisposition is null ? "Replace reviewed" : "Apply reviewed merge";
        _retainBox.Text = _review?.PinPolicy is not null
            ? "Retain original; clear only pin policy for all affected consumers"
            : "Retain original, including local edits; apply this reviewed plan";
        _finishButton.Text = _inventoryNeedsRefresh && _review is null ? "Refresh inventory" : "Skip review";
        _finishButton.Enabled = idle && (_review is not null || _inventoryNeedsRefresh);
        _preserveButton.Enabled = idle && _review is { PinPolicy: null } && _preserveRunner is not null;
        RefreshUndoControls(idle);
        RefreshPresentation();
    }

    private async Task ApplyReviewAsync()
    {
        if (_review is not { } captured || _applyRunner is null || _retainBox.Value != CheckState.Checked
            || captured.SourceReview is not null && _sourceReviewBox.Value != CheckState.Checked
            || _operationCancellations.HasActive || _loadCancellations.HasActive) return;
        var hidden = _options?.AllowHiddenDirectories ?? false;
        if (captured.SourceReview is not null) captured = captured with { AcknowledgeSourceReview = _sourceReviewBox.Value == CheckState.Checked };
        await ExecuteReviewOperationAsync(token => _applyRunner(captured, hidden, token), applying: true,
            unpinOnly: captured.PinPolicy is not null).ConfigureAwait(false);
    }

    private Task FinishReviewAsync()
    {
        if (_undoReview is not null || _operationCancellations.HasActive || _loadCancellations.HasActive) return Task.CompletedTask;
        if (_review is null) return _inventoryNeedsRefresh ? LoadAsync() : Task.CompletedTask;
        _review = null;
        _retainBox.Value = CheckState.UnChecked;
        _sourceReviewBox.Value = CheckState.UnChecked;
        _options = null;
        const string next = "Return to Installed or Issues to choose another copy.";
        _status.Text = " review closed; " + next;
        ShowPreview("## Review closed\n\nThe prepared change was retained without applying. Saved evidence remains available in F2 Details. " + next, _technicalPreview);
        SetOperationControlsEnabled(true);
        return Task.CompletedTask;
    }

    private async Task ExecuteReviewOperationAsync(Func<CancellationToken, Task<UpdateResult>> operation, bool applying = false, bool unpinOnly = false)
    {
        using var ownership = _operationCancellations.TryBegin(_lifetimeToken);
        if (ownership is null) return;
        if (!applying) ClearUndo();
        var generation = Interlocked.Increment(ref _reviewGeneration);
        var token = ownership.Token;
        var capturedReview = applying ? _review : null;
        var invalidated = false;
        SetBusy(true);
        SetOperationControlsEnabled(false);
        _status.Text = applying ? " applying the reviewed candidate…"
            : unpinOnly ? " capturing installed content for a pin-only review…" : " preparing one exact candidate…";
        try
        {
            var result = await operation(token).ConfigureAwait(false);
            // This callback is only cache notification. Production reconciliation
            // belongs to the workflow and runs even if this UI dispatch is canceled.
            if (result.MutationAttempted) { _onUpdateApplied(); invalidated = true; }
            await _runOnUi(() =>
            {
                if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                _inventoryNeedsRefresh |= result.MutationAttempted;
                SaveOutcomes(result.Outcomes);
                _review = applying ? null : result.SavedPlan;
                _retainBox.Value = CheckState.UnChecked;
                _sourceReviewBox.Value = CheckState.UnChecked;
                var itemResult = result;
                result = _results.Append(result) with { Outcomes = _outcomes.ToImmutableArray() };
                RememberUndo(itemResult, result, applying);
                ShowResultPreview(result, unpinOnly);
                _status.Text = _review?.PinPolicy is not null ? " pin-only review required · inspect all consumers, approve retention, then Apply unpin only"
                    : _review is not null ? " review required · choose Preserve installed or approve Replace reviewed"
                    : result.Cancelled ? " canceled; inspect saved evidence before recovery"
                    : !result.Succeeded ? $" failed/refused: {TuiHelpers.ErrorSnippet(result.ErrorMessage)}"
                    : $" {result.Outcomes.LastOrDefault()?.Status ?? "complete"} · {(_inventoryNeedsRefresh ? "Refresh inventory before another review" : "F2 Details for outcome")}";
                if (_review is null) _options = null;
                SetBusy(false);
                // Ownership remains held until the queued callback returns.
                ownership.Dispose();
                SetOperationControlsEnabled(true);
                if (_preview.Visible) _preview.SetFocus();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { _logger.Debug("update.tab", "update work canceled during deactivation or teardown"); }
        catch (Exception ex)
        {
            _logger.Error("update.tab", ex.Message);
            if (applying && !invalidated) _onUpdateApplied();
            await _runOnUi(() =>
            {
                if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                if (applying)
                {
                    _inventoryNeedsRefresh = true;
                    _review = null;
                    _retainBox.Value = _sourceReviewBox.Value = CheckState.UnChecked;
                    var failure = UpdateResult.Failure(false, 2, ex.Message, []) with
                    {
                        SavedPlan = capturedReview,
                        MutationAttempted = true,
                        Outcomes = capturedReview is null ? [] : [new(capturedReview.Subject.InstallationId, capturedReview.OriginalPath,
                            "recovery-required", null, null, [], 0, [], "Unexpected apply failure; inspect the saved plan before recovery.")]
                    };
                    SaveOutcomes(failure.Outcomes);
                    ShowResultPreview(_results.Append(failure) with { Outcomes = _outcomes.ToImmutableArray() }, unpinOnly);
                }
                _status.Text = $" operation failed: {TuiHelpers.ErrorSnippet(ex.Message)}; inspect saved evidence";
                if (_review is null) _options = null;
                SetBusy(false);
                ownership.Dispose();
                SetOperationControlsEnabled(true);
            }).ConfigureAwait(false);
        }
    }

    private void SaveOutcomes(IEnumerable<InstallationUpdateOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            _outcomes.RemoveAll(prior => prior.InstallationId == outcome.InstallationId);
            _outcomes.Add(outcome);
        }
    }

    internal Task ApplyReviewForTestsAsync() => ApplyReviewAsync();
    internal Task FinishReviewForTestsAsync() => FinishReviewAsync();
    internal CheckBox SourceReviewBoxForTests => _sourceReviewBox;
    internal CheckBox RetainBoxForTests => _retainBox;
    internal Button ApplyButtonForTests => _applyButton;
    internal string PreviewTextForTests => _preview.Text.ToString();
}
