using SkillView.Inventory.Models;
using SkillView.Maintenance;
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
    private const int AgentsColumnWidth = 7;

    /// winget-tui-style pin filter cycle for the Installed table.
    /// Maps to: All rows · only pinned · only unpinned.
    internal enum PinFilter { All, PinnedOnly, UnpinnedOnly }

    /// Scope filter cycle. Inventory is reconciled before selecting physical
    /// rows so every consumer remains visible; agent and scope must match
    /// the same exposure. "All" captures everything.
    internal enum ScopeFilter { All, User, Project, Custom }

    private readonly Func<Action, Task> _runOnUi;
    private readonly Action<Func<Task>, string> _runTask;
    private readonly Func<CancellationToken, Task<InventorySnapshot>> _snapshotLoader;
    private readonly Func<InstalledSkill, InventorySnapshot, CancellationToken, Task> _onRemove;
    private readonly Action<Exception>? _onRemovalError;
    private readonly Func<string, bool> _openPath;
    private readonly Action _onLeaveTab;
    private readonly Action _onGoToSearch;
    private readonly Action? _onStateChange;
    private readonly Action<InstalledSkill>? _onPackageCheck;
    private readonly Action<InstalledSkill>? _onLocalReimport;
    private readonly Action<InstalledSkill>? _onLocalSourceSwitch;
    private readonly CancellationToken _lifetimeToken;
    private readonly UpdateAvailabilitySession? _availability;
    private readonly Action? _onCheckUpdates;
    private readonly Action<InstalledSkill>? _onReviewUpdate;
    private readonly Button _checkUpdates;
    private readonly Button _reviewUpdate;
    private readonly Label _updateStatus;
    private bool _disposed;

    private readonly TextField _filterField;
    private readonly TableView _table;
    private readonly Markdown _detail;
    private readonly Label _footer;
    private readonly TabLoadingView _loading = new("Loading installed skills…");
    private bool _showDetails;
    private readonly SpinnerView _spinner;
    private InventorySnapshot? _snapshot;
    internal InventorySnapshot? CurrentSnapshot => _snapshot;
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
    private FocusTarget _lastFocusedTarget = FocusTarget.Table;
    private long _loadGeneration;
    private readonly CancellationOperationSlot _loadCancellations;
    private readonly CancellationOperationSlot _removeCancellations;
    private int _loadActive;
    private int _removeActive;
    private string? _idleStatusMessage;
    private string? _removalErrorDetail;

    internal InstalledTabView(
        Func<Action, Task> runOnUi,
        Action<Func<Task>, string> runTask,
        Func<CancellationToken, Task<InventorySnapshot>> snapshotLoader,
        Func<InstalledSkill, InventorySnapshot, CancellationToken, Task> onRemove,
        Action onLeaveTab,
        Action onGoToSearch,
        Action? onStateChange = null,
        CancellationToken lifetimeToken = default,
        Action<AggregateException>? onCancellationCallbackException = null,
        Action<InstalledSkill>? onPackageCheck = null,
        Action<InstalledSkill>? onLocalReimport = null, Action<InstalledSkill>? onLocalSourceSwitch = null,
        Action<Exception>? onRemovalError = null, Func<string, bool>? openPath = null,
        UpdateAvailabilitySession? availability = null, Action? onCheckUpdates = null,
        Action<InstalledSkill>? onReviewUpdate = null)
    {
        _runOnUi = runOnUi;
        _runTask = runTask;
        _snapshotLoader = snapshotLoader;
        _onRemove = onRemove;
        _onRemovalError = onRemovalError;
        _openPath = openPath ?? TuiHelpers.OpenInDefaultHandler;
        _onLeaveTab = onLeaveTab;
        _onGoToSearch = onGoToSearch;
        _onStateChange = onStateChange;
        _onPackageCheck = onPackageCheck;
        _onLocalReimport = onLocalReimport;
        _onLocalSourceSwitch = onLocalSourceSwitch;
        _lifetimeToken = lifetimeToken;
        _availability = availability;
        _onCheckUpdates = onCheckUpdates;
        _onReviewUpdate = onReviewUpdate;
        _loadCancellations = new CancellationOperationSlot(onCancellationCallbackException);
        _removeCancellations = new CancellationOperationSlot(onCancellationCallbackException);

        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;
        _checkUpdates = new SingleRowButton { X = 0, Y = 2, Text = "Check updates (C)", Visible = availability is not null };
        _reviewUpdate = new SingleRowButton { X = Pos.Right(_checkUpdates) + 1, Y = 2, Text = "Review update (u)", Visible = availability is not null };
        _updateStatus = new Label { X = Pos.Right(_reviewUpdate) + 1, Y = 2, Width = Dim.Fill(), Text = "Updates not checked", Visible = availability is not null };
        _checkUpdates.Accepting += (_, e) => { e.Handled = true; _onCheckUpdates?.Invoke(); };
        _reviewUpdate.Accepting += (_, e) => { e.Handled = true; ReviewSelectedUpdate(); };

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
            Height = 1,
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
        TuiHelpers.ConfigureTableNavigation(_table);
        TuiHelpers.ConfigureTableChrome(_table);
        _table.Style.ShowVerticalCellLineForLastColumn = true;
        _table.Style.AlwaysUseNormalColorForVerticalCellLines = true;
        _table.Style.AlwaysShowHeaders = true;
        _table.MaxCellWidth = int.MaxValue;
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

        Add(_loading);

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
                RefreshSelectedDetail();
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
        _table.ViewportChanged += (_, _) =>
        {
            if (_table.Viewport.Width > 0 && _table.Viewport.Width != _lastWidth)
                RecomputeColumnWidths();
        };

        KeyDown += OnKeyDown;

        TuiHelpers.ApplyScheme(SchemeNames.Base,
            this, filterLabel, searchScopeLabel, legendLabel, _filterField, _table, _detail, _footer, _spinner);

        Add(filterLabel, searchScopeLabel, legendLabel, _filterField, _table, _detail, _spinner, _footer, _checkUpdates, _reviewUpdate, _updateStatus);
        Disposing += (_, _) => { _disposed = true; CancelPendingWork(); };
    }

    /// Kick off a snapshot load and reveal the tab. Safe to call repeatedly —
    /// each call refreshes the inventory.
    internal async Task LoadAsync()
    {
        var loadGeneration = Interlocked.Increment(ref _loadGeneration);
        using var loadCancellation = _loadCancellations.Replace(_lifetimeToken);
        Visible = true;
        _showDetails = false;
        _detail.Text = string.Empty;
        Interlocked.Exchange(ref _loadActive, 1);
        _idleStatusMessage = null;
        RefreshOperationStatus();
        try
        {
            var snapshot = await _snapshotLoader(loadCancellation.Token).ConfigureAwait(false);
            await _runOnUi(() =>
            {
                if (!IsCurrentLoad(loadGeneration))
                {
                    return;
                }

                Interlocked.Exchange(ref _loadActive, 0);
                if (ReferenceEquals(_snapshot, snapshot))
                {
                    RefreshOperationStatus();
                    RestoreFocus();
                    _onStateChange?.Invoke();
                }
                else Populate(snapshot);
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
        SetContentVisibility(loading: false, removing: Volatile.Read(ref _removeActive) != 0);
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
        _removalErrorDetail = null;
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
        // Select physical rows using the same exposure semantics as discovery,
        // retaining every other consumer in each selected row.
        if (ToScope(_scopeFilter) is { } wantedScope)
        {
            source = source.Where(s => SkillView.Inventory.LocalInventoryService.MatchesFilters(s, wantedScope, null));
        }
        source = _pinFilter switch
        {
            PinFilter.PinnedOnly => source.Where(s => PinDisplayPolicy.From(s).HasPin),
            PinFilter.UnpinnedOnly => source.Where(s => PinDisplayPolicy.From(s).State == PinDisplayState.Unpinned),
            _ => source,
        };
        _rows = ApplySort(source);
    }

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
        // Let TableView truncate by terminal cell width. Pre-truncating the data
        // loses text permanently when a resize makes a column wider.
        var columns = new Dictionary<string, Func<InstalledSkill, object>>
        {
            ["Name"] = s => s.Name,
            ["State"] = s => SkillHealthFormatter.CompactBadge(s.Validity, s.IsSymlinked),
            ["Loc"] = s => InstalledInventoryFormatter.DescribeTableLocation(s),
            ["Agents"] = s => InstalledInventoryFormatter.DescribeAgents(s),
        };
        if (_availability is not null) columns["Updates"] = s => _availability.Get(s).TableLabel;
        if (_hasPackages)
        {
            columns["Package"] = s => InstalledInventoryFormatter.DescribeTablePackageSource(s.Package?.Source);
        }

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
                case "Updates": cs.MinWidth = 11; cs.MaxWidth = 11; break;
            }
        }
        ConfigureCategoryColors();
        _table.Update();
    }

    private void ConfigureCategoryColors()
    {
        var baseScheme = _table.GetScheme();
        // Allocate these few schemes once per table source, never per cell draw.
        var colors = new InstalledCategoryColors(baseScheme, TuiHelpers.CurrentTheme);
        var locations = _rows.Select(s => colors.Location(s.Scope)).ToArray();
        var agents = _rows.Select(s => colors.Agents(InstalledInventoryFormatter.DescribeAgents(s))).ToArray();
        _table.Style.GetOrCreateColumnStyle(2).ColorGetter = args => locations[args.RowIndex];
        _table.Style.GetOrCreateColumnStyle(3).ColorGetter = args => agents[args.RowIndex];
    }

    private void RecomputeColumnWidths()
    {
        var width = ResolveWidthForLayout(
            _table.Viewport.Width,
            _table.Frame.Width,
            _lastWidth > 0 ? _lastWidth : 70);
        _lastWidth = width;

        // Four/five columns consume one separator each (including the final
        // border). Spending the whole viewport prevents a selected blank cell
        // from appearing beyond the last border.
        var available = Math.Max(2, width - (_hasPackages ? 5 : 4) - (_availability is null ? 0 : 1));
        var fixedCols = LocationColumnWidth + StateColumnWidth + AgentsColumnWidth + (_availability is null ? 0 : 11);
        var remaining = Math.Max(2, available - fixedCols);
        if (_hasPackages)
        {
            _nameW = Math.Clamp((int)Math.Round(remaining * 0.55), Math.Min(12, remaining - 1), remaining - 1);
            _pkgW = remaining - _nameW;
        }
        else
        {
            _nameW = remaining;
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
        "Legend: USR user · PRJ project · CUS custom · OK valid · SYM symlink · REV review · K check · R local reimport";

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
        if (_table.Visible && _rows.Count > 0) _table.SetFocus();
        else if (_loading.Visible) _loading.SetFocus();
        else _detail.SetFocus();
    }

    private void RestoreFocus()
    {
        switch (_lastFocusedTarget)
        {
            case FocusTarget.Filter:
                _filterField.SetFocus();
                break;
            default:
                FocusList();
                break;
        }
    }

    private void RefreshAll()
    {
        if (_snapshot is null) return;
        var emptyListHadFocus = _rows.Count == 0 && _detail.HasFocus;
        ApplyFilter();
        RecomputeColumnWidths();
        BuildTableSource();
        if (_rows.Count == 0 && _table.HasFocus) _detail.SetFocus();
        else if (emptyListHadFocus && _rows.Count > 0) _table.SetFocus();
        RefreshOperationStatus();
        RefreshSelectedDetail();
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
        if (_snapshot.IsPartial)
            srcSuffix += $" · partial scan ({_snapshot.Providers.Count(provider => !provider.Succeeded)} provider failure(s), {_snapshot.Diagnostics.InaccessiblePaths.Length} inaccessible path(s))";
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
        if (!_filterField.HasFocus && key.AsRune.Value == 'C' && _onCheckUpdates is not null)
        {
            key.Handled = true;
            _onCheckUpdates();
            return;
        }
        if (!_filterField.HasFocus && (key.AsRune.Value is 'u' or 'U' || key.KeyCode == KeyCode.Enter) && _onReviewUpdate is not null)
        {
            key.Handled = true;
            ReviewSelectedUpdate();
            return;
        }
        if (key.KeyCode == KeyCode.F2)
        {
            key.Handled = true;
            _showDetails = !_showDetails;
            RefreshSelectedDetail();
            return;
        }
        if (!_filterField.HasFocus && key.AsRune.Value == 'K' && _onPackageCheck is not null
            && Volatile.Read(ref _loadActive) == 0 && Volatile.Read(ref _removeActive) == 0)
        {
            key.Handled = true;
            if (GetSelectedSkill() is { } selected) _onPackageCheck(selected);
            return;
        }
        if (!_filterField.HasFocus && key.AsRune.Value == 'R' && _onLocalReimport is not null
            && Volatile.Read(ref _loadActive) == 0 && Volatile.Read(ref _removeActive) == 0)
        {
            key.Handled = true;
            if (GetSelectedSkill() is { } selected) _onLocalReimport(selected);
            return;
        }
        if (!_filterField.HasFocus && key.AsRune.Value == 'T' && _onLocalSourceSwitch is not null
            && Volatile.Read(ref _loadActive) == 0 && Volatile.Read(ref _removeActive) == 0)
        {
            key.Handled = true;
            if (GetSelectedSkill() is { } selected) _onLocalSourceSwitch(selected);
            return;
        }
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
        // Every scope selects reconciled rows from the full shared snapshot.
        if (!_filterField.HasFocus && key.AsRune.Value == 'G')
        {
            _scopeFilter = CycleScope(_scopeFilter);
            RefreshAll();
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
                FocusList();
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
                    if (CanActOnSelection && GetSelectedSkill() is { } selected)
                        _openPath(selected.ResolvedPath);
                    break;
                }
        }
    }

    private sealed class InstalledTableSource : EnumerableTableSource<InstalledSkill>
    {
        public InstalledTableSource(IReadOnlyList<InstalledSkill> rows, Dictionary<string, Func<InstalledSkill, object>> cols)
            : base(rows, cols) { }
    }

    private string RenderDetail(InstalledSkill skill)
    {
        var observation = _availability?.Get(skill);
        return (observation is null ? "" : observation.Detail + "\n**C:** check/cancel updates · **u / Enter:** review this copy\n\n"
            + (_showDetails && observation.Diagnostic.Length > 0 ? "````\n" + (TerminalEscapeSanitizer.Sanitize(observation.Diagnostic) ?? "").Replace("`", "'") + "\n````\n\n" : ""))
            + InstalledScreen.RenderDetail(skill, _showDetails);
    }

    private void ReviewSelectedUpdate()
    {
        if (CanActOnSelection && GetSelectedSkill() is { } selected) _onReviewUpdate?.Invoke(selected);
    }

    internal void RefreshAvailability()
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested || _availability is null) return;
        _checkUpdates.Text = _availability.Running ? "Cancel check (C)" : "Check updates (C)";
        _checkUpdates.Enabled = !_availability.Stopping;
        _updateStatus.Text = _availability.Summary;
        _reviewUpdate.Enabled = CanActOnSelection && GetSelectedSkill() is not null;
        // Keep rows, selection and table offsets while updating live cell getters.
        _table.Update();
        var detailOffset = _detail.Viewport.Location;
        RefreshSelectedDetail();
        TuiHelpers.RestoreMarkdownScroll(_detail, detailOffset);
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
        Exception? failure = null;
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
        catch (Exception ex) { failure = ex; }
        finally
        {
            try
            {
                await _runOnUi(() =>
                {
                    Interlocked.Exchange(ref _removeActive, 0);
                    if (failure is not null && !cancellationToken.IsCancellationRequested)
                    {
                        _idleStatusMessage = " removal failed: " + TuiHelpers.ErrorSnippet(failure.Message);
                        var message = failure.Message;
                        if (message.Length > 4096) message = message[..4096] + " [truncated]";
                        _removalErrorDetail = "## Could not start removal\n\n" + MarkdownTableFormatter.FormatCodeSpan(message)
                            + "\n\nRefresh Installed and select a visible skill before trying again.";
                    }
                    RefreshOperationStatus();
                    if (failure is not null && !cancellationToken.IsCancellationRequested)
                        _onRemovalError?.Invoke(failure);
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

    private bool CanActOnSelection => Volatile.Read(ref _loadActive) == 0 && Volatile.Read(ref _removeActive) == 0;

    private bool TryStartSelectedRemove()
    {
        if (!CanActOnSelection) return false;
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
        _idleStatusMessage = null;
        _removalErrorDetail = null;
        RefreshOperationStatus();
        _runTask(
            () => RunRemoveAsync(skill, snapshot),
            "installed.remove");
        return true;
    }

    private void RefreshOperationStatus()
    {
        var loading = Volatile.Read(ref _loadActive) != 0;
        var removing = Volatile.Read(ref _removeActive) != 0;
        SetContentVisibility(loading, removing);
        if (Volatile.Read(ref _removeActive) != 0)
        {
            SetSpinnerActive(true);
            _footer.Text = " checking removal safety…";
            _detail.Text = "## Checking removal safety…\n\n"
                + "SkillView is validating the selected skill and its related installs. "
                + "You can leave this tab to cancel.";
            return;
        }

        if (Volatile.Read(ref _loadActive) != 0)
        {
            SetSpinnerActive(true);
            _footer.Text = " loading inventory…";
            return;
        }

        SetSpinnerActive(false);
        RefreshSelectedDetail();
        if (_removalErrorDetail is not null) _detail.Text = _removalErrorDetail;
        if (_idleStatusMessage is not null)
        {
            if (_snapshot is null) _detail.Text = "## Could not load installed skills\n\n" + TerminalEscapeSanitizer.Sanitize(_idleStatusMessage) + "\n\nLeave this tab and return to retry.";
            _footer.Text = _idleStatusMessage;
            return;
        }

        RefreshFooter();
        RefreshAvailability();
    }

    private void SetContentVisibility(bool loading, bool removing)
    {
        if (loading && !removing)
        {
            _loading.SetLoading(true);
            if (_lastFocusedTarget == FocusTarget.Table) _loading.SetFocus();
            _table.Visible = false;
            _detail.Visible = false;
        }
        else
        {
            // Make the destination focusable before retiring the indicator;
            // hiding a focused view must not implicitly focus the filter.
            _table.Visible = !loading;
            _detail.Visible = true;
            if (_loading.HasFocus)
            {
                if (loading) _detail.SetFocus();
                else RestoreFocus();
            }
            _loading.SetLoading(false);
        }
    }

    private void RefreshSelectedDetail()
    {
        if (_snapshot is null || Volatile.Read(ref _removeActive) != 0 || Volatile.Read(ref _loadActive) != 0)
        {
            return;
        }

        _detail.Text = _rows.Count == 0
            ? "(no matches)"
            : RenderDetail(_rows[Math.Clamp(_table.GetSelectedRow(), 0, _rows.Count - 1)]);
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

    internal void SetFilterTextForTests(string text) => _filterField.Text = text;

    internal bool RemoveActiveForTests => Volatile.Read(ref _removeActive) != 0;

    internal bool LoadActiveForTests => Volatile.Read(ref _loadActive) != 0;

    internal bool SpinnerVisibleForTests => _spinner.Visible;

    internal string FooterTextForTests => _footer.Text.ToString();

    internal string DetailTextForTests => _detail.Text.ToString();

    internal bool TryStartSelectedRemoveForTests() => TryStartSelectedRemove();
}
