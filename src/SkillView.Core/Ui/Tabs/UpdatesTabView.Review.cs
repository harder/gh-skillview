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
    private Button _nextButton = null!;
    private ReplacementResult? _review;
    private Queue<InstalledSkill> _queue = new();
    private GhSkillUpdateService.Options? _queueOptions;
    private readonly List<InstallationUpdateOutcome> _queueOutcomes = [];
    private UpdateResultAccumulator _queueResults = new();
    private long _reviewGeneration;

    private void InitializeReviewControls()
    {
        _sourceReviewBox = new CheckBox
        {
            X = 0,
            Y = Pos.AnchorEnd(8),
            Text = "I reviewed source changes and omissions in F2 Details",
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
        _nextButton = new SingleRowButton
        {
            X = Pos.Right(_applyButton) + 2,
            Y = Pos.AnchorEnd(2),
            Text = "Next / skip",
            Enabled = false
        };
        _retainBox.ValueChanged += (_, _) => RefreshReviewControls();
        _applyButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            _runTask(ApplyReviewAsync, "updates.apply-reviewed");
        };
        _nextButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            _runTask(SkipAndPrepareNextAsync, "updates.next-review");
        };
        TuiHelpers.ApplyScheme(Theming.SchemeNames.Base, _sourceReviewBox, _retainBox, _applyButton, _nextButton);
    }

    private void RefreshReviewControls()
    {
        var idle = !_operationCancellations.HasActive && !_loadCancellations.HasActive;
        var applicable = _review is not null && (!_localReviewStarted || _review.LocalDisposition is not null);
        _retainBox.Enabled = idle && applicable;
        _sourceReviewBox.Enabled = idle && applicable && _review?.SourceReview is not null;
        _applyButton.Enabled = idle && applicable && _applyRunner is not null && _retainBox.Value == CheckState.Checked
            && (_review?.SourceReview is null || _sourceReviewBox.Value == CheckState.Checked);
        _applyButton.Text = _review?.PinPolicy is not null ? "Apply unpin only" : _review?.LocalDisposition is null ? "Replace reviewed" : "Apply reviewed merge";
        _retainBox.Text = _review?.PinPolicy is not null
            ? "Retain original; clear only pin policy for all affected consumers"
            : "Retain original, including local edits; apply this reviewed plan";
        _nextButton.Text = _inventoryNeedsRefresh && _review is null && _queue.Count == 0 ? "Refresh inventory" : "Next / skip";
        _nextButton.Enabled = idle && (_review is not null || _queue.Count > 0 || _inventoryNeedsRefresh);
        RefreshLocalReviewControls(idle);
        RefreshPresentation();
    }

    private async Task PrepareNextAsync()
    {
        if (_operationCancellations.HasActive || _review is not null || _queueOptions is null || !_queue.TryDequeue(out var target)) return;
        var ghPath = _ghPathProvider();
        if (ghPath is null) { _status.Text = " gh unavailable; remaining targets were not started"; return; }
        var options = _queueOptions with { Targets = [target], Skills = [target.Name], All = false };
        await ExecuteReviewOperationAsync(token => _updateRunner(ghPath, options, token), dryRun: false).ConfigureAwait(false);
    }

    private async Task ApplyReviewAsync()
    {
        if (_review is not { } captured || _applyRunner is null || _retainBox.Value != CheckState.Checked
            || captured.SourceReview is not null && _sourceReviewBox.Value != CheckState.Checked
            || _localReviewStarted && captured.LocalDisposition is null
            || _operationCancellations.HasActive || _loadCancellations.HasActive) return;
        var hidden = _queueOptions?.AllowHiddenDirectories ?? false;
        if (captured.SourceReview is not null) captured = captured with { AcknowledgeSourceReview = _sourceReviewBox.Value == CheckState.Checked };
        await ExecuteReviewOperationAsync(token => _applyRunner(captured, hidden, token), dryRun: false, applying: true,
            unpinOnly: captured.PinPolicy is not null).ConfigureAwait(false);
    }

    private Task SkipAndPrepareNextAsync()
    {
        if (_operationCancellations.HasActive || _loadCancellations.HasActive) return Task.CompletedTask;
        if (_inventoryNeedsRefresh && _review is null && _queue.Count == 0)
            return LoadForSubjectAsync(_drillInSubject);
        if (_review is { } review)
        {
            SaveOutcomes([new(review.Subject.InstallationId, review.OriginalPath, "skipped", null, null, [], 0, [],
                $"Saved plan {review.Id} retained without applying; manifest: {review.PlanPath}")]);
            _review = null;
        }
        _retainBox.Value = CheckState.UnChecked;
        _sourceReviewBox.Value = CheckState.UnChecked;
        ResetLocalReview();
        if (_queue.Count > 0) return PrepareNextAsync();
        _queueOptions = null;
        _status.Text = " review queue complete; saved plans remain available for inspection";
        ShowPreview("## Review queue complete\n\nSaved evidence remains available in F2 Details. Choose an installation for another review.", _technicalPreview);
        SetOperationControlsEnabled(true);
        return Task.CompletedTask;
    }

    private async Task ExecuteReviewOperationAsync(Func<CancellationToken, Task<UpdateResult>> operation, bool dryRun, bool applying = false, bool unpinOnly = false)
    {
        using var ownership = _operationCancellations.TryBegin(_lifetimeToken);
        if (ownership is null) return;
        var generation = Interlocked.Increment(ref _reviewGeneration);
        var token = ownership.Token;
        SetBusy(true);
        SetOperationControlsEnabled(false);
        _status.Text = dryRun ? " checking availability…" : applying ? " applying the reviewed candidate…"
            : unpinOnly ? " capturing installed content for a pin-only review…" : " preparing one exact candidate…";
        try
        {
            var result = await operation(token).ConfigureAwait(false);
            // This callback is only cache notification. Production reconciliation
            // belongs to the workflow and runs even if this UI dispatch is canceled.
            if (result.MutationAttempted) _onUpdateApplied();
            await _runOnUi(() =>
            {
                if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                if (!dryRun)
                {
                    _inventoryNeedsRefresh |= result.MutationAttempted;
                    SaveOutcomes(result.Outcomes);
                    _review = applying ? null : result.SavedPlan;
                    ResetLocalReview();
                    _retainBox.Value = CheckState.UnChecked;
                    _sourceReviewBox.Value = CheckState.UnChecked;
                    result = _queueResults.Append(result) with { Outcomes = _queueOutcomes.ToImmutableArray() };
                }
                ShowResultPreview(result, dryRun, unpinOnly);
                _status.Text = dryRun ? result.Succeeded ? " availability check complete; no saved-plan quota consumed" : " availability check failed; inspect outcomes"
                    : _review?.PinPolicy is not null ? " pin-only review required · inspect all consumers, approve retention, then Apply unpin only"
                    : _review is not null ? $" review required · {_queue.Count} remaining · choose preserve, local review, or approve Replace reviewed"
                    : result.Cancelled ? " canceled; inspect saved evidence before recovery"
                    : !result.Succeeded ? $" failed/refused: {TuiHelpers.ErrorSnippet(result.ErrorMessage)} · {_queue.Count} remaining"
                    : _queue.Count > 0 ? $" {result.Outcomes.LastOrDefault()?.Status ?? "complete"} · {_queue.Count} remaining · Next / skip continues"
                    : $" {result.Outcomes.LastOrDefault()?.Status ?? "complete"} · no remaining items · {(_inventoryNeedsRefresh ? "Refresh inventory before another review" : "F2 Details for outcome")}";
                if (_review is null && _queue.Count == 0) _queueOptions = null;
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
            await _runOnUi(() =>
            {
                if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                _status.Text = $" operation failed: {TuiHelpers.ErrorSnippet(ex.Message)}; inspect saved evidence";
                if (_review is null && _queue.Count == 0) _queueOptions = null;
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
            _queueOutcomes.RemoveAll(prior => prior.InstallationId == outcome.InstallationId);
            _queueOutcomes.Add(outcome);
        }
    }

    internal Task ApplyReviewForTestsAsync() => ApplyReviewAsync();
    internal Task NextReviewForTestsAsync() => SkipAndPrepareNextAsync();
    internal CheckBox SourceReviewBoxForTests => _sourceReviewBox;
    internal CheckBox RetainBoxForTests => _retainBox;
    internal Button ApplyButtonForTests => _applyButton;
    internal string PreviewTextForTests => _preview.Text.ToString();
}
