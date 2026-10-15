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
        _retainBox = new CheckBox
        {
            X = 0,
            Y = Pos.AnchorEnd(6),
            Text = "Retain original, including local edits; apply this reviewed plan",
            Enabled = false
        };
        _applyButton = new Button
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Text = "Apply reviewed plan",
            Enabled = false
        };
        _nextButton = new Button
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
        TuiHelpers.ApplyScheme(Theming.SchemeNames.Base, _retainBox, _applyButton, _nextButton);
    }

    private void RefreshReviewControls()
    {
        var idle = !_operationCancellations.HasActive && !_loadCancellations.HasActive;
        _retainBox.Enabled = idle && _review is not null;
        _applyButton.Enabled = idle && _review is not null && _applyRunner is not null && _retainBox.Value == CheckState.Checked;
        _nextButton.Enabled = idle && (_review is not null || _queue.Count > 0);
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
            || _operationCancellations.HasActive || _loadCancellations.HasActive) return;
        var hidden = _queueOptions?.AllowHiddenDirectories ?? false;
        await ExecuteReviewOperationAsync(token => _applyRunner(captured, hidden, token), dryRun: false, applying: true).ConfigureAwait(false);
    }

    private Task SkipAndPrepareNextAsync()
    {
        if (_operationCancellations.HasActive || _loadCancellations.HasActive) return Task.CompletedTask;
        if (_review is { } review)
        {
            SaveOutcomes([new(review.Subject.InstallationId, review.OriginalPath, "skipped", null, null, [], 0, [],
                $"Saved plan {review.Id} retained without applying; manifest: {review.PlanPath}")]);
            _review = null;
        }
        _retainBox.Value = CheckState.UnChecked;
        if (_queue.Count > 0) return PrepareNextAsync();
        _queueOptions = null;
        _status.Text = " review queue complete; saved plans remain available for inspection";
        SetOperationControlsEnabled(true);
        return Task.CompletedTask;
    }

    private async Task ExecuteReviewOperationAsync(Func<CancellationToken, Task<UpdateResult>> operation, bool dryRun, bool applying = false)
    {
        using var ownership = _operationCancellations.TryBegin(_lifetimeToken);
        if (ownership is null) return;
        var generation = Interlocked.Increment(ref _reviewGeneration);
        var token = ownership.Token;
        SetBusy(true);
        SetOperationControlsEnabled(false);
        _status.Text = dryRun ? " checking availability…" : applying ? " applying the reviewed candidate…" : " preparing one exact candidate…";
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
                    SaveOutcomes(result.Outcomes);
                    _review = applying ? null : result.SavedPlan;
                    _retainBox.Value = CheckState.UnChecked;
                    result = _queueResults.Append(result) with { Outcomes = _queueOutcomes.ToImmutableArray() };
                }
                _preview.Text = UpdateScreen.RenderResult(result, dryRun, false, []);
                _status.Text = dryRun ? result.Succeeded ? " availability check complete; no saved-plan quota consumed" : " availability check failed; inspect outcomes"
                    : _review is not null ? $" review required · {_queue.Count} remaining · approve retention, then Apply reviewed plan"
                    : result.Cancelled ? " canceled; inspect saved evidence before recovery"
                    : !result.Succeeded ? $" failed/refused: {TuiHelpers.ErrorSnippet(result.ErrorMessage)} · {_queue.Count} remaining"
                    : $" {result.Outcomes.LastOrDefault()?.Status ?? "complete"} · {_queue.Count} remaining · Next / skip continues";
                SetBusy(false);
                // Ownership remains held until the queued callback returns.
                ownership.Dispose();
                SetOperationControlsEnabled(true);
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
    internal CheckBox RetainBoxForTests => _retainBox;
    internal Button ApplyButtonForTests => _applyButton;
    internal string PreviewTextForTests => _preview.Text.ToString();
}
