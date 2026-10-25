using SkillView.Gh.Models;
using SkillView.Maintenance;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

internal sealed partial class UpdatesTabView
{
    private readonly Button _detailsButton;
    private readonly Label _reviewHint;
    private string _summaryPreview = "";
    private string _technicalPreview = "";
    private bool _showTechnical;
    private bool _singleInstallationView;
    private bool _inventoryNeedsRefresh;
    private bool _reviewPresentationReady;

    private bool ShowUnpinOnlyAction()
    {
        if (_review is not null || _queue.Count > 0 || _inventoryNeedsRefresh || _unpinRunner is null) return false;
        var row = _table.GetSelectedRow();
        var selected = row >= 0 && row < _skills.Count ? _skills[row] : null;
        return selected is not null && (selected.Pinned || selected.Revision.PinnedRef is not null
            || selected.AllOwnershipClaims.Any(owner => owner.PinnedRef is not null));
    }

    private void ShowPreview(string summary, string technical)
    {
        _summaryPreview = summary;
        _technicalPreview = technical;
        _showTechnical = false;
        _preview.Text = summary;
        _detailsButton.Text = "Details (F2)";
        _reviewHint.Text = "F2 Details · Tab Move focus · Esc Back";
        _detailsButton.Enabled = technical.Length > 0;
    }

    private void ShowResultPreview(UpdateResult result, bool dryRun, bool unpinOnly) =>
        ShowPreview(UpdateReviewPresentation.Result(result, dryRun, _review is not null),
            UpdateScreen.RenderResult(result, dryRun, false, [], unpinOnly));

    private void ShowPlanPreview(ReplacementResult plan, bool awaitingReview) =>
        ShowPreview(UpdateReviewPresentation.Plan(plan, awaitingReview), UpdateScreen.RenderPlan(plan));

    private void TogglePreviewDetails()
    {
        if (_choosingVersion || _technicalPreview.Length == 0) return;
        _showTechnical = !_showTechnical;
        _preview.Text = _showTechnical ? _technicalPreview : _summaryPreview;
        _detailsButton.Text = _showTechnical ? "Summary (F2)" : "Details (F2)";
        _reviewHint.Text = _showTechnical ? "F2 Summary · Tab Move focus · Esc Back" : "F2 Details · Tab Move focus · Esc Back";
    }

    private void ShowSelectedPreview()
    {
        if (_undoReview is not null || _review is not null || _queue.Count > 0 || _choosingVersion || _inventoryNeedsRefresh) return;
        var row = _table.GetSelectedRow();
        if (row < 0 || row >= _skills.Count) return;
        var target = _skills[row];
        ShowPreview(UpdateReviewPresentation.Selection(target), "## Selected installation\n\n" + InstalledScreen.RenderDetail(target, showDetails: true, showShortcuts: false));
    }

