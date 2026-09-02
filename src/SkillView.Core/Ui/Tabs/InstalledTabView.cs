using SkillView.Inventory.Models;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

/// Embedded tab 2 — replaces the modal InstalledScreen.Show() subloop. Lives
/// inside the main window with Visible=false until activated; tab activation
/// triggers a snapshot load via <see cref="LoadAsync"/> and reveals the view.
///
/// All key dispatch + display logic is delegated to the existing static
/// helpers on <see cref="InstalledScreen"/> (BuildShortcuts, DecideShortcut,
/// RenderDetail, ShortcutCommand) so the test surface stays intact.
internal sealed class InstalledTabView : FrameView
{
    private enum SortMode { Name, Package, Location }
    private enum FocusTarget { Table, Filter }
    private const int LocationColumnWidth = 3;
    private const int StateColumnWidth = 5;
    private const int AgentsColumnWidth = 5;

    /// winget-tui-style pin filter cycle for the Installed table.
    /// Maps to: All rows · only pinned · only unpinned.
    internal enum PinFilter { All, PinnedOnly, UnpinnedOnly }

    /// Scope filter cycle. "user"/"project" push down to `gh skill list
    /// --scope`; "custom" filters in-process (gh's --scope only accepts
    /// project|user). "All" captures everything.
    internal enum ScopeFilter { All, User, Project, Custom }

    private readonly Func<Action, Task> _runOnUi;
    private readonly Action<Func<Task>, string> _runTask;
    private readonly Func<CancellationToken, Task<InventorySnapshot>> _snapshotLoader;
    private readonly Func<string?, CancellationToken, Task<InventorySnapshot>>? _scopedSnapshotLoader;
    private readonly Func<InstalledSkill, InventorySnapshot, CancellationToken, Task> _onRemove;
    private readonly Action _onLeaveTab;
    private readonly Action _onGoToSearch;
    private readonly Action? _onStateChange;
    private readonly CancellationToken _lifetimeToken;

    private readonly TextField _filterField;
    private readonly TableView _table;
    private readonly Markdown _detail;
    private readonly Label _footer;
    private readonly SpinnerView _spinner;
    private InventorySnapshot? _snapshot;
    private IReadOnlyList<InstalledSkill> _rows = Array.Empty<InstalledSkill>();
    private IReadOnlyList<InstalledSkill> _all = Array.Empty<InstalledSkill>();
    private SortMode _sort = SortMode.Name;
    private PinFilter _pinFilter = PinFilter.All;
    private ScopeFilter _scopeFilter = ScopeFilter.All;
    private bool _hasPackages;
    private int _packageCount;
    private int _lastWidth = -1;
    private int _nameW = 12;
    private int _pkgW = 18;
    private int _agentsW = 8;
    private FocusTarget _lastFocusedTarget = FocusTarget.Table;
    private long _loadGeneration;
    private readonly CancellationOperationSlot _loadCancellations;
    private readonly CancellationOperationSlot _removeCancellations;
    private int _loadActive;
    private int _removeActive;
    private string? _idleStatusMessage;

    internal InstalledTabView(
        Func<Action, Task> runOnUi,
        Action<Func<Task>, string> runTask,
        Func<CancellationToken, Task<InventorySnapshot>> snapshotLoader,
        Func<InstalledSkill, InventorySnapshot, CancellationToken, Task> onRemove,
        Action onLeaveTab,
        Action onGoToSearch,
        Action? onStateChange = null,
        Func<string?, CancellationToken, Task<InventorySnapshot>>? scopedSnapshotLoader = null,
        CancellationToken lifetimeToken = default,
        Action<AggregateException>? onCancellationCallbackException = null)
    {
        _runOnUi = runOnUi;
        _runTask = runTask;
        _snapshotLoader = snapshotLoader;
        _scopedSnapshotLoader = scopedSnapshotLoader;
        _onRemove = onRemove;
        _onLeaveTab = onLeaveTab;
        _onGoToSearch = onGoToSearch;
        _onStateChange = onStateChange;
        _lifetimeToken = lifetimeToken;
        _loadCancellations = new CancellationOperationSlot(onCancellationCallbackException);
        _removeCancellations = new CancellationOperationSlot(onCancellationCallbackException);

        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;

        var filterLabel = new Label { Text = "Filter:", X = 0, Y = 0 };
        _filterField = new TextField
        {
            X = 8,
            Y = 0,
            Width = Dim.Percent(60) - 8,
            Text = string.Empty,
        };
        var searchScopeLabel = new Label
        {
            X = Pos.Right(_filterField) + 1,
            Y = 0,
            Width = Dim.Fill(),
            Text = BuildSearchScopeText(),
            Enabled = false,
        };
        var legendLabel = new Label
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Text = BuildLegendText(),
            Enabled = false,
        };
        TuiHelpers.ConfigureTextInput(_filterField, SchemeNames.Base);
        _filterField.HasFocusChanged += (_, _) =>
        {
            if (_filterField.HasFocus)
            {
                _lastFocusedTarget = FocusTarget.Filter;
            }
        };

