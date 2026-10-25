using System.Collections.Immutable;
using System.Text;
using SkillView.Maintenance;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

internal sealed partial class UpdatesTabView
{
    private readonly Func<LocalUpdateRequest, bool, CancellationToken, Task<LocalUpdateResponse>>? _localReviewRunner;
    private Button _preserveButton = null!;
    private Button _localReviewButton = null!;
    private Button _buildMergeButton = null!;
    private View _localPanel = null!;
    private TableView _localPaths = null!;
    private Markdown _localDetail = null!;
    private TextField _resolutionFile = null!;
    private Button _captureResolutionButton = null!;
    private LocalUpdateReview? _localReview;
    private bool _localReviewStarted;
    private bool _localReviewUncertain;

    private void InitializeLocalReviewControls()
    {
        Button Action(string text, Pos x, Pos y, Action action)
        {
            var button = new SingleRowButton { Text = text, X = x, Y = y, Enabled = false };
            button.Accepting += (_, args) => { args.Handled = true; action(); };
            return button;
        }
        _preserveButton = Action("Preserve installed", 0, Pos.AnchorEnd(7),
            () => _runTask(() => RunLocalActionAsync(LocalUpdateAction.Preserve), "updates.preserve"));
        _localReviewButton = Action("Review local", Pos.Right(_preserveButton) + 1, Pos.AnchorEnd(7),
            () => _runTask(() => RunLocalActionAsync(LocalUpdateAction.Review), "updates.local-review"));
        _buildMergeButton = Action("Build merge", Pos.Right(_localReviewButton) + 1, Pos.AnchorEnd(7),
            () => _runTask(() => RunLocalActionAsync(LocalUpdateAction.Build), "updates.build-merge"));
        _localPanel = new View { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(9), Visible = false, CanFocus = true };
        _localPaths = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(40),
            Height = Dim.Fill(4),
            FullRowSelect = true
        };
        TuiHelpers.DisableTypeToSearch(_localPaths);
        TuiHelpers.ConfigureTableNavigation(_localPaths);
        TuiHelpers.ConfigureTableChrome(_localPaths);
        _localPaths.ValueChanged += (_, _) => RenderLocalPath();
        _localDetail = new Markdown
        {
            X = Pos.Right(_localPaths) + 1,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(4)
        };
        TuiHelpers.ConfigureMarkdownPane(_localDetail, Theming.SchemeNames.Base);
        var installed = Action("Use installed", 0, Pos.AnchorEnd(4), () => QueueLocalChoice("installed"));
        var source = Action("Use source", Pos.Right(installed) + 1, Pos.AnchorEnd(4), () => QueueLocalChoice("source"));
        var automatic = Action("Automatic", Pos.Right(source) + 1, Pos.AnchorEnd(4), () => QueueLocalChoice(null));
        var fileLabel = new Label { X = 0, Y = Pos.AnchorEnd(3), Text = "File:" };
        _resolutionFile = new TextField { X = 6, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Text = "" };
        _captureResolutionButton = Action("Capture file for path", 0, Pos.AnchorEnd(2), () => QueueLocalChoice("resolved"));
        var note = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Text = "Private draft; Build merge, review, then approve Apply." };
        _localPanel.Add(_localPaths, _localDetail, installed, source, automatic, fileLabel, _resolutionFile, _captureResolutionButton, note);
        // These choice buttons are enabled by their owning panel after a valid review.
        installed.Enabled = source.Enabled = automatic.Enabled = true;
        TuiHelpers.ApplyScheme(Theming.SchemeNames.Base, _preserveButton, _localReviewButton, _buildMergeButton,
            _localPanel, _localPaths, installed, source, automatic, fileLabel, _resolutionFile, _captureResolutionButton, note);
    }

    private void RefreshLocalReviewControls(bool idle)
    {
        if (_localPanel is null) return;
        var available = idle && _review is not null && _review.PinPolicy is null && _localReviewRunner is not null;
        _preserveButton.Enabled = available;
        _localReviewButton.Enabled = available && _review!.LocalDisposition is null;
        _buildMergeButton.Enabled = available && !_localReviewUncertain && _localReview?.CanMaterialize == true
            && _review!.LocalDisposition is null;
        _localPanel.Enabled = available && !_localReviewUncertain;
        _captureResolutionButton.Enabled = _localPaths.GetSelectedRow() > 0;
    }

    private void ResetLocalReview()
    {
        _localReview = null;
        _localReviewStarted = false;
        _localReviewUncertain = false;
        _resolutionFile.Text = "";
        _localPaths.Table = null;
        _localPanel.Visible = false;
        _preview.Visible = true;
    }

    private void QueueLocalChoice(string? side) =>
        _runTask(() => RunLocalChoiceAsync(side), "updates.choose-local");

    private Task RunLocalChoiceAsync(string? side)
    {
        if (_localReview is not { } local || _localReviewUncertain || _review is null
            || _operationCancellations.HasActive || _loadCancellations.HasActive) return Task.CompletedTask;
        var index = _localPaths.GetSelectedRow();
        if (index < 0 || index > local.Review.Paths.Length) return Task.CompletedTask;
        var path = index == 0 ? "/" : local.Review.Paths[index - 1].Path;
        if (side == "resolved" && (path == "/" || string.IsNullOrWhiteSpace(_resolutionFile.Text.ToString())))
        { _status.Text = " select a payload path and enter a regular file to capture"; return Task.CompletedTask; }
        var choices = local.Review.Paths.Where(row => row.Reason == "explicit-choice" && row.Path != path)
            .Select(row => new LocalPathChoice(row.Path, row.Selection!)).ToList();
        if (local.Review.RootMode.Reason == "explicit-choice" && path != "/")
            choices.Add(new("/", local.Review.RootMode.Selection));
        if (side is not null) choices.Add(new(path, side));
        ImmutableArray<LocalResolutionInput> resolutions = side == "resolved" ? [new(path, _resolutionFile.Text.ToString())] : [];
        return RunLocalActionAsync(LocalUpdateAction.Choose, choices.ToImmutableArray(), resolutions);
    }

    private async Task RunLocalActionAsync(LocalUpdateAction action, ImmutableArray<LocalPathChoice> choices = default,
        ImmutableArray<LocalResolutionInput> resolutions = default)
    {
        if (_review is not { } plan || plan.PinPolicy is not null || _localReviewRunner is null || _loadCancellations.HasActive
            || action is LocalUpdateAction.Choose or LocalUpdateAction.Build && (_localReview is null || _localReviewUncertain)
            || action == LocalUpdateAction.Build && _localReview?.CanMaterialize != true
            || action != LocalUpdateAction.Preserve && plan.LocalDisposition is not null) return;
        using var ownership = _operationCancellations.TryBegin(_lifetimeToken);
        if (ownership is null) return;
        var token = ownership.Token;
        var generation = Interlocked.Increment(ref _reviewGeneration);
        var request = new LocalUpdateRequest(action, plan, _localReview,
            choices.IsDefault ? [] : choices, resolutions.IsDefault ? [] : resolutions);
        var hidden = _options?.AllowHiddenDirectories ?? false;
        var selected = _localPaths.GetSelectedRow();
        // Starting capture closes the old replacement approval, including when
        // capture or UI delivery fails after creating private evidence.
        if (action != LocalUpdateAction.Preserve) _localReviewStarted = true;
        _retainBox.Value = CheckState.UnChecked;
        _sourceReviewBox.Value = CheckState.UnChecked;
        SetBusy(true);
        SetOperationControlsEnabled(false);
        _status.Text = action switch
        {
            LocalUpdateAction.Preserve => " verifying installed payload and owner…",
            LocalUpdateAction.Review => " capturing immutable recorded / installed / source evidence…",
            LocalUpdateAction.Choose => " saving exact path choices and captured file evidence…",
            _ => " building and verifying a private merged candidate…"
        };
        try
        {
            var response = await _localReviewRunner(request, hidden, token).ConfigureAwait(false);
            await _runOnUi(() =>
            {
                if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                if (action == LocalUpdateAction.Preserve)
                {
                    var preserved = response.Plan ?? throw new InvalidOperationException("Preserve returned no verified result.");
                    SaveOutcomes([new(preserved.Subject.InstallationId, preserved.OriginalPath, "preserved", null, null, [], 0, [], preserved.Message)]);
                    ShowPlanPreview(preserved, awaitingReview: false);
                    _review = null;
                    ResetLocalReview();
                    _status.Text = " installed payload preserved; no maintenance preference recorded · Review update prepares this copy again";
                }
                else if (response.Review is { } local)
                {
                    _localReview = local;
                    _localReviewUncertain = false;
                    PopulateLocalReview(selected);
                    _status.Text = local.CanMaterialize ? " choices ready; Build merge for a new approval review"
                        : " resolve path / topology conflicts; inspect metadata refusals in the detail pane";
                }
                else if (action == LocalUpdateAction.Build && response.Plan is { } merged)
                {
                    ResetLocalReview();
                    _review = merged;
                    _localReviewStarted = true;
                    ShowPlanPreview(merged, awaitingReview: true);
                    _status.Text = " merged candidate verified; review exact result, approve retention, then Apply reviewed merge";
                }
                else throw new InvalidOperationException("The local update action returned no review.");
                SetBusy(false);
                ownership.Dispose();
                SetOperationControlsEnabled(true);
                if (_preview.Visible) _preview.SetFocus();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { _logger.Debug("update.tab", "local review canceled during deactivation or teardown"); }
        catch (Exception ex)
        {
            _logger.Error("update.tab", ex.Message);
            try
            {
                await _runOnUi(() =>
                {
                    if (token.IsCancellationRequested || generation != Interlocked.Read(ref _reviewGeneration)) return;
                    _localReviewUncertain |= action != LocalUpdateAction.Preserve;
                    _status.Text = $" local review refused: {TuiHelpers.ErrorSnippet(ex.Message)}; Review local to refresh, or Skip review";
                    SetBusy(false);
                    ownership.Dispose();
                    SetOperationControlsEnabled(true);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    private void PopulateLocalReview(int selected)
    {
        var local = _localReview!;
        var rows = new[] { (Path: "/", Selection: local.Review.RootMode.Selection) }
            .Concat(local.Review.Paths.Select(row => (row.Path, row.Selection ?? "UNRESOLVED"))).ToArray();
        _localPaths.Table = new EnumerableTableSource<(string Path, string Selection)>(rows,
            new Dictionary<string, Func<(string Path, string Selection), object>>
            {
                ["Path"] = row => TerminalEscapeSanitizer.Sanitize(row.Path) ?? "",
                ["Choice"] = row => row.Selection
            });
        _localPaths.Style.GetOrCreateColumnStyle(0).MaxWidth = 24;
        _localPaths.SetSelection(0, Math.Clamp(selected, 0, rows.Length - 1), false);
        _preview.Visible = false;
        _localPanel.Visible = true;
        _resolutionFile.Text = "";
        RenderLocalPath();
        _localPaths.SetFocus();
    }

    private void RenderLocalPath()
    {
        if (_localReview is not { } local) return;
        var sb = new StringBuilder();
        void Field(string name, string? value) => sb.AppendLine($"**{name}:** {MarkdownTableFormatter.FormatCodeSpan(value ?? "absent")}  ");
        Field("Review", local.ReviewFingerprint);
        Field("Evidence", local.ReviewPath);
        if (_localPaths.GetSelectedRow() == 0)
        {
            Field("Path", "/ (installation root mode)");
            Field("Recorded mode", Mode(local.Review.RootMode.Recorded));
            Field("Installed mode", Mode(local.Review.RootMode.Installed));
            Field("Source mode", Mode(local.Review.RootMode.Source));
            Field("Selected", local.Review.RootMode.Selection);
            Field("Reason", local.Review.RootMode.Reason);
        }
        else if (_localPaths.GetSelectedRow() > 0 && _localPaths.GetSelectedRow() <= local.Review.Paths.Length)
        {
            var row = local.Review.Paths[_localPaths.GetSelectedRow() - 1];
            Field("Path", row.Path);
            Field("Recorded", Entry(row.Recorded)); Field("Installed", Entry(row.Installed));
            Field("Source", Entry(row.Source)); Field("Captured file", Entry(row.Resolution));
            Field("Selected", row.Selection ?? "UNRESOLVED"); Field("Reason", row.Reason);
        }
        const int displayedRefusals = 32;
        var refusalCount = local.Review.TopologyConflicts.Length + local.MetadataRefusals.Length;
        foreach (var issue in local.Review.TopologyConflicts.Concat(local.MetadataRefusals).Take(displayedRefusals))
            Field("Refusal", TuiHelpers.Truncate(issue, 2000));
        if (refusalCount > displayedRefusals) Field("Additional refusals", $"{refusalCount - displayedRefusals}; inspect the saved evidence for every item");
        Field("State", local.Message);
        _localDetail.Text = TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? "";
        _captureResolutionButton.Enabled = _localPaths.GetSelectedRow() > 0;
    }

    private static string Mode(int? mode) => mode is { } value ? Convert.ToString(value, 8) : "unspecified";
    private static string? Entry(PayloadEntry? entry) => entry is null ? null
        : $"{entry.Kind}; {entry.Bytes} bytes; mode {Mode(entry.UnixMode)}; SHA256 {entry.ContentHash}"
            + (entry.LinkTarget is { } target ? $"; link {target}" : "");

    internal Task LocalActionForTestsAsync(LocalUpdateAction action) => RunLocalActionAsync(action);
    internal bool LocalFileInputHasFocus => _localPanel.Visible && _resolutionFile.HasFocus;
    internal View LocalPanelForTests => _localPanel;
    internal Task LocalChoiceForTestsAsync(string? side) => RunLocalChoiceAsync(side);
    internal void SelectLocalPathForTests(int row) { _localPaths.SetSelection(0, row, false); RenderLocalPath(); }
    internal TextField ResolutionFileForTests => _resolutionFile;
    internal bool LocalPanelVisibleForTests => _localPanel.Visible;
    internal string LocalDetailForTests => _localDetail.Text.ToString();
    internal Button BuildMergeButtonForTests => _buildMergeButton;
}
