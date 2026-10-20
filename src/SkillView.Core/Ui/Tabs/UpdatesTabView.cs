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

/// Embedded tab 3 — replaces the modal UpdateScreen.Show() subloop with a
/// persistent view that follows the winget-tui Updates pattern:
///   Space — toggle mark on current row
///   a     — mark / unmark all visible
///   u     — prepare a review for the current physical installation
///   U     — queue a separate review for every marked row
/// Plus the existing Dry-run / Update / Cancel buttons.
///
/// Renders RenderResult markdown into the right pane using the existing
/// static helper on UpdateScreen so the 3 UpdateScreenTests stay green.
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
    private readonly TableView _table;
    private readonly Markdown _preview;
    private readonly CheckBox _allBox;
    private readonly CheckBox _hiddenBox;
    private readonly CheckBox _unpinBox;
    private readonly Label _status;
    private readonly SpinnerView _spinner;
    private readonly Button _dryRunButton;
    private readonly Button _updateButton;
    private readonly Button _unpinOnlyButton;
    private readonly Button _chooseVersionButton;
    private readonly Func<InstalledSkill, string?> _chooseTag;
    private bool _choosingVersion;
    private readonly StatusBar _statusBar;

    private CheckBoxTableSourceWrapperByIndex? _wrapper;
    private IReadOnlyList<InstalledSkill> _skills = Array.Empty<InstalledSkill>();
    private int _nameW = 12;
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
        Func<InstalledSkill, string?>? chooseTag = null)
    {
        _runOnUi = runOnUi;
        _runTask = runTask;
        _snapshotLoader = snapshotLoader;
        _updateRunner = updateRunner;
        _unpinRunner = unpinRunner;
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
            Text = "Space marks · c compare/plan row · u review · U marked · p unpin",
            X = 0,
            Y = 0,
        };
        _table = new TableView
        {
            X = 0,
            Y = 1,
            Width = Dim.Percent(45),
            Height = Dim.Fill(9),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(_table);
        TuiHelpers.ConfigureTableNavigation(_table);
        TuiHelpers.ConfigureTableChrome(_table);

        _preview = new Markdown
        {
            X = Pos.Right(_table) + 1,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(9),
            Text = "## Updates\n\n_Loading inventory… press **Dry-run** once loaded to preview pending updates._",
        };
        TuiHelpers.ConfigureMarkdownPane(_preview, SchemeNames.Base);

        _allBox = new CheckBox
        {
            X = 0,
            Y = Pos.AnchorEnd(5),
            Text = "_all",
        };
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
            Text = "_unpin",
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

        _dryRunButton = new SingleRowButton
        {
            Text = "_Dry-run",
            X = 0,
            Y = Pos.AnchorEnd(3),
        };
        _updateButton = new SingleRowButton
        {
            Text = "_Update",
            X = Pos.Right(_dryRunButton) + 2,
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

        InitializeReviewControls();
        InitializeLocalReviewControls();

        _dryRunButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _runTask(() => RunAsync(dryRun: true, batchOnly: false), "updates.dry-run");
        };
        _updateButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _runTask(() => RunAsync(dryRun: false, batchOnly: false), "updates.run");
        };
        _unpinOnlyButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _runTask(RunUnpinAsync, "updates.unpin-plan");
        };

        _statusBar = new StatusBar(TuiHelpers.WithMarkdownShortcuts(
        [
            new Shortcut { Title = "Space", HelpText = "Toggle" },
            new Shortcut { Title = "a",     HelpText = "Mark all" },
            new Shortcut { Title = "u",     HelpText = "Review row" },
            new Shortcut { Title = "U",     HelpText = "Review marked" },
            new Shortcut { Title = "d",     HelpText = "Dry-run" },
            new Shortcut { Title = "p",     HelpText = "Unpin row" },
            new Shortcut { Key = Key.Esc,   Title = "Esc", HelpText = "Back" },
        ], includeOpenLink: false));

        TuiHelpers.ApplyScheme(SchemeNames.Base,
            this, _tableLabel, _table, _preview,
            _allBox, _hiddenBox, _unpinBox,
            _status, _spinner, _dryRunButton, _updateButton, _unpinOnlyButton, _chooseVersionButton, _statusBar);

        KeyDown += OnKeyDown;

        Add(_tableLabel, _table, _preview,
            _allBox, _hiddenBox, _unpinBox,
            _status, _spinner, _dryRunButton, _updateButton, _unpinOnlyButton, _chooseVersionButton, _statusBar, _sourceReviewBox, _retainBox, _applyButton, _nextButton);
        Add(_preserveButton, _localReviewButton, _buildMergeButton, _localPanel);
        Disposing += (_, _) => CancelPendingWork();
    }

    internal Task LoadAsync() => LoadForSubjectAsync(_drillInSubject);

    internal Task LoadForSubjectAsync(OperationSubject? selected) => LoadForSubjectCoreAsync(selected);

    private async Task<(InventorySnapshot Snapshot, long Generation)?> LoadForSubjectCoreAsync(OperationSubject? selected)
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
        if (_review is not null || _queue.Count > 0 || _operationCancellations.HasActive) return null;
        var loadGeneration = Interlocked.Increment(ref _loadGeneration);
        using var loadCancellation = _loadCancellations.Replace(_lifetimeToken);
        Visible = true;
        if (selected is not null)
        {
            // A failed or canceled capture must not leave a previous workspace's
            // targets actionable after this load lease ends.
            _skills = [];
            _wrapper = null;
            _table.Table = null;
            _allBox.Value = CheckState.UnChecked;
            _unpinBox.Value = CheckState.UnChecked;
            _preview.Text = "## Selected installation\n\nCapturing fresh inventory for this exact installation.";
        }
        SetBusy(true);
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

                if (selected is null) Populate(snapshot);
                else PopulateSelected(snapshot, selected);
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
            }).ConfigureAwait(false);
        }
        return null;
    }

    private void Populate(InventorySnapshot snapshot)
    {
        _skills = snapshot.Skills;
        var rowsList = _skills.Select((s, i) => (Idx: i, S: s)).ToList();
        var inner = new EnumerableTableSource<(int Idx, InstalledSkill S)>(
            rowsList,
            new Dictionary<string, Func<(int Idx, InstalledSkill S), object>>
            {
                ["Name"] = row => TuiHelpers.Truncate(row.S.Name, _nameW),
                ["Scope"] = row => row.S.Scope.ToString(),
                ["Flags"] = row => (row.S.Pinned ? "p" : "-") + (row.S.IsSymlinked ? "s" : "-"),
            });
        _wrapper = new CheckBoxTableSourceWrapperByIndex(_table, inner);
        _table.Table = _wrapper;
        var style = _table.Style;
        style.ExpandLastColumn = true;
        var nameStyle = style.GetOrCreateColumnStyle(2);
        nameStyle.MinWidth = 8;
        RecomputeColumnWidths();
        _status.Text = $" {_skills.Count} installed skill(s) — Space to toggle, U to review marked";
        _preview.Text = "## Updates\n\n_Press **Dry-run** to preview, or mark rows and **U** to review each installation._";
        _table.SetFocus();
        SetBusy(false);
    }

    private void PopulateSelected(InventorySnapshot snapshot, OperationSubject selected)
    {
        var target = SkillView.Maintenance.MaintenanceQueueBuilder.ResolveInstallation(snapshot, selected);
        // A missing or ambiguous ID must not silently select the first same-name
        // row. Restrict this drill-in to one fresh physical target, and clear
        // broad/batch choices left over from earlier visits.
        _allBox.Value = CheckState.UnChecked;
        _unpinBox.Value = CheckState.UnChecked;
        Populate(snapshot with { Skills = target is null ? [] : [target] });
        if (target is null)
        {
            _status.Text = " selected installation is missing or ambiguous — refresh Changes";
            _preview.Text = "## Selection refused\n\nThe queue address no longer identifies exactly one installation. Return to Changes and refresh disk inventory.";
            return;
        }
        _table.SetSelectedRow(0);
        _wrapper?.CheckedRows.Add(0);
        _status.Text = " exact installation selected · availability unknown · Dry-run checks; c compares/plans";
        _preview.Text = "## Selected installation\n\n" + InstalledScreen.RenderDetail(target)
            + "\n\nUpdate availability is unknown until **Dry-run**. Runtime session state is unknown. **c** compares installed content with one saved candidate and opens its plan review for this installation. Inspect the comparison and approve retention before apply; its result verifies disk, not session activation.";
    }


    private bool IsCurrentLoad(long loadGeneration) =>
        Interlocked.Read(ref _loadGeneration) == loadGeneration && !_lifetimeToken.IsCancellationRequested;

    internal CheckBox UnpinBoxForTests => _unpinBox;
    internal void MarkRowForTests(int row) => _wrapper?.CheckedRows.Add(row);
    internal void SelectRowForTests(int row) => _table.SetSelection(0, row, false);
    internal void UpdateCurrentRowForTests() => UpdateCurrentRow();
    internal CheckBox AllBoxForTests => _allBox;
    internal Button DryRunButtonForTests => _dryRunButton;
    internal Button UnpinOnlyButtonForTests => _unpinOnlyButton;
    internal Task PlanUnpinForTestsAsync() => RunUnpinAsync();
    internal void ChooseVersionForTests() => ChooseVersion();
    internal Button ChooseVersionButtonForTests => _chooseVersionButton;
    internal string StatusTextForTests => _status.Text.ToString();
    internal string TableLabelForTests => _tableLabel.Text.ToString();
    internal string? SelectedInstallationForTests => _table.GetSelectedRow() is var row && row >= 0 && row < _skills.Count ? _skills[row].InstallationId : null;
    internal IReadOnlyList<string> LoadedSkillNamesForTests => _skills.Select(s => s.Name).ToArray();
    internal bool BusyForTests => _spinner.Visible || _spinner.AutoSpin;
    internal Task RunForTestsAsync(bool dryRun, bool batchOnly) => RunAsync(dryRun, batchOnly);

    /// Activate this view as a drill-in from the Changes workspace.
    /// Hides the Changes tab via <paramref name="hideChanges"/>, then shows and loads this view.
    internal void ActivateFromChanges(Action hideChanges, OperationSubject? selected = null)
    {
        _drillInSubject = selected;
        hideChanges();
        Visible = true;
        _runTask(() => LoadForSubjectAsync(selected), "updates.load");
    }

    private void RecomputeColumnWidths()
    {
        var viewportWidth = _table.Viewport.Width;
        var available = viewportWidth > 0 ? Math.Max(30, viewportWidth - 4) : 50;
        _nameW = Math.Max(12, available - 1 - 6 - 5);
        if (_table.Table is null) return;
        var style = _table.Style;
        var nameStyle = style.GetOrCreateColumnStyle(2);
        nameStyle.MaxWidth = _nameW;
        _table.Update();
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (key.Handled || _choosingVersion) return;

        if (key.KeyCode == KeyCode.Esc)
        {
            key.Handled = true;
            _onLeaveTab();
            return;
        }

        // Local-review file paths and table navigation belong to their controls.
        // Whole-screen printable shortcuts must not consume typed filenames.
        if (_localPanel.Visible) return;

        var rune = key.AsRune.Value;
        if (rune == 'a')
        {
            key.Handled = true;
            ToggleAllMarks();
        }
        else if (rune == 'A')
        {
            key.Handled = true;
            ClearAllMarks();
        }
        else if (rune is 'u' or 'c')
        {
            key.Handled = true;
            UpdateCurrentRow();
        }
        else if (rune == 'U')
        {
            key.Handled = true;
            _runTask(() => RunAsync(dryRun: false, batchOnly: true), "updates.batch");
        }
        else if (rune == 'd' || rune == 'D')
        {
            key.Handled = true;
            _runTask(() => RunAsync(dryRun: true, batchOnly: false), "updates.dry-run");
        }
        else if (rune == 'p')
        {
            key.Handled = true;
            _runTask(RunUnpinAsync, "updates.unpin-plan");
        }
    }

    private void ToggleAllMarks()
    {
        if (_wrapper is null) return;
        var allMarked = _wrapper.CheckedRows.Count >= _skills.Count;
        if (allMarked)
        {
            ClearAllMarks();
            _status.Text = " marks cleared";
            return;
        }
        for (var i = 0; i < _skills.Count; i++)
        {
            if (!_wrapper.CheckedRows.Contains(i))
            {
                _wrapper.CheckedRows.Add(i);
            }
        }
        _table.SetNeedsDraw();
        _status.Text = $" marked all {_skills.Count}";
    }

    private void ClearAllMarks()
    {
        if (_wrapper is null) return;
        _wrapper.CheckedRows.Clear();
        _table.SetNeedsDraw();
    }

    private void UpdateCurrentRow()
    {
        if (_choosingVersion || _wrapper is null || _review is not null || _queue.Count > 0 || _operationCancellations.HasActive) return;
        var row = _table.GetSelectedRow();
        if (row < 0 || row >= _skills.Count)
        {
            _status.Text = " no row selected";
            return;
        }
        // Mark just the current row and run a one-skill update.
        _wrapper.CheckedRows.Clear();
        _wrapper.CheckedRows.Add(row);
        _runTask(() => RunAsync(dryRun: false, batchOnly: true), "updates.current");
    }

    private async Task RunAsync(bool dryRun, bool batchOnly)
    {
        if (_choosingVersion || _operationCancellations.HasActive || _loadCancellations.HasActive || _review is not null || _queue.Count > 0) return;
        var ghPath = _ghPathProvider();
        if (ghPath is null)
        {
            _status.Text = " gh not found — press 'd' for Doctor";
            return;
        }
        var allChecked = !batchOnly && _allBox.Value == CheckState.Checked;
        var selected = _wrapper is null
            ? new List<InstalledSkill>()
            : Enumerable.Range(0, _skills.Count)
                .Where(i => _wrapper.CheckedRows.Contains(i))
                .Select(i => _skills[i])
                .ToList();

        var marked = selected.Select(s => s.Name).ToList();
        var targets = allChecked ? _skills : selected;
        if (targets.Count == 0)
        {
            _status.Text = dryRun
                ? " pick at least one skill or enable --all to dry-run"
                : " pick at least one skill (Space to mark) or enable --all";
            return;
        }

        var options = new GhSkillUpdateService.Options(
            Skills: marked.ToArray(), All: allChecked, DryRun: dryRun,
            Unpin: _unpinBox.Value == CheckState.Checked, Targets: targets.ToArray(),
            AllowHiddenDirectories: _hiddenBox.Value == CheckState.Checked);
        if (!dryRun)
        {
            if (targets.Count > SkillUpdateCoordinator.MaxTargets)
            {
                _status.Text = $" select at most {SkillUpdateCoordinator.MaxTargets} installations";
                return;
            }
            _queue = new Queue<InstalledSkill>(targets.DistinctBy(target => target.InstallationId));
            _queueOptions = options;
            _queueOutcomes.Clear();
            _queueResults = new();
            await PrepareNextAsync().ConfigureAwait(false);
        }
        else await ExecuteReviewOperationAsync(
            token => _updateRunner(ghPath, options, token), dryRun: true).ConfigureAwait(false);
    }

    private async Task RunUnpinAsync()
    {
        if (_choosingVersion || _unpinRunner is null || _operationCancellations.HasActive || _loadCancellations.HasActive
            || _review is not null || _queue.Count > 0) return;
        var row = _table.GetSelectedRow();
        if (row < 0 || row >= _skills.Count) { _status.Text = " select one physical installation to unpin"; return; }
        var selected = _skills[row];
        var hidden = _hiddenBox.Value == CheckState.Checked;
        _queueOptions = new([selected.Name], false, false, false, Targets: [selected], AllowHiddenDirectories: hidden);
        _queueOutcomes.Clear();
        _queueResults = new();
        await ExecuteReviewOperationAsync(token => _unpinRunner(selected, hidden, token), dryRun: false, unpinOnly: true).ConfigureAwait(false);
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
        ResetLocalReview();
        _queue.Clear();
        _queueOptions = null;
        _retainBox.Value = CheckState.UnChecked;
        _sourceReviewBox.Value = CheckState.UnChecked;
        SetBusy(false);
        SetOperationControlsEnabled(true);
    }

    private void SetBusy(bool busy)
    {
        _spinner.Visible = busy;
        _spinner.AutoSpin = busy;
    }

    private void SetOperationControlsEnabled(bool enabled)
    {
        enabled = enabled && _review is null && _queue.Count == 0;
        _dryRunButton.Enabled = enabled;
        _updateButton.Enabled = enabled;
        _unpinOnlyButton.Enabled = enabled && _unpinRunner is not null;
        _chooseVersionButton.Enabled = enabled && !_lifetimeToken.IsCancellationRequested;
        _tableLabel.Text = _queueOptions?.Tag is not null
            ? VersionUpdateGuidance.ShortTagChoice
            : "Space marks · c compare/plan row · u review · U marked · p unpin";
        _allBox.Enabled = enabled;
        _hiddenBox.Enabled = enabled;
        _unpinBox.Enabled = enabled;
        _table.Enabled = enabled;
        RefreshReviewControls();
    }
}
