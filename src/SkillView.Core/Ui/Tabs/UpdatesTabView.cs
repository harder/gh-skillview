using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Threading;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

/// Saved-operation review for one exact subject selected in Installed, Issues,
/// or Copies. Availability discovery belongs to the main views.
internal sealed partial class UpdatesTabView : FrameView
{
    private readonly Func<Action, Task> _runOnUi;
    private readonly Action<Func<Task>, string> _runTask;
    private readonly Func<CancellationToken, Task<InventorySnapshot>> _snapshotLoader;
    private readonly Func<string, GhSkillUpdateService.Options, CancellationToken, Task<UpdateResult>> _updateRunner;
    private readonly Func<InstalledSkill, bool, CancellationToken, Task<UpdateResult>>? _unpinRunner;
    private readonly Func<string?> _ghPathProvider;
    private readonly Logger _logger;
    private readonly Action _onLeaveTab;
    private readonly Action _onUpdateApplied;
    private readonly CancellationToken _lifetimeToken;

    private readonly Label _tableLabel;
    private readonly Markdown _preview;
    private readonly CheckBox _hiddenBox;
    private readonly CheckBox _unpinBox;
    private readonly Label _status;
    private readonly SpinnerView _spinner;
    private readonly Button _updateButton;
    private readonly Button _unpinOnlyButton;
    private readonly Button _chooseVersionButton;
    private readonly Func<InstalledSkill, string?> _chooseTag;
    private bool _choosingVersion;

    private InstalledSkill? _target;
    private long _loadGeneration;
    private OperationSubject? _drillInSubject;
    private readonly CancellationOperationSlot _loadCancellations;
    private readonly CancellationOperationSlot _operationCancellations;

    internal UpdatesTabView(
        Func<Action, Task> runOnUi,
        Action<Func<Task>, string> runTask,
        Func<CancellationToken, Task<InventorySnapshot>> snapshotLoader,
        Func<string, GhSkillUpdateService.Options, CancellationToken, Task<UpdateResult>> updateRunner,
        Func<string?> ghPathProvider,
        Logger logger,
        Action onLeaveTab,
        Action onUpdateApplied,
        CancellationToken lifetimeToken = default,
        Func<ReplacementResult, bool, CancellationToken, Task<UpdateResult>>? applyRunner = null,
        Func<LocalUpdateRequest, bool, CancellationToken, Task<LocalUpdateResponse>>? localReviewRunner = null,
        Func<InstalledSkill, bool, CancellationToken, Task<UpdateResult>>? unpinRunner = null,
        Func<InstalledSkill, string?>? chooseTag = null,
        Func<ReplacementResult, bool, CancellationToken, Task<ReplacementResult>>? inspectUndoRunner = null,
        Func<ReplacementResult, bool, CancellationToken, Task<UpdateResult>>? undoRunner = null)
    {
        _runOnUi = runOnUi;
        _runTask = runTask;
        _snapshotLoader = snapshotLoader;
        _updateRunner = updateRunner;
        _unpinRunner = unpinRunner;
        _inspectUndoRunner = inspectUndoRunner;
        _undoRunner = undoRunner;
        _applyRunner = applyRunner;
        _localReviewRunner = localReviewRunner;
        _ghPathProvider = ghPathProvider;
        _logger = logger;
        _onLeaveTab = onLeaveTab;
        _onUpdateApplied = onUpdateApplied;
        _lifetimeToken = lifetimeToken;
        _chooseTag = chooseTag ?? (selected => App is { } app ? GitTagSelectionModal.Show(app, selected, _lifetimeToken) : null);
        var cancellationReporter = CancellationCallbackReporter.For(logger, "updates tab");
        _loadCancellations = new CancellationOperationSlot(cancellationReporter);
        _operationCancellations = new CancellationOperationSlot(cancellationReporter);

        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;

        _tableLabel = new Label
        {
            Text = "Selected physical copy · F2 Details",
            X = 0,
            Y = 0,
        };
        _preview = new Markdown
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(9),
            Text = "## Update review\n\nSelect an exact copy in Installed or Issues to begin.",
        };
        TuiHelpers.ConfigureMarkdownPane(_preview, SchemeNames.Base);