        _table = new TableView
        {
            X = 0,
            Y = 3,
            Width = Dim.Percent(60),
            Height = Dim.Fill(1),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(_table);
        TuiHelpers.ConfigureTableChrome(_table);
        _table.Style.ShowVerticalCellLineForLastColumn = true;
        _table.HasFocusChanged += (_, _) =>
        {
            if (_table.HasFocus)
            {
                _lastFocusedTarget = FocusTarget.Table;
            }
        };

        _detail = new Markdown
        {
            X = Pos.Right(_table) + 1,
            Y = 3,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            Text = "(no skills loaded)",
        };
        TuiHelpers.ConfigureMarkdownPane(_detail, SchemeNames.Base);

        _footer = new Label
        {
            X = 2,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(2),
            Text = " loading inventory…",
        };
        _spinner = new SpinnerView
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = 1,
            Height = 1,
            Visible = true,
            AutoSpin = true,
            Style = new SpinnerStyle.Dots(),
        };

        _filterField.TextChanged += (_, _) => RefreshAll();
        _filterField.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                FocusList();
            }
        };
        _table.ValueChanged += (_, _) =>
        {
            var row = _table.GetSelectedRow();
            if (row >= 0 && row < _rows.Count)
            {
                _detail.Text = RenderDetail(_rows[row]);
                _onStateChange?.Invoke();
            }
        };
        _table.FrameChanged += (_, _) =>
        {
            var width = ResolveWidthForLayout(
                _table.Viewport.Width,
                _table.Frame.Width,
                _lastWidth > 0 ? _lastWidth : 70);
            if (width != _lastWidth)
            {
                RecomputeColumnWidths();
            }
        };

        KeyDown += OnKeyDown;

        TuiHelpers.ApplyScheme(SchemeNames.Base,
            this, filterLabel, searchScopeLabel, legendLabel, _filterField, _table, _detail, _footer, _spinner);

        Add(filterLabel, searchScopeLabel, legendLabel, _filterField, _table, _detail, _spinner, _footer);
        Disposing += (_, _) => CancelPendingWork();
    }

    /// Kick off a snapshot load and reveal the tab. Safe to call repeatedly —
    /// each call refreshes the inventory.
    internal async Task LoadAsync()
    {
        var loadGeneration = Interlocked.Increment(ref _loadGeneration);
        using var loadCancellation = _loadCancellations.Replace(_lifetimeToken);
        Visible = true;
        Interlocked.Exchange(ref _loadActive, 1);
        _idleStatusMessage = null;
        RefreshOperationStatus();
        try
        {
            var snapshot = await CaptureSnapshotAsync(loadCancellation.Token).ConfigureAwait(false);
            await _runOnUi(() =>
            {
                if (!IsCurrentLoad(loadGeneration))
                {
                    return;
                }

                Interlocked.Exchange(ref _loadActive, 0);
                Populate(snapshot);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (loadCancellation.Token.IsCancellationRequested)
        {
            // A newer load or tab deactivation superseded this one.
        }
        catch (Exception ex)
        {
            await _runOnUi(() =>
            {
                if (!IsCurrentLoad(loadGeneration))
                {
                    return;
                }

                Interlocked.Exchange(ref _loadActive, 0);
                _idleStatusMessage = $" inventory load failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
                RefreshOperationStatus();
            }).ConfigureAwait(false);
        }
    }

    /// Populate the tab from an already-captured snapshot without triggering
    /// another async load. Used by startup auto-open so we don't double-scan.
    internal void LoadSeeded(InventorySnapshot snapshot)
    {
        CancelPendingWork();
        Interlocked.Increment(ref _loadGeneration);
        Visible = true;
        Populate(snapshot);
    }

    private void Populate(InventorySnapshot snapshot)
    {
        _snapshot = snapshot;
        _all = snapshot.Skills;
        _packageCount = _all
            .Where(s => s.Package is not null)
            .Select(s => s.Package!.Source)
            .Distinct(StringComparer.Ordinal)
            .Count();
        _hasPackages = _packageCount > 0;
        _sort = _hasPackages ? SortMode.Package : SortMode.Name;

        ApplyFilter();
        RecomputeColumnWidths();
        BuildTableSource();
        ScheduleDeferredWidthStabilization();
        _idleStatusMessage = null;
        _detail.Text = _rows.Count == 0 ? "(no matches)" : RenderDetail(_rows[0]);
        RestoreFocus();
        _onStateChange?.Invoke();
        RefreshOperationStatus();
    }

    private void ApplyFilter()
    {
        var q = _filterField.Text.Trim();
        IEnumerable<InstalledSkill> source = _all;
        if (q.Length > 0)
        {
            var cmp = StringComparison.OrdinalIgnoreCase;
            source = _all.Where(s =>
                s.Name.Contains(q, cmp)
                || s.ResolvedPath.Contains(q, cmp)
                || s.Agents.Any(a => a.AgentId.Contains(q, cmp))
                || (s.Package?.Source.Contains(q, cmp) ?? false));
        }
        // Scope filter is also applied here (not just server-side) so it's
        // correct whether or not the gh push-down ran, and covers "custom".
        if (ToScope(_scopeFilter) is { } wantedScope)
        {
            source = source.Where(s => s.Scope == wantedScope);
        }
        source = _pinFilter switch
        {
            PinFilter.PinnedOnly => source.Where(s => s.Pinned),
            PinFilter.UnpinnedOnly => source.Where(s => !s.Pinned),
            _ => source,
        };
        _rows = ApplySort(source);
    }

    /// Snapshot source for <see cref="LoadAsync"/>. When a scope-aware loader
    /// is wired and the filter is user/project, the capture pushes `--scope`
    /// down to `gh skill list`; otherwise it captures everything and the
    /// in-process filter narrows the view.
    private Task<InventorySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken) =>
        _scopedSnapshotLoader is not null
            ? _scopedSnapshotLoader(ToGhScope(_scopeFilter), cancellationToken)
            : _snapshotLoader(cancellationToken);

    internal static PinFilter CyclePin(PinFilter current) => current switch
    {
        PinFilter.All => PinFilter.PinnedOnly,
        PinFilter.PinnedOnly => PinFilter.UnpinnedOnly,
        _ => PinFilter.All,
    };

    internal static string DescribePin(PinFilter f) => f switch
    {
        PinFilter.PinnedOnly => "pinned only",
        PinFilter.UnpinnedOnly => "unpinned only",
        _ => "all",
    };

    internal static ScopeFilter CycleScope(ScopeFilter current) => current switch
    {
        ScopeFilter.All => ScopeFilter.User,
        ScopeFilter.User => ScopeFilter.Project,
        ScopeFilter.Project => ScopeFilter.Custom,
        _ => ScopeFilter.All,
    };

    internal static string DescribeScope(ScopeFilter f) => f switch
    {
        ScopeFilter.User => "user",
        ScopeFilter.Project => "project",
        ScopeFilter.Custom => "custom",
        _ => "all",
    };

    private static Scope? ToScope(ScopeFilter f) => f switch
    {
        ScopeFilter.User => Scope.User,
        ScopeFilter.Project => Scope.Project,
        ScopeFilter.Custom => Scope.Custom,
        _ => null,
    };

    /// gh's `--scope` accepts only project|user, so "custom" and "all" map to
    /// null (full capture) and are narrowed in-process.
    private static string? ToGhScope(ScopeFilter f) => f switch
    {
        ScopeFilter.User => "user",
        ScopeFilter.Project => "project",
        _ => null,
    };

    private IReadOnlyList<InstalledSkill> ApplySort(IEnumerable<InstalledSkill> input) => _sort switch
    {
        SortMode.Package => input
            .OrderBy(s => s.Package?.Source ?? "~", StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        SortMode.Location => input
            .OrderBy(s => s.Scope)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        _ => input
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
    };

    private void BuildTableSource()
    {
        var columns = new Dictionary<string, Func<InstalledSkill, object>>
        {
            ["Name"] = s => TuiHelpers.Truncate(s.Name, _nameW),
            ["Loc"] = s => InstalledInventoryFormatter.DescribeTableLocation(s),
        };
        if (_hasPackages)
        {
            columns["Package"] = s => TuiHelpers.Truncate(
                InstalledInventoryFormatter.DescribeTablePackageSource(s.Package?.Source),
                _pkgW);
        }
        columns["State"] = s => SkillHealthFormatter.CompactBadge(s.Validity, s.IsSymlinked);
        columns["Agents"] = s => TuiHelpers.Truncate(
            InstalledInventoryFormatter.DescribeAgents(s),
            _agentsW);

        _table.Table = new InstalledTableSource(_rows, columns);
        var style = _table.Style;
        style.ExpandLastColumn = false;
        for (var i = 0; i < _table.Table.Columns; i++)
        {
            var cs = style.GetOrCreateColumnStyle(i);
            switch (_table.Table.ColumnNames[i])
            {
                case "Name": cs.MinWidth = _nameW; cs.MaxWidth = _nameW; break;
                case "Package": cs.MinWidth = _pkgW; cs.MaxWidth = _pkgW; break;
                case "Loc": cs.MinWidth = LocationColumnWidth; cs.MaxWidth = LocationColumnWidth; break;
                case "State": cs.MinWidth = StateColumnWidth; cs.MaxWidth = StateColumnWidth; break;
                case "Agents": cs.MinWidth = AgentsColumnWidth; cs.MaxWidth = AgentsColumnWidth; break;
            }
        }
        _table.Update();
    }

    private void RecomputeColumnWidths()
    {
        var width = ResolveWidthForLayout(
            _table.Viewport.Width,
            _table.Frame.Width,
            _lastWidth > 0 ? _lastWidth : 70);
        _lastWidth = width;

        var available = Math.Max(40, width - 6);
        var fixedCols = LocationColumnWidth + StateColumnWidth + AgentsColumnWidth;
        var remaining = Math.Max(20, available - fixedCols);
        _agentsW = AgentsColumnWidth;
        if (_hasPackages)
        {
            _pkgW = Math.Max(18, (int)Math.Round(remaining * 0.54));
            _nameW = Math.Max(12, remaining - _pkgW);
        }
        else
        {
            _nameW = Math.Max(12, remaining);
        }
        var style = _table.Style;
        for (var i = 0; i < (_table.Table?.Columns ?? 0); i++)
        {
            var name = _table.Table!.ColumnNames[i];
            var cs = style.GetOrCreateColumnStyle(i);
            if (name == "Name")
            {
                cs.MinWidth = _nameW;
                cs.MaxWidth = _nameW;
            }
            else if (name == "Package")
            {
                cs.MinWidth = _pkgW;
                cs.MaxWidth = _pkgW;
            }
            else if (name == "Agents")
            {
                cs.MinWidth = AgentsColumnWidth;
                cs.MaxWidth = AgentsColumnWidth;
            }
        }
        _table.Update();
        _table.SetNeedsDraw();
    }

    internal static int ResolveWidthForLayoutForTests(int viewportWidth, int frameWidth, int fallbackWidth) =>
        ResolveWidthForLayout(viewportWidth, frameWidth, fallbackWidth);

    private static int ResolveWidthForLayout(int viewportWidth, int frameWidth, int fallbackWidth)
    {
        if (viewportWidth > 0)
        {
            return viewportWidth;
        }

        return fallbackWidth;
    }

    internal static string BuildSearchScopeText() => "name · path · agent · package";

    internal static string BuildLegendText() =>
        "Legend: USR user · PRJ project · CUS custom · OK valid · SYM symlink · REV review";

    private void ScheduleDeferredWidthStabilization()
    {
        QueueDeferredWidthStabilization(remainingPasses: 3);
    }

    private void QueueDeferredWidthStabilization(int remainingPasses)
    {
        _runTask(() => RunDeferredWidthStabilizationAsync(remainingPasses), "installed.layout");
    }

    private async Task RunDeferredWidthStabilizationAsync(int remainingPasses)
    {
        try
        {
            await _runOnUi(() =>
            {
                if (!Visible || _table.Table is null)
                {
                    return;
                }

                RecomputeColumnWidths();

                if (remainingPasses > 1)
                {
                    QueueDeferredWidthStabilization(remainingPasses - 1);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            // The application can stop before a queued stabilization callback
            // reaches the UI loop. That is expected teardown, not a crash.
        }
    }

    /// Force focus onto the skill list. Used after the tab is activated so the
    /// browse-first list (not the filter text field) holds focus — otherwise
    /// global hotkeys like `?`, `d`, and `x` get typed into the filter.
    internal void FocusList()
    {
        _lastFocusedTarget = FocusTarget.Table;
        _table.SetFocus();
    }

    private void RestoreFocus()
    {
        switch (_lastFocusedTarget)
        {
            case FocusTarget.Filter:
                _filterField.SetFocus();
                break;
            default:
                _table.SetFocus();
                break;
        }
    }

    private void RefreshAll()
    {
        if (_snapshot is null) return;
        ApplyFilter();
        RecomputeColumnWidths();
        BuildTableSource();
        RefreshOperationStatus();
        _detail.Text = _rows.Count == 0
            ? "(no matches)"
            : RenderDetail(_rows[Math.Clamp(_table.GetSelectedRow(), 0, _rows.Count - 1)]);
        _onStateChange?.Invoke();
    }

    private void RefreshFooter()
    {
        if (_snapshot is null)
        {
            _footer.Text = " (inventory not loaded)";
            return;
        }
        var counts = _rows.Count == _all.Count
            ? $" {_all.Count} skill(s) · {_snapshot.ScannedRoots.Length} location(s)"
            : $" {_rows.Count} of {_all.Count} skill(s) · {_snapshot.ScannedRoots.Length} location(s)";
        var pkgs = _packageCount > 0 ? $" · {_packageCount} package(s)" : "";
        var srcSuffix = _snapshot.UsedGhSkillList ? " · merged inventory" : " · scan only";
        var sortLabel = _sort switch
        {
            SortMode.Package => "package",
            SortMode.Location => "location",
            _ => "name",
        };
        var pinSuffix = _pinFilter == PinFilter.All ? "" : $" · 📌 {DescribePin(_pinFilter)}";
        var scopeSuffix = _scopeFilter == ScopeFilter.All ? "" : $" · scope:{DescribeScope(_scopeFilter)}";
        _footer.Text = $"{counts}{pkgs} · sort {sortLabel}{pinSuffix}{scopeSuffix}{srcSuffix}";
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (key.Handled) return;
        // `P` (capital) cycles the pin filter — handled here rather than via
        // InstalledScreen.DecideShortcut so the static test surface stays
        // unchanged. Only fires when the filter field doesn't own focus so
        // typing a literal P in the filter still works.
        if (!_filterField.HasFocus && key.AsRune.Value == 'P')
        {
            _pinFilter = CyclePin(_pinFilter);
            RefreshAll();
            key.Handled = true;
            return;
        }
        // `G` cycles the scope filter (all → user → project → custom). When a
        // scope-aware loader is wired, user/project re-query `gh skill list
        // --scope` server-side; otherwise it's an in-process narrow.
        if (!_filterField.HasFocus && key.AsRune.Value == 'G')
        {
            _scopeFilter = CycleScope(_scopeFilter);
            if (_scopedSnapshotLoader is not null && _scopeFilter is ScopeFilter.User or ScopeFilter.Project)
            {
                _runTask(LoadAsync, "installed.scope");
            }
            else
            {
                RefreshAll();
            }
            key.Handled = true;
            return;
        }

        var decision = InstalledScreen.DecideShortcut(key, _filterField.HasFocus, canRemove: true);
        if (decision.Command == InstalledScreen.ShortcutCommand.None) return;

        key.Handled = true;
        switch (decision.Command)
        {
            case InstalledScreen.ShortcutCommand.Close:
                _onLeaveTab();
                break;
            case InstalledScreen.ShortcutCommand.GoToSearch:
                _onGoToSearch();
                break;
            case InstalledScreen.ShortcutCommand.FocusFilter:
                _filterField.SetFocus();
                _filterField.SelectAll();
                break;
            case InstalledScreen.ShortcutCommand.FocusTable:
                _table.SetFocus();
                break;
            case InstalledScreen.ShortcutCommand.CycleSort:
                _sort = _sort switch
                {
                    SortMode.Name => _hasPackages ? SortMode.Package : SortMode.Location,
                    SortMode.Package => SortMode.Location,
                    _ => SortMode.Name,
                };
                RefreshAll();
                break;
            case InstalledScreen.ShortcutCommand.Remove:
                TryStartSelectedRemove();
                break;
            case InstalledScreen.ShortcutCommand.Open:
                {
                    var i = _table.GetSelectedRow();
                    if (i >= 0 && i < _rows.Count)
                    {
                        TuiHelpers.OpenInDefaultHandler(_rows[i].ResolvedPath);
                    }
                    break;
                }
        }
    }

    private sealed class InstalledTableSource : EnumerableTableSource<InstalledSkill>
    {
        public InstalledTableSource(IReadOnlyList<InstalledSkill> rows, Dictionary<string, Func<InstalledSkill, object>> cols)
            : base(rows, cols) { }
    }

    private static string RenderDetail(InstalledSkill skill)
    {
        return InstalledScreen.RenderDetail(skill);
    }

    private bool IsCurrentLoad(long loadGeneration) =>
        Interlocked.Read(ref _loadGeneration) == loadGeneration;

    internal void CancelPendingWork()
    {
        Interlocked.Increment(ref _loadGeneration);
        if (_loadCancellations.Cancel())
        {
            Interlocked.Exchange(ref _loadActive, 0);
            RefreshOperationStatus();
        }
        _removeCancellations.Cancel();
    }

    private async Task RunRemoveAsync(InstalledSkill skill, InventorySnapshot snapshot)
    {
        using var removeCancellation = _removeCancellations.Replace(_lifetimeToken);
        var cancellationToken = removeCancellation.Token;
        var shouldRefresh = false;
        try
        {
            await _onRemove(skill, snapshot, cancellationToken).ConfigureAwait(false);
            shouldRefresh = !cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Tab deactivation or app shutdown canceled the preflight before a
            // dialog could be opened.
        }
        finally
        {
            try
            {
                await _runOnUi(() =>
                {
                    Interlocked.Exchange(ref _removeActive, 0);
                    RefreshOperationStatus();
                    if (shouldRefresh && !cancellationToken.IsCancellationRequested)
                    {
                        // The modal invalidates the shared inventory cache when
                        // removal changes the filesystem. Refreshing here also
                        // preserves the prior behavior for canceled dialogs.
                        _runTask(LoadAsync, "installed.remove-refresh");
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                // Expected while the application is tearing down.
            }
        }
    }

    private bool TryStartSelectedRemove()
    {
        var index = _table.GetSelectedRow();
        if (index < 0
            || index >= _rows.Count
            || _snapshot is null
            || Interlocked.CompareExchange(ref _removeActive, 1, 0) != 0)
        {
            return false;
        }

        var skill = _rows[index];
        var snapshot = _snapshot;
        RefreshOperationStatus();
        _runTask(
            () => RunRemoveAsync(skill, snapshot),
            "installed.remove");
        return true;
    }

    private void RefreshOperationStatus()
    {
        if (Volatile.Read(ref _removeActive) != 0)
        {
            SetSpinnerActive(true);
            _footer.Text = " checking removal safety…";
            return;
        }

        if (Volatile.Read(ref _loadActive) != 0)
        {
            SetSpinnerActive(true);
            _footer.Text = " loading inventory…";
            return;
        }

        SetSpinnerActive(false);
        if (_idleStatusMessage is not null)
        {
            _footer.Text = _idleStatusMessage;
            return;
        }

        RefreshFooter();
    }

    private void SetSpinnerActive(bool active)
    {
        _spinner.Visible = active;
        _spinner.AutoSpin = active;
    }

    internal string GetFilterText() => _filterField.Text.Trim();

    internal string? GetPinFilterState() => _pinFilter switch
    {
        PinFilter.PinnedOnly => "pinned only",
        PinFilter.UnpinnedOnly => "unpinned only",
        _ => null
    };

    internal InstalledSkill? GetSelectedSkill()
    {
        var idx = _table.GetSelectedRow();
        return idx >= 0 && idx < _rows.Count ? _rows[idx] : null;
    }

    internal IReadOnlyList<string> VisibleSkillNamesForTests => _rows.Select(s => s.Name).ToArray();

    internal void SetSelectedRowForTests(int row) => _table.SetSelectedRow(row);

    internal void FocusFilterForTests() => _filterField.SetFocus();

    internal bool FilterHasFocus => _filterField.HasFocus;

    internal bool FilterHasFocusForTests => FilterHasFocus;

    internal bool TableHasFocusForTests => _table.HasFocus;

    internal void SendFilterKeyForTests(Key key) => _filterField.NewKeyDownEvent(key);

    internal bool RemoveActiveForTests => Volatile.Read(ref _removeActive) != 0;

    internal bool LoadActiveForTests => Volatile.Read(ref _loadActive) != 0;

    internal bool SpinnerVisibleForTests => _spinner.Visible;

    internal string FooterTextForTests => _footer.Text.ToString();

    internal bool TryStartSelectedRemoveForTests() => TryStartSelectedRemove();
}
