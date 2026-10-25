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
    private bool _inventoryNeedsRefresh;
    private bool _reviewPresentationReady;

    private bool ShowUnpinOnlyAction()
    {
        if (_review is not null || _inventoryNeedsRefresh || _unpinRunner is null) return false;
        var selected = _target;
        return selected is not null && Inventory.Models.PinDisplayPolicy.From(selected).HasPin;
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

    private void ShowResultPreview(UpdateResult result, bool unpinOnly) =>
        ShowPreview(UpdateReviewPresentation.Result(result, _review is not null) + ResourcePresentation(result),
            UpdateScreen.RenderResult(result, unpinOnly: unpinOnly) + ResourcePresentation(result));

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
        if (_undoReview is not null || _review is not null || _choosingVersion || _inventoryNeedsRefresh) return;
        if (_target is not { } target) return;
        var disclosure = _resourceIntent is { } intent ? "\n\nReview replacement for " + MarkdownTableFormatter.FormatCodeSpan(intent.Target)
            + ". " + PackageResourceReplacement.Disclosure : "";
        ShowPreview(UpdateReviewPresentation.Selection(target) + disclosure, "## Selected installation\n\n" + InstalledScreen.RenderDetail(target, showDetails: true, showShortcuts: false) + disclosure);
    }

    // Only layout and visibility change here. Review/apply admission stays in
    // RefreshReviewControls and the existing saved-update coordinator.
    private void RefreshPresentation()
    {
        if (!_reviewPresentationReady) return;
        var undoing = _undoReview is not null;
        var reviewing = undoing || _review is not null || _inventoryNeedsRefresh;
        var applicable = _review is not null;
        var localActions = _review is { PinPolicy: null } && _preserveRunner is not null;
        _reviewHint.Text = _showTechnical ? "F2 Summary · Tab Move focus · Esc Back" : "F2 Details · Tab Move focus · Esc Back";
        if (_options?.Tag is null)
            _tableLabel.Text = undoing ? "Review undo · F2 Details"
                : _inventoryNeedsRefresh && _review is null ? "Refresh inventory before another review · F2 Details"
                : reviewing ? "Review saved change · F2 Details" : "Selected physical copy · F2 Details";
        _preview.Visible = true;
        _hiddenBox.Visible = _unpinBox.Visible = !reviewing;
        _unpinBox.X = 0;
        _hiddenBox.X = Pos.Right(_unpinBox) + 2;
        _updateButton.Visible = _chooseVersionButton.Visible = !reviewing;
        _unpinOnlyButton.Visible = ShowUnpinOnlyAction();
        _sourceReviewBox.Visible = applicable && _review?.SourceReview is not null;
        _retainBox.Visible = _applyButton.Visible = applicable;
        _finishButton.Visible = reviewing && !undoing;
        _undoButton.Visible = !undoing && _undoResult is not null && _review is null
            && _inspectUndoRunner is not null && _undoRunner is not null;
        _confirmUndoButton.Visible = _cancelUndoButton.Visible = _undoApproval.Visible = undoing;
        _preserveButton.Visible = localActions;
        _detailsButton.Visible = true;

        _updateButton.X = 0;
        _chooseVersionButton.X = Pos.Right(_updateButton) + 2;
        _updateButton.Y = _chooseVersionButton.Y = Pos.AnchorEnd(2);
        _unpinOnlyButton.X = 0;
        _unpinOnlyButton.Y = Pos.AnchorEnd(3);
        _applyButton.X = 0;
        _applyButton.Y = _finishButton.Y = Pos.AnchorEnd(2);
        _finishButton.X = applicable ? Pos.Right(_applyButton) + 2 : 0;
        _detailsButton.Y = Pos.AnchorEnd(reviewing ? 2 : 3);
        _detailsButton.X = reviewing ? Pos.Right(_finishButton) + 2
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
        _preview.Height = Dim.Fill(footer);
        SetNeedsLayout();
    }

    internal Button DetailsButtonForTests => _detailsButton;
    internal bool ShowingTechnicalForTests => _showTechnical;
    internal void ToggleDetailsForTests() => TogglePreviewDetails();
}