        _hiddenBox = new CheckBox
        {
            X = 22,
            Y = Pos.AnchorEnd(5),
            Text = "Include _hidden directories",
        };
        _unpinBox = new CheckBox
        {
            X = 10,
            Y = Pos.AnchorEnd(5),
            Text = "_Unpin with update",
        };

        _status = new Label
        {
            X = 2,
            Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(2),
            Text = " loading inventory…",
        };
        _spinner = new SpinnerView
        {
            X = 0,
            Y = Pos.AnchorEnd(4),
            Width = 1,
            Height = 1,
            Visible = false,
            AutoSpin = false,
            Style = new SpinnerStyle.Dots(),
        };

        _updateButton = new SingleRowButton
        {
            Text = "_Review update",
            X = 0,
            Y = Pos.AnchorEnd(3),
            IsDefault = true,
        };
        _unpinOnlyButton = new SingleRowButton
        {
            Text = "_Plan unpin only",
            X = Pos.Right(_updateButton) + 2,
            Y = Pos.AnchorEnd(3),
            Enabled = _unpinRunner is not null
        };

        _chooseVersionButton = new SingleRowButton
        {
            Text = "Choose version...",
            X = Pos.Right(_unpinOnlyButton) + 2,
            Y = Pos.AnchorEnd(3)
        };
        _chooseVersionButton.Accepting += (_, args) => { args.Handled = true; ChooseVersion(); };