    // Only layout and visibility change here. Review/apply admission stays in
    // RefreshReviewControls and the existing saved-update coordinator.
    private void RefreshPresentation()
    {
        if (!_reviewPresentationReady) return;
        var undoing = _undoReview is not null;
        var reviewing = undoing || _review is not null || _queue.Count > 0 || _inventoryNeedsRefresh;
        var applicable = _review is not null;
        var localActions = _review is { PinPolicy: null } && _preserveRunner is not null;
        _reviewHint.Text = _showTechnical ? "F2 Summary · Tab Move focus · Esc Back" : "F2 Details · Tab Move focus · Esc Back";
        _statusBar.Visible = !reviewing && !_singleInstallationView;
        _reviewHint.Visible = !_statusBar.Visible;
        if (_queueOptions?.Tag is null)
            _tableLabel.Text = undoing ? "Review undo · F2 Details"
                : _inventoryNeedsRefresh && _review is null && _queue.Count == 0
                ? "Refresh inventory before another review · F2 Details"
                : reviewing ? "Review saved change · F2 Details"
                : _singleInstallationView ? "Selected physical copy · F2 Details"
                : "Space marks · c compare/plan row · u review · U marked · p unpin";
        var tableHadFocus = _table.HasFocus;
        _table.Visible = !reviewing && !_singleInstallationView;
        _preview.Visible = true;
        _preview.X = _table.Visible ? Pos.Right(_table) + 1 : 0;
        _preview.Width = Dim.Fill();

        _allBox.Visible = !reviewing && !_singleInstallationView;
        _hiddenBox.Visible = _unpinBox.Visible = !reviewing;
        _unpinBox.X = _allBox.Visible ? 10 : 0;
        _hiddenBox.X = Pos.Right(_unpinBox) + 2;
        _dryRunButton.Visible = _updateButton.Visible = _chooseVersionButton.Visible = !reviewing;
        _unpinOnlyButton.Visible = ShowUnpinOnlyAction();
        _sourceReviewBox.Visible = applicable && _review?.SourceReview is not null;
        _retainBox.Visible = _applyButton.Visible = applicable;
        _nextButton.Visible = reviewing && !undoing;
        _undoButton.Visible = !undoing && _undoResult is not null && _review is null && _queue.Count == 0
            && _inspectUndoRunner is not null && _undoRunner is not null;
        _confirmUndoButton.Visible = _cancelUndoButton.Visible = _undoApproval.Visible = undoing;
        _preserveButton.Visible = localActions;
        _detailsButton.Visible = true;

        _dryRunButton.X = 0;
        _updateButton.X = Pos.Right(_dryRunButton) + 2;
        _chooseVersionButton.X = Pos.Right(_updateButton) + 2;
        _dryRunButton.Y = _updateButton.Y = _chooseVersionButton.Y = Pos.AnchorEnd(2);
        _unpinOnlyButton.X = 0;
        _unpinOnlyButton.Y = Pos.AnchorEnd(3);
        _applyButton.X = 0;
        _applyButton.Y = _nextButton.Y = Pos.AnchorEnd(2);
        _nextButton.X = applicable ? Pos.Right(_applyButton) + 2 : 0;
        _detailsButton.Y = Pos.AnchorEnd(reviewing ? 2 : 3);
        _detailsButton.X = reviewing ? Pos.Right(_nextButton) + 2
            : _unpinOnlyButton.Visible ? Pos.Right(_unpinOnlyButton) + 2 : 0;
        _undoButton.X = Pos.Right(_detailsButton) + 2;
        _undoButton.Y = _confirmUndoButton.Y = _cancelUndoButton.Y = Pos.AnchorEnd(2);
        _confirmUndoButton.X = 0;
        _cancelUndoButton.X = Pos.Right(_confirmUndoButton) + 2;
        _undoApproval.X = 0;
        _undoApproval.Y = Pos.AnchorEnd(4);
        if (undoing) _detailsButton.X = Pos.Right(_cancelUndoButton) + 2;
        _status.Y = _spinner.Y = Pos.AnchorEnd(reviewing ? 3 : 4);

        var footer = 5;
        if (reviewing)
        {
            var row = 4;
            if (_undoApproval.Visible) row++;
            if (_retainBox.Visible) _retainBox.Y = Pos.AnchorEnd(row++);
            if (_sourceReviewBox.Visible) _sourceReviewBox.Y = Pos.AnchorEnd(row++);
            if (localActions)
            {
                _preserveButton.Y = Pos.AnchorEnd(row++);
            }
            footer = row - 1;
        }
        _table.Height = _preview.Height = Dim.Fill(footer);
        if (tableHadFocus && !_table.Visible && _preview.Visible) _preview.SetFocus();
        SetNeedsLayout();
    }

    internal Button DetailsButtonForTests => _detailsButton;
    internal bool ShowingTechnicalForTests => _showTechnical;
    internal void ToggleDetailsForTests() => TogglePreviewDetails();
}
