using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
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
///   u     — run update on the current row's skill
///   U     — run update on every marked row (batch)
/// Plus the existing Dry-run / Update / Cancel buttons.
///
/// Renders RenderResult markdown into the right pane using the existing
/// static helper on UpdateScreen so the 3 UpdateScreenTests stay green.
internal sealed class UpdatesTabView : FrameView
{
    internal readonly record struct CapabilityUiState(
        bool SupportsAll,
        string AllLabel,
        bool SupportsForce,
        bool SupportsUnpin,
        bool SupportsYes,
        string YesLabel,
        bool YesDefaultChecked,
        bool SupportsDryRun);

    private readonly Func<Action, Task> _runOnUi;
    private readonly Func<Task<InventorySnapshot>> _snapshotLoader;
    private readonly Func<GhSkillUpdateService> _updateServiceFactory;
    private readonly Func<string?> _ghPathProvider;
    private readonly Func<CapabilityProfile> _capabilitiesProvider;
    private readonly Logger _logger;
    private readonly Action _onLeaveTab;
    private readonly Action _onUpdateApplied;

    private readonly Label _tableLabel;
    private readonly TableView _table;
    private readonly Markdown _preview;
    private readonly CheckBox _allBox;
    private readonly CheckBox _forceBox;
    private readonly CheckBox _unpinBox;
    private readonly CheckBox _yesBox;
    private readonly Label _status;
    private readonly SpinnerView _spinner;
    private readonly Button _dryRunButton;
    private readonly Button _updateButton;
    private readonly StatusBar _statusBar;

    private CheckBoxTableSourceWrapperByIndex? _wrapper;
    private IReadOnlyList<InstalledSkill> _skills = Array.Empty<InstalledSkill>();
    private int _nameW = 12;
    private long _loadGeneration;

    internal UpdatesTabView(
        Func<Action, Task> runOnUi,
        Func<Task<InventorySnapshot>> snapshotLoader,
        Func<GhSkillUpdateService> updateServiceFactory,
        Func<string?> ghPathProvider,
        Func<CapabilityProfile> capabilitiesProvider,
        Logger logger,
        Action onLeaveTab,
        Action onUpdateApplied)
    {
        _runOnUi = runOnUi;
        _snapshotLoader = snapshotLoader;
        _updateServiceFactory = updateServiceFactory;
        _ghPathProvider = ghPathProvider;
        _capabilitiesProvider = capabilitiesProvider;
        _logger = logger;
        _onLeaveTab = onLeaveTab;
        _onUpdateApplied = onUpdateApplied;

        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;

        _tableLabel = new Label
        {
            Text = "Select skills to update. Space toggles · a marks all · u updates current · U updates all marked",
            X = 0, Y = 0,
        };
        _table = new TableView
        {
            X = 0, Y = 1,
            Width = Dim.Percent(45),
            Height = Dim.Fill(5),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(_table);
        TuiHelpers.ConfigureTableChrome(_table);

        _preview = new Markdown
        {
            X = Pos.Right(_table) + 1, Y = 1,
            Width = Dim.Fill(), Height = Dim.Fill(5),
            Text = "## Updates\n\n_Loading inventory… press **Dry-run** once loaded to preview pending updates._",
        };
        TuiHelpers.ConfigureMarkdownPane(_preview, SchemeNames.Base);

        _allBox = new CheckBox
        {
            X = 0, Y = Pos.AnchorEnd(4),
            Text = "_all",
        };
        _forceBox = new CheckBox
        {
            X = 10, Y = Pos.AnchorEnd(4), Text = "_force",
        };
        _unpinBox = new CheckBox
        {
            X = 22, Y = Pos.AnchorEnd(4), Text = "_unpin",
        };
        _yesBox = new CheckBox
        {
            X = 34, Y = Pos.AnchorEnd(4),
            Text = "_yes",
        };

        _status = new Label
        {
            X = 0, Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(10),
            Text = " loading inventory…",
        };
        _spinner = new SpinnerView
        {
            X = Pos.AnchorEnd(10), Y = Pos.AnchorEnd(3),
            Width = 1, Height = 1,
            Visible = false, AutoSpin = false,
        };

        _dryRunButton = new Button
        {
            Text = "_Dry-run",
            X = Pos.Center() - 22,
            Y = Pos.AnchorEnd(2),
        };
        _updateButton = new Button
        {
            Text = "_Update",
            X = Pos.Right(_dryRunButton) + 2,
            Y = Pos.AnchorEnd(2),
            IsDefault = true,
        };

        _dryRunButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _ = RunAsync(dryRun: true, batchOnly: false);
        };
        _updateButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _ = RunAsync(dryRun: false, batchOnly: false);
        };