        _detailsButton = new SingleRowButton { Text = "Details (F2)", X = 0, Y = Pos.AnchorEnd(3) };
        _reviewHint = new Label { Text = "F2 Details · Tab Move focus · Esc Back", X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        _detailsButton.Accepting += (_, args) => { args.Handled = true; TogglePreviewDetails(); };

        InitializeReviewControls();
        InitializeLocalReviewControls();
        InitializeUndoControls();

        _updateButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _runTask(ReviewUpdateAsync, "updates.prepare");
        };
        _unpinOnlyButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _runTask(RunUnpinAsync, "updates.unpin-plan");
        };

        TuiHelpers.ApplyScheme(SchemeNames.Base,
            this, _tableLabel, _preview,
            _hiddenBox, _unpinBox,
            _status, _spinner, _updateButton, _unpinOnlyButton, _chooseVersionButton, _detailsButton, _reviewHint);

        KeyDown += OnKeyDown;

        Add(_tableLabel, _preview,
            _hiddenBox, _unpinBox,
            _status, _spinner, _updateButton, _unpinOnlyButton, _chooseVersionButton, _detailsButton, _reviewHint, _sourceReviewBox, _retainBox, _applyButton, _finishButton);
        Add(_preserveButton, _localReviewButton, _buildMergeButton, _localPanel);
        Add(_undoButton, _undoApproval, _confirmUndoButton, _cancelUndoButton);
        _reviewPresentationReady = true;
        SetOperationControlsEnabled(false);
        Disposing += (_, _) => CancelPendingWork();
    }

    internal Task LoadAsync() => _drillInSubject is { } subject ? LoadForSubjectAsync(subject) : Task.CompletedTask;

    internal async Task<(InventorySnapshot Snapshot, long Generation)?> LoadForSubjectAsync(OperationSubject selected)
    {
        // App entry points use RunOwnedTask's default runOnThreadPool:false.
        // This pre-await chooser handshake therefore runs on the UI thread;
        // snapshot work may run elsewhere and publication returns through _runOnUi.
        if (_choosingVersion)
        {
            _versionReloadPending = true;
            _versionReloadSubject = selected;
            return null;
        }
        if (_review is not null || _undoReview is not null || _operationCancellations.HasActive) return null;
        _drillInSubject = selected;
        ClearUndo();
        var loadGeneration = Interlocked.Increment(ref _loadGeneration);
        using var loadCancellation = _loadCancellations.Replace(_lifetimeToken);
        Visible = true;
        // Never leave a previous copy actionable after failed/canceled loading.
        _target = null;
        _unpinBox.Value = CheckState.UnChecked;
        ShowPreview("## Selected installation\n\nCapturing fresh inventory for this exact installation.", "");
        SetBusy(true);
        SetOperationControlsEnabled(false);
        _status.Text = " loading inventory…";
        try
        {
            var snapshot = await _snapshotLoader(loadCancellation.Token).ConfigureAwait(false);
            var published = false;
            await _runOnUi(() =>
            {
                if (!IsCurrentLoad(loadGeneration))
                {
                    return;
                }

                _inventoryNeedsRefresh = false;
                PopulateSelected(snapshot, selected);
                published = true;
            }).ConfigureAwait(false);
            return published ? (snapshot, loadGeneration) : null;
        }
        catch (OperationCanceledException) when (loadCancellation.Token.IsCancellationRequested)
        {
            // A newer load or teardown superseded this one.
        }
        catch (Exception ex)
        {
            await _runOnUi(() =>
            {
                if (!IsCurrentLoad(loadGeneration))
                {
                    return;
                }

                _status.Text = $" inventory load failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
                SetBusy(false);
                loadCancellation.Dispose();
                SetOperationControlsEnabled(true);
            }).ConfigureAwait(false);
        }
        return null;
    }

    private void PopulateSelected(InventorySnapshot snapshot, OperationSubject selected)
    {
        _target = MaintenanceQueueBuilder.ResolveInstallation(snapshot, selected);
        SetBusy(false);
        if (_target is null)
        {
            _status.Text = " selected installation is missing or ambiguous — refresh Installed or Issues";
            ShowPreview("## Selection refused\n\nThe selected address no longer identifies exactly one installation. Return to Installed or Issues and refresh disk inventory.", "");
        }
        else
        {
            _status.Text = " exact installation selected · Review update or Choose version";
            ShowSelectedPreview();
        }
        SetOperationControlsEnabled(true);
        _preview.SetFocus();
    }

    private bool IsCurrentLoad(long loadGeneration) =>
        Interlocked.Read(ref _loadGeneration) == loadGeneration && !_lifetimeToken.IsCancellationRequested;

    internal CheckBox UnpinBoxForTests => _unpinBox;
    internal Button ReviewButtonForTests => _updateButton;
    internal Button UnpinOnlyButtonForTests => _unpinOnlyButton;
    internal Task PlanUnpinForTestsAsync() => RunUnpinAsync();
    internal void ChooseVersionForTests() => ChooseVersion();
    internal Button ChooseVersionButtonForTests => _chooseVersionButton;
    internal string StatusTextForTests => _status.Text.ToString();
    internal string TableLabelForTests => _tableLabel.Text.ToString();
    internal string? SelectedInstallationForTests => _target?.InstallationId;
    internal IReadOnlyList<string> LoadedSkillNamesForTests => _target is { } target ? [target.Name] : [];
    internal bool BusyForTests => _spinner.Visible || _spinner.AutoSpin;

    internal void ActivateExactReview(OperationSubject selected, bool prepareImmediately = true)
    {
        Visible = true;
        _runTask(async () =>
        {
            var loaded = await LoadForSubjectAsync(selected).ConfigureAwait(false);
            if (loaded is not { } current) return;
            await _runOnUi(() =>
            {
                if (!Visible || !IsCurrentLoad(current.Generation) || _target is null) return;
                if (prepareImmediately) _runTask(ReviewUpdateAsync, "updates.prepare");
            }).ConfigureAwait(false);
        }, "updates.exact-review");
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (key.Handled || _choosingVersion) return;

        if (key.KeyCode == KeyCode.Esc)
        {
            key.Handled = true;
            if (_undoReview is not null && !_operationCancellations.HasActive) { CancelUndoReview(); return; }
            _onLeaveTab();
            return;
        }

        // Local-review file paths and table navigation belong to their controls.
        // Whole-screen printable shortcuts must not consume typed filenames.
        if (_localPanel.Visible) return;
        if (key.KeyCode == KeyCode.F2)
        {
            key.Handled = true;
            TogglePreviewDetails();
            return;
        }
        // Alt mnemonics and application Ctrl bindings own modified keys.
        if (key.IsAlt || key.IsCtrl) return;

        var rune = key.AsRune.Value;
        if (rune is 'u' or 'U' or 'c')
        {
            key.Handled = true;
            _runTask(ReviewUpdateAsync, "updates.prepare");
        }
        else if (rune == 'p')
        {
            key.Handled = true;
            _runTask(RunUnpinAsync, "updates.unpin-plan");
        }
    }

    internal async Task ReviewUpdateAsync()
    {
        if (_choosingVersion || _undoReview is not null || _inventoryNeedsRefresh || _operationCancellations.HasActive
            || _loadCancellations.HasActive || _review is not null || _target is not { } target) return;
        var ghPath = _ghPathProvider();
        if (ghPath is null) { _status.Text = " gh unavailable; open Doctor for setup"; return; }
        _options = new(Skills: [target.Name], Targets: [target], Unpin: _unpinBox.Value == CheckState.Checked,
            AllowHiddenDirectories: _hiddenBox.Value == CheckState.Checked);
        _outcomes.Clear();
        _results = new();
        await ExecuteReviewOperationAsync(token => _updateRunner(ghPath, _options, token)).ConfigureAwait(false);
    }

    private async Task RunUnpinAsync()
    {
        if (_choosingVersion || _undoReview is not null || _inventoryNeedsRefresh || _unpinRunner is null || _operationCancellations.HasActive || _loadCancellations.HasActive
            || _review is not null) return;
        if (_target is not { } selected) return;
        var hidden = _hiddenBox.Value == CheckState.Checked;
        _options = new([selected.Name], false, false, false, Targets: [selected], AllowHiddenDirectories: hidden);
        _outcomes.Clear();
        _results = new();
        await ExecuteReviewOperationAsync(token => _unpinRunner(selected, hidden, token), unpinOnly: true).ConfigureAwait(false);
    }

    internal void CancelPendingLoad()
    {
        Interlocked.Increment(ref _loadGeneration);
        _versionReloadPending = false;
        _versionReloadSubject = null;
        _loadCancellations.Cancel();
        if (!_operationCancellations.HasActive)
        {
            SetBusy(false);
        }
    }

    internal void CancelPendingWork()
    {
        CancelPendingLoad();
        _operationCancellations.Cancel();
        _review = null;
        ClearUndo();
        ResetLocalReview();
        _options = null;
        _retainBox.Value = CheckState.UnChecked;
        _sourceReviewBox.Value = CheckState.UnChecked;
        SetBusy(false);
        SetOperationControlsEnabled(true);
    }

    private void SetBusy(bool busy)
    {
        _spinner.Visible = busy;
        _spinner.AutoSpin = busy;
        RefreshPresentation();
    }

    private void SetOperationControlsEnabled(bool enabled)
    {
        enabled = enabled && _target is not null && _undoReview is null && !_inventoryNeedsRefresh && _review is null;
        _updateButton.Enabled = enabled;
        _unpinOnlyButton.Enabled = enabled && _unpinRunner is not null;
        _chooseVersionButton.Enabled = enabled && !_lifetimeToken.IsCancellationRequested;
        _tableLabel.Text = _options?.Tag is not null
            ? VersionUpdateGuidance.ShortTagChoice
            : "Selected physical copy · F2 Details";
        _hiddenBox.Enabled = enabled;
        _unpinBox.Enabled = enabled;
        RefreshReviewControls();
    }
}
