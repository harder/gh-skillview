using SkillView.Gh.Models;
using SkillView.Maintenance;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Terminal.Gui.Editor;

namespace SkillView.Ui.Tabs;

internal sealed partial class UpdatesTabView
{
    private readonly Button _detailsButton;
    private readonly Button _diffButton;
    private readonly Editor _diffPreview;
    private ReplacementResult? _displayedPlan;
    private bool _showDiff;
    private bool _resetPreviewTop;
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

    private void ShowPreview(string summary, string technical, ReplacementResult? plan = null, bool preserveScroll = false)
    {
        var location = _preview.Viewport.Location;
        var wasTechnical = _showTechnical;
        _displayedPlan = plan;
        _showDiff = preserveScroll && _showDiff;
        // A late package note updates the summary behind an open Diff. Keep
        // that pane visible so its focus, caret and scroll remain the reader's.
        _diffPreview.Visible = _showDiff;
        _diffButton.Text = _showDiff ? "Back to review (D)" : "View source changes (D)";
        _summaryPreview = summary;
        _technicalPreview = technical;
        _showTechnical = preserveScroll && wasTechnical;
        _preview.Visible = !_showDiff;
        _preview.Text = _showTechnical ? technical : summary;
        _resetPreviewTop = !preserveScroll;
        TuiHelpers.RestoreMarkdownScroll(_preview, preserveScroll ? location : System.Drawing.Point.Empty);
        _detailsButton.Text = _showTechnical ? "Summary (F2)" : "Details (F2)";
        _reviewHint.Text = "F2 Details · Tab Move focus · Esc Back";
        _detailsButton.Enabled = technical.Length > 0;
    }

    private void ShowResultPreview(UpdateResult result, bool unpinOnly, bool preserveScroll = false) =>
        ShowPreview(ResourcePresentation(result) + UpdateReviewPresentation.Result(result, _review is not null),
            UpdateScreen.RenderResult(result, unpinOnly: unpinOnly) + ResourcePresentation(result), result.SavedPlan, preserveScroll);

    private void ShowPlanPreview(ReplacementResult plan, bool awaitingReview) =>
        ShowPreview(UpdateReviewPresentation.Plan(plan, awaitingReview), UpdateScreen.RenderPlan(plan), plan);

    private void ToggleSourceDiff()
    {
        if (_choosingVersion || _displayedPlan is not { PinPolicy: null } plan
            || _operationCancellations.HasActive || _loadCancellations.HasActive || _undoReview is not null
            || !Visible || _lifetimeToken.IsCancellationRequested) return;
        _showDiff = !_showDiff;
        if (_showDiff)
        {
            _diffPreview.Text = SourceReviewScreen.RenderDiff(plan);
            _diffPreview.Layout();
            _diffPreview.CaretOffset = 0;
            _diffPreview.Viewport = _diffPreview.Viewport with { X = 0, Y = 0 };
        }
        RefreshPresentation();
        if (_showDiff) _diffPreview.SetFocus(); else _diffButton.SetFocus();
    }

    private void TogglePreviewDetails()
    {
        if (_choosingVersion || _technicalPreview.Length == 0) return;
        _showDiff = false;
        _showTechnical = !_showTechnical;
        _preview.Text = _showTechnical ? _technicalPreview : _summaryPreview;
        _resetPreviewTop = true;
        TuiHelpers.RestoreMarkdownScroll(_preview, System.Drawing.Point.Empty);
        _detailsButton.Text = _showTechnical ? "Summary (F2)" : "Details (F2)";
        _reviewHint.Text = _showTechnical ? "F2 Summary · Tab Move focus · Esc Back" : "F2 Details · Tab Move focus · Esc Back";
        RefreshPresentation();
        _preview.SetFocus();
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
        _preview.Visible = !_showDiff;
        _diffPreview.Visible = _showDiff;
        _diffButton.Visible = _displayedPlan is { PinPolicy: null } && !undoing;
        _diffButton.Enabled = !_operationCancellations.HasActive && !_loadCancellations.HasActive && !_lifetimeToken.IsCancellationRequested;
        _diffButton.Text = _showDiff ? "Back to review (D)" : "View source changes (D)";
        if (_showDiff) _reviewHint.Text = "D / Esc Back to review · F2 Technical details · Tab Move focus";
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
            if (localActions || _diffButton.Visible)
            {
                _diffButton.Y = _preserveButton.Y = Pos.AnchorEnd(row++);
                _diffButton.X = 0;
                _preserveButton.X = _diffButton.Visible ? Pos.Right(_diffButton) + 2 : 0;
            }
            footer = row - 1;
        }
        _preview.Height = Dim.Fill(footer);
        if (!reviewing && _diffButton.Visible)
        {
            _diffButton.Y = Pos.AnchorEnd(5);
            _preview.Height = Dim.Fill(6);
        }
        _diffPreview.Height = _preview.Height;
        SetNeedsLayout();
    }

    internal Button DetailsButtonForTests => _detailsButton;
    internal bool ShowingTechnicalForTests => _showTechnical;
    internal void ToggleDetailsForTests() => TogglePreviewDetails();
    internal Button DiffButtonForTests => _diffButton;
    internal Editor DiffPreviewForTests => _diffPreview;
    internal void ToggleDiffForTests() => ToggleSourceDiff();
}