        _statusBar = new StatusBar(TuiHelpers.WithMarkdownShortcuts(
        [
            new Shortcut { Title = "Space", HelpText = "Toggle" },
            new Shortcut { Title = "a",     HelpText = "Mark all" },
            new Shortcut { Title = "u",     HelpText = "Update row" },
            new Shortcut { Title = "U",     HelpText = "Update marked" },
            new Shortcut { Title = "d",     HelpText = "Dry-run" },
            new Shortcut { Key = Key.Esc,   Title = "Esc", HelpText = "Back" },
        ], includeOpenLink: false));

        TuiHelpers.ApplyScheme(SchemeNames.Base,
            this, _tableLabel, _table, _preview,
            _allBox, _forceBox, _unpinBox, _yesBox,
            _status, _spinner, _dryRunButton, _updateButton, _statusBar);

        ApplyCapabilities(_capabilitiesProvider());

        KeyDown += OnKeyDown;

        Add(_tableLabel, _table, _preview,
            _allBox, _forceBox, _unpinBox, _yesBox,
            _status, _spinner, _dryRunButton, _updateButton, _statusBar);
    }

    internal async Task LoadAsync()
    {
        var loadGeneration = Interlocked.Increment(ref _loadGeneration);
        Visible = true;
        RefreshCapabilities();
        _status.Text = " loading inventory…";
        try
        {
            var snapshot = await _snapshotLoader().ConfigureAwait(false);
            await _runOnUi(() =>
            {
                if (!IsCurrentLoad(loadGeneration))
                {
                    return;
                }

                RefreshCapabilities();
                Populate(snapshot);
            }).ConfigureAwait(false);
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
            }).ConfigureAwait(false);
        }
    }

    internal void RefreshCapabilities() => ApplyCapabilities(_capabilitiesProvider());

    internal static CapabilityUiState DescribeCapabilityState(CapabilityProfile caps) => new(
        SupportsAll: caps.SupportsUpdateAll,
        AllLabel: caps.SupportsUpdateAll ? "_all" : "_all (not supported)",
        SupportsForce: caps.SupportsUpdateForce,
        SupportsUnpin: caps.SupportsUpdateUnpin,
        SupportsYes: caps.SupportsUpdateYes,
        YesLabel: caps.SupportsUpdateYes ? "_yes" : "yes (needs gh --yes)",
        YesDefaultChecked: caps.SupportsUpdateYes,
        SupportsDryRun: caps.SupportsUpdateDryRun);

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
        _status.Text = $" {_skills.Count} installed skill(s) — Space to toggle, U to update marked";
        _preview.Text = "## Updates\n\n_Press **Dry-run** to preview, or mark rows and **U** to update all marked._";
        _table.SetFocus();
    }

    private void ApplyCapabilities(CapabilityProfile caps)
    {
        var state = DescribeCapabilityState(caps);

        var allWasEnabled = _allBox.Enabled;
        var forceWasEnabled = _forceBox.Enabled;
        var unpinWasEnabled = _unpinBox.Enabled;
        var yesWasEnabled = _yesBox.Enabled;
        var keepAllChecked = _allBox.Enabled && _allBox.Value == CheckState.Checked;
        var keepForceChecked = _forceBox.Enabled && _forceBox.Value == CheckState.Checked;
        var keepUnpinChecked = _unpinBox.Enabled && _unpinBox.Value == CheckState.Checked;
        var keepYesChecked = _yesBox.Enabled && _yesBox.Value == CheckState.Checked;

        _allBox.Text = state.AllLabel;
        _allBox.Enabled = state.SupportsAll;
        _allBox.Value = state.SupportsAll && allWasEnabled && keepAllChecked
            ? CheckState.Checked
            : CheckState.UnChecked;

        _forceBox.Enabled = state.SupportsForce;
        _forceBox.Value = state.SupportsForce && forceWasEnabled && keepForceChecked
            ? CheckState.Checked
            : CheckState.UnChecked;

        _unpinBox.Enabled = state.SupportsUnpin;
        _unpinBox.Value = state.SupportsUnpin && unpinWasEnabled && keepUnpinChecked
            ? CheckState.Checked
            : CheckState.UnChecked;

        _yesBox.Text = state.YesLabel;
        _yesBox.Enabled = state.SupportsYes;
        _yesBox.Value = state.SupportsYes
            ? (yesWasEnabled
                ? (keepYesChecked ? CheckState.Checked : CheckState.UnChecked)
                : (state.YesDefaultChecked ? CheckState.Checked : CheckState.UnChecked))
            : CheckState.UnChecked;

        _dryRunButton.Enabled = state.SupportsDryRun;
    }

    private bool IsCurrentLoad(long loadGeneration) =>
        Interlocked.Read(ref _loadGeneration) == loadGeneration;

    internal CheckBox AllBoxForTests => _allBox;
    internal Button DryRunButtonForTests => _dryRunButton;
    internal string StatusTextForTests => _status.Text.ToString();
    internal IReadOnlyList<string> LoadedSkillNamesForTests => _skills.Select(s => s.Name).ToArray();

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
        if (key.Handled) return;

        if (key.KeyCode == KeyCode.Esc)
        {
            key.Handled = true;
            _onLeaveTab();
            return;
        }

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
        else if (rune == 'u')
        {
            key.Handled = true;
            UpdateCurrentRow();
        }
        else if (rune == 'U')
        {
            key.Handled = true;
            _ = RunAsync(dryRun: false, batchOnly: true);
        }
        else if (rune == 'd' || rune == 'D')
        {
            key.Handled = true;
            _ = RunAsync(dryRun: true, batchOnly: false);
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
        if (_wrapper is null) return;
        var row = _table.GetSelectedRow();
        if (row < 0 || row >= _skills.Count)
        {
            _status.Text = " no row selected";
            return;
        }
        // Mark just the current row and run a one-skill update.
        _wrapper.CheckedRows.Clear();
        _wrapper.CheckedRows.Add(row);
        _ = RunAsync(dryRun: false, batchOnly: true);
    }

    private async Task RunAsync(bool dryRun, bool batchOnly)
    {
        if (_spinner.Visible) return;
        var ghPath = _ghPathProvider();
        if (ghPath is null)
        {
            _status.Text = " gh not found — press 'd' for Doctor";
            return;
        }
        var caps = _capabilitiesProvider();
        var allChecked = !batchOnly && _allBox.Value == CheckState.Checked;
        var yesChecked = _yesBox.Value == CheckState.Checked;
        var marked = _wrapper is null
            ? new List<string>()
            : Enumerable.Range(0, _skills.Count)
                .Where(i => _wrapper.CheckedRows.Contains(i))
                .Select(i => _skills[i].Name)
                .ToList();

        if (!allChecked && marked.Count == 0)
        {
            _status.Text = dryRun
                ? " pick at least one skill or enable --all to dry-run"
                : " pick at least one skill (Space to mark) or enable --all";
            return;
        }
        if (allChecked && !dryRun && !yesChecked && !caps.SupportsUpdateYes)
        {
            _status.Text = " refusing --all without --yes (would hang on gh's prompt)";
            return;
        }

        _spinner.Visible = true;
        _spinner.AutoSpin = true;
        _status.Text = dryRun
            ? $" dry-running {(allChecked ? "all" : marked.Count + " skill(s)")}…"
            : $" updating {(allChecked ? "all" : marked.Count + " skill(s)")}…";

        var options = new GhSkillUpdateService.Options(
            Skills: marked,
            All: allChecked,
            DryRun: dryRun,
            Force: _forceBox.Value == CheckState.Checked,
            Unpin: _unpinBox.Value == CheckState.Checked,
            Yes: yesChecked,
            Json: false);

        try
        {
            var result = await _updateServiceFactory()
                .UpdateAsync(ghPath, caps, options).ConfigureAwait(false);
            await _runOnUi(() =>
            {
                _spinner.AutoSpin = false;
                _spinner.Visible = false;
                _preview.Text = UpdateScreen.RenderResult(result, dryRun, allChecked, marked);
                if (dryRun)
                {
                    _status.Text = result.Succeeded
                        ? (string.IsNullOrWhiteSpace(result.StdOut)
                            ? " dry-run complete · no updates available"
                            : $" dry-run complete · {result.Entries.Length} entries parsed")
                        : $" dry-run failed (exit {result.ExitCode}): {TuiHelpers.ErrorSnippet(result.ErrorMessage)}";
                }
                else if (result.Succeeded)
                {
                    _status.Text = " update succeeded";
                    _onUpdateApplied();
                }
                else
                {
                    var snippet = TuiHelpers.ErrorSnippet(result.ErrorMessage);
                    _status.Text = snippet.Length > 0
                        ? $" update failed (exit {result.ExitCode}): {snippet}"
                        : $" update failed (exit {result.ExitCode}) — see logs";
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error("update.tab", ex.Message);
            await _runOnUi(() =>
            {
                _spinner.AutoSpin = false;
                _spinner.Visible = false;
                _status.Text = $" update failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
            }).ConfigureAwait(false);
        }
    }
}
