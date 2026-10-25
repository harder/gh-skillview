using System.Collections.ObjectModel;
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
/// Owns source/copy navigation over one frozen projection. Shared ordinary and
/// technical detail rendering and local shortcut policy live in InstalledScreen;
/// copy operations retain their existing exact-target callbacks.
internal sealed class InstalledTabView : FrameView
{
    private enum SortMode { Name, Source, Location }
    private enum FocusTarget { Table, Filter }
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
    private readonly Label _coverage;
    private readonly Label _controls;
    private readonly Button _detailsButton;
    private readonly Button _moreActions;
    private readonly Button _back;
    private readonly Button[] _facetButtons;
    private readonly Button _coverageButton;
    private readonly Button _clearFilters;
    private readonly Func<InstalledIssueEvidence?> _capturedIssues;
    private readonly Func<long> _catalogGeneration;
    private (InstalledIssueEvidence? Evidence, long Generation) _issueView;
    private InstalledFilterSelection _filters = new();
    private readonly string[] _chosenLabels = ["Any", "Any", "Any", "Any"];
    private bool _coverageOpen;
    private InstalledOverview _overview = InstalledOverview.Empty;
    private InstalledOverviewRows _viewRows = new([], 0, 0, 0);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownGroups = new(StringComparer.Ordinal);
    private string? _selectedKey;
    private string? _topRowKey;
    private bool _topRowHidden;
    private int _lastRestoredTop;
    private bool _restoring;
    private bool _detailOpen;
    private bool _wide;
    private bool _layoutActive;
    private bool _reflowActive;
    private int? _descriptionWidth;
    private View? _rememberedFocus;
    private string _selectionReason = "Select an exact copy for actions.";
    private bool _disposed;
    private PopoverMenu? _actionsMenu;

    private readonly TextField _filterField;
    private readonly TableView _table;
    private readonly Markdown _detail;
    private readonly Label _footer;
    private readonly TabLoadingView _loading = new("Loading installed skills…");
    private bool _showDetails;
    private readonly SpinnerView _spinner;
    private InventorySnapshot? _snapshot;
    internal InventorySnapshot? CurrentSnapshot => _snapshot;
    private IReadOnlyList<InstalledOverviewRow> _rows = Array.Empty<InstalledOverviewRow>();
    private SortMode _sort = SortMode.Name;
    private PinFilter _pinFilter = PinFilter.All;
    private ScopeFilter _scopeFilter = ScopeFilter.All;
    private int _lastWidth = -1;
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
        Action? onStateChange = null,
        CancellationToken lifetimeToken = default,
        Action<AggregateException>? onCancellationCallbackException = null,
        Action<InstalledSkill>? onPackageCheck = null,
        Action<InstalledSkill>? onLocalReimport = null, Action<InstalledSkill>? onLocalSourceSwitch = null,
        Action<Exception>? onRemovalError = null, Func<string, bool>? openPath = null,
        UpdateAvailabilitySession? availability = null, Action? onCheckUpdates = null,
        Action<InstalledSkill>? onReviewUpdate = null, Func<InstalledIssueEvidence?>? capturedIssues = null, Func<long>? catalogGeneration = null)
    {
        _capturedIssues = capturedIssues ?? (() => null);
        _catalogGeneration = catalogGeneration ?? (() => 0);
        _runOnUi = runOnUi;
        _runTask = runTask;
        _snapshotLoader = snapshotLoader;
        _onRemove = onRemove;
        _onRemovalError = onRemovalError;
        _openPath = openPath ?? TuiHelpers.OpenInDefaultHandler;
        _onLeaveTab = onLeaveTab;
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
        _checkUpdates = new SingleRowButton { X = Pos.AnchorEnd(35), Y = 0, Text = "Check all updates (C)", Visible = availability is not null };
        _reviewUpdate = new SingleRowButton { X = 13, Y = Pos.AnchorEnd(1), Text = "Review update (u)", Visible = onReviewUpdate is not null };
        _checkUpdates.Accepting += (_, e) => { e.Handled = true; _onCheckUpdates?.Invoke(); };
        _reviewUpdate.Accepting += (_, e) => { e.Handled = true; ReviewSelectedUpdate(); };
        _detailsButton = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "Details" };
        _detailsButton.Accepting += (_, e) => { e.Handled = true; OpenSelectedDetail(); };
        _moreActions = new SingleRowButton { X = 37, Y = Pos.AnchorEnd(1), Text = "More actions" };
        _moreActions.Accepting += (_, e) => { e.Handled = true; ShowMoreActions(); };
        _back = new SingleRowButton { X = 56, Y = Pos.AnchorEnd(1), Text = "Back (Esc)", Visible = false };
        _back.Accepting += (_, e) => { e.Handled = true; BackToList(); };
        _coverage = new Label { X = 14, Y = 2, Width = Dim.Fill(), Height = 1, Text = "Inventory not loaded" };
        _controls = new Label { X = 22, Y = 0, Width = Dim.Fill(35), Height = 1 };
        _clearFilters = new SingleRowButton { X = Pos.AnchorEnd(9), Y = 0, Text = "Clear" };
        _clearFilters.Accepting += (_, e) => { e.Handled = true; ClearFilters(); };
        _coverageButton = new SingleRowButton { X = 0, Y = 2, Text = "Coverage" };
        _coverageButton.Accepting += (_, e) => { e.Handled = true; ToggleCoverage(); };
        _facetButtons = Enumerable.Range(0, 4).Select(i =>
        {
            var button = new SingleRowButton { Y = 1, HotKeySpecifier = (System.Text.Rune)0xffff };
            button.Accepting += (_, e) => { e.Handled = true; ShowFilterChoices(i); };
            return (Button)button;
        }).ToArray();

        var filterLabel = new Label { Text = "Search:", X = 0, Y = 0 };
        _filterField = new TextField
        {
            X = 8,
            Y = 0,
            Width = 13,
            Text = string.Empty,
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
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(_table);
        TuiHelpers.ConfigureTableNavigation(_table);
        TuiHelpers.ConfigureTableChrome(_table);
        _table.Style.ShowVerticalCellLineForLastColumn = true;
        _table.Style.ShowHorizontalHeaderOverline = false;
        _table.Style.ShowHorizontalHeaderUnderline = false;
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
            Height = Dim.Fill(2),
            Text = "(no skills loaded)",
        };
        TuiHelpers.ConfigureMarkdownPane(_detail, SchemeNames.Base);

        _footer = new Label
        {
            X = 2,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(2),
            Text = " loading inventory…",
        };
        _spinner = new SpinnerView
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
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
            if (_restoring) return;
            var row = _table.GetSelectedRow();
            if (row >= 0 && row < _rows.Count)
            {
                _selectedKey = _rows[row].Key;
                RefreshSelectedDetail();
                _onStateChange?.Invoke();
            }
        };
        _table.KeyDown += (_, key) =>
        {
            if (key.Handled) return;
            if (key.KeyCode == KeyCode.Enter) { key.Handled = true; BrowseSelected(); }
            else if (key.KeyCode is KeyCode.CursorLeft or KeyCode.CursorRight)
            { key.Handled = true; ExpandSelected(key.KeyCode == KeyCode.CursorRight); }
        };
        FrameChanged += (_, _) => ApplyResponsiveLayout();
        _table.ViewportChanged += (_, _) => RecomputeColumnWidths();
        _detail.ViewportChanged += (_, _) => ReflowDetail();

        KeyDown += OnKeyDown;
        _detail.KeyDown += OnKeyDown;
        _table.KeyDown += OnKeyDown;
        _filterField.KeyDown += OnKeyDown;

        TuiHelpers.ApplyScheme(SchemeNames.Base,
            this, filterLabel, _filterField, _table, _detail, _footer, _spinner);

        Add(filterLabel, _filterField, _table, _detail, _spinner, _footer,
            _detailsButton, _reviewUpdate, _moreActions, _checkUpdates, _back, _coverage, _controls, _coverageButton, _clearFilters);
        Add(_facetButtons);
        _detail.TabStop = TabBehavior.NoStop;
        foreach (var control in new View[] { _filterField, _facetButtons[0], _facetButtons[1], _facetButtons[2], _facetButtons[3], _clearFilters, _coverageButton, _table, _detail, _detailsButton, _reviewUpdate, _moreActions, _checkUpdates, _back })
            control.HasFocusChanged += (_, _) => { if (control.HasFocus) _rememberedFocus = control; };
        Disposing += (_, _) => { _disposed = true; _actionsMenu?.Dispose(); CancelPendingWork(); };
    }

    /// Kick off a snapshot load and reveal the tab. Safe to call repeatedly —
    /// each call refreshes the inventory.
    internal async Task LoadAsync()
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested) return;
        var priorSnapshot = _snapshot;
        var priorOverview = _overview;
        var loadGeneration = Interlocked.Increment(ref _loadGeneration);
        using var loadCancellation = _loadCancellations.Replace(_lifetimeToken);
        Visible = true;
        Interlocked.Exchange(ref _loadActive, 1);
        _idleStatusMessage = null;
        RefreshOperationStatus();
        try
        {
            var snapshot = await _snapshotLoader(loadCancellation.Token).ConfigureAwait(false);
            var overview = ReferenceEquals(priorSnapshot, snapshot) ? priorOverview : InstalledOverview.Build(snapshot, loadCancellation.Token);
            await _runOnUi(() =>
            {
                if (_disposed || _lifetimeToken.IsCancellationRequested || loadCancellation.Token.IsCancellationRequested || !IsCurrentLoad(loadGeneration))
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
                else Populate(snapshot, overview);
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
    internal void LoadSeeded(InventorySnapshot snapshot, InstalledOverview? overview = null)
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested) return;
        CancelPendingWork();
        Interlocked.Increment(ref _loadGeneration);
        Visible = true;
        Populate(snapshot, overview ?? InstalledOverview.Build(snapshot, _lifetimeToken));
    }

    internal bool RevealInstall(InstalledInstallHandoff handoff)
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested) return false;
        var group = handoff.Overview.Groups.SingleOrDefault(group => group.Key == handoff.GroupKey);
        var copies = group?.Copies.Where(copy => handoff.CopyKeys.Contains(copy.Key, StringComparer.Ordinal)).ToArray();
        if (group is null || copies is null || copies.Length != handoff.CopyKeys.Count || copies.Length == 0
            || copies.Any(copy => copy.Ambiguous)) return false;
        LoadSeeded(handoff.Snapshot, handoff.Overview);
        // Evaluate every verified copy against the full fresh projection first.
        // Clearing is the same single state transition as ordinary Clear filters.
        var adjusted = copies.Any(copy => !Matches(copy.Target!));
        if (adjusted) ClearFilters();
        _coverageOpen = false;
        _detailOpen = copies.Length == 1 && !handoff.BrowseOnly;
        _expanded.Add(group.Key);
        _selectedKey = _detailOpen ? copies[0].Key : group.Key;
        _topRowKey = _selectedKey;
        _topRowHidden = false;
        RefreshAll();
        // Bounded row projections may omit a late group. Never select a peer.
        if (FindRow(_selectedKey) < 0) { _detailOpen = false; RefreshSelectedDetail(); return false; }
        _table.RowOffset = FindRow(_selectedKey);
        if (handoff.BrowseOnly) _idleStatusMessage = (adjusted ? "Adjusted Installed filters to show this result. " : "")
                + "Showing verified copies of this skill; choose a copy to review it.";
        else if (adjusted) _idleStatusMessage = "Adjusted Installed filters to show this result.";
        RefreshOperationStatus();
        if (_detailOpen) OpenSelectedDetail(); else FocusList();
        return true;
    }

    private void Populate(InventorySnapshot snapshot, InstalledOverview overview)
    {
        _snapshot = snapshot;
        _overview = overview;
        var keys = overview.Groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        _knownGroups.IntersectWith(keys); _expanded.IntersectWith(keys);
        foreach (var group in overview.Groups)
            if (_knownGroups.Add(group.Key)) _expanded.Add(group.Key);
        _idleStatusMessage = null;
        _removalErrorDetail = null;
        RefreshAll();
        ScheduleDeferredWidthStabilization();
        RestoreFocus();
    }

    private bool Matches(InstalledSkill s)
    {
        var q = _filterField.Text.Trim();
        var cmp = StringComparison.OrdinalIgnoreCase;
        return (q.Length == 0 || s.Name.Contains(q, cmp) || s.ResolvedPath.Contains(q, cmp)
            || s.Agents.Any(a => a.AgentId.Contains(q, cmp) || a.Path.Contains(q, cmp))
            || s.AllOwnershipClaims.Any(o => o.Source.Contains(q, cmp) || (o.SourceUrl?.Contains(q, cmp) ?? false))
            || (s.FrontMatter.Upstream?.Contains(q, cmp) ?? false) || (s.FrontMatter.LocalPath?.Contains(q, cmp) ?? false))
            && (_overview.Filters?.Matches(s, _filters, ToScope(_scopeFilter), _issueView.Evidence, _issueView.Generation) ?? false)
            && (_pinFilter == PinFilter.All || (_pinFilter == PinFilter.PinnedOnly ? PinDisplayPolicy.From(s).HasPin
                : PinDisplayPolicy.From(s).State == PinDisplayState.Unpinned));
    }

    private void ApplyFilter()
    {
        // Each table row occupies one terminal line, so its within-row offset is
        // zero. Keep a hidden anchor through collapse/filter until it reappears;
        // an explicit scroll to another offset establishes a new anchor.
        if (!_topRowHidden || _table.RowOffset != _lastRestoredTop)
            _topRowKey = _table.RowOffset >= 0 && _table.RowOffset < _rows.Count ? _rows[_table.RowOffset].Key : _topRowKey;
        _viewRows = _overview.View(Matches, _sort.ToString().ToLowerInvariant(), _expanded,
            _availability is null ? null : _availability.Get, _lifetimeToken);
        _rows = _viewRows.Rows;
        _selectedKey ??= _rows.FirstOrDefault()?.Key;
        BuildTableSource();
        _restoring = true;
        try
        {
            var selected = FindRow(_selectedKey);
            if (selected < 0)
            {
                var parent = _overview.Groups.FirstOrDefault(g => g.Copies.Any(c => c.Key == _selectedKey));
                selected = FindRow(parent?.Key);
            }
            _table.SetSelectedRow(Math.Max(0, selected));
            var topIndex = FindRow(_topRowKey);
            _topRowHidden = _topRowKey is not null && topIndex < 0;
            if (topIndex < 0)
            {
                var parent = _overview.Groups.FirstOrDefault(g => g.Copies.Any(c => c.Key == _topRowKey));
                topIndex = FindRow(parent?.Key);
            }
            if (topIndex >= 0) _table.RowOffset = topIndex;
            _lastRestoredTop = _table.RowOffset;
        }
        finally { _restoring = false; }
    }

    private int FindRow(string? key)
    {
        for (var i = 0; i < _rows.Count; i++) if (_rows[i].Key == key) return i;
        return -1;
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

    private void BuildTableSource()
    {
        _restoring = true;
        try
        {
            _table.Table = new EnumerableTableSource<InstalledOverviewRow>(_rows, new Dictionary<string, Func<InstalledOverviewRow, object>>
            {
                ["Skill / source / copy"] = r => r.Label,
                ["Copies / consumers / updates"] = r => r.Summary,
            });
            _table.Style.ExpandLastColumn = false;
            RecomputeColumnWidths();
            _table.Update();
        }
        finally { _restoring = false; }
    }

    private void RecomputeColumnWidths()
    {
        var width = _table.Viewport.Width > 0 ? _table.Viewport.Width : Math.Max(30, _lastWidth);
        _lastWidth = width;
        var available = Math.Max(2, width - 2);
        var first = Math.Max(1, available * 55 / 100);
        for (var i = 0; i < 2; i++)
        {
            var style = _table.Style.GetOrCreateColumnStyle(i);
            style.MinWidth = style.MaxWidth = i == 0 ? first : available - first;
        }
        _table.SetNeedsDraw();
    }

    private void ReflowDetail()
    {
        if (_reflowActive || _disposed || _lifetimeToken.IsCancellationRequested) return;
        // Viewport already excludes the actual scrollbar. The code-block copy
        // glyph occupies the last two columns, including its click target.
        var width = _detail.Viewport.Width - (_detail.ShowCopyButtons ? 2 : 0);
        if (width <= 0 || width == _descriptionWidth) return;
        _descriptionWidth = width;
        _reflowActive = true;
        try
        {
            var offset = _detail.Viewport.Location;
            RefreshSelectedDetail();
            TuiHelpers.RestoreMarkdownScroll(_detail, offset);
            var size = _detail.GetContentSize();
            _detail.Viewport = _detail.Viewport with
            {
                X = Math.Clamp(offset.X, 0, Math.Max(0, size.Width - _detail.Viewport.Width)),
                Y = Math.Clamp(offset.Y, 0, Math.Max(0, size.Height - _detail.Viewport.Height))
            };
        }
        finally { _reflowActive = false; }
    }

    private void ApplyResponsiveLayout()
    {
        if (_layoutActive || _facetButtons is null || _detail is null) return;
        _layoutActive = true;
        try
        {
            var tableFocused = _table.HasFocus;
            var detailFocused = _detail.HasFocus;
            _wide = Frame.Width >= 110;
            var detail = _detailOpen && !_wide;
            var showTable = !detail && _rows.Count > 0;
            var showDetail = _wide || detail || _rows.Count == 0;
            _table.Width = _wide ? Dim.Percent(55) : Dim.Fill();
            _detail.X = _wide ? Pos.Right(_table) + 1 : 0;
            _detail.Y = 3;
            LayoutFilters();
            _detail.Width = Dim.Fill();
            if (showTable) _table.Visible = true;
            if (showDetail) _detail.Visible = true;
            if (tableFocused && !showTable) _detail.SetFocus();
            else if (detailFocused && !showDetail) _table.SetFocus();
            _table.Visible = showTable;
            _detail.Visible = showDetail;
            _back.Visible = _detailOpen;
            RecomputeColumnWidths();
        }
        finally { _layoutActive = false; }
    }

    internal static int ResolveWidthForLayoutForTests(int viewportWidth, int frameWidth, int fallbackWidth) =>
        viewportWidth > 0 ? viewportWidth : fallbackWidth;

    internal static string BuildSearchScopeText() => "name · path · agent · source";

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
                if (_disposed || _lifetimeToken.IsCancellationRequested || !Visible || _table.Table is null)
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
        if (_detailOpen && !_wide) _detail.SetFocus();
        else if (_table.Visible && _rows.Count > 0) _table.SetFocus();
        else if (_loading.Visible) _loading.SetFocus();
        else _detail.SetFocus();
    }

    private void RestoreFocus()
    {
        if (_rememberedFocus is { Visible: true, Enabled: true, CanFocus: true } remembered)
        { remembered.SetFocus(); return; }
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
        if (_disposed || _lifetimeToken.IsCancellationRequested || _snapshot is null) return;
        CaptureIssueView();
        var detailOffset = _detail.Viewport.Location;
        var emptyListHadFocus = _rows.Count == 0 && _detail.HasFocus;
        ApplyFilter();
        ApplyResponsiveLayout();
        if (_rows.Count == 0 && _table.HasFocus) _detail.SetFocus();
        else if (emptyListHadFocus && _rows.Count > 0) _table.SetFocus();
        RefreshOperationStatus(captureIssues: false);
        RefreshSelectedDetail();
        TuiHelpers.RestoreMarkdownScroll(_detail, detailOffset);
        _onStateChange?.Invoke();
    }

    private void RefreshFooter()
    {
        if (_snapshot is null)
        {
            _footer.Text = " (inventory not loaded)";
            return;
        }
        var counts = _viewRows.MatchingCopies == _overview.Copies ? $"{_overview.Copies} observed {(_overview.Copies == 1 ? "copy" : "copies")}"
            : $"{_viewRows.MatchingCopies} of {_overview.Copies} observed copies";
        _footer.Text = $" {counts} · {InstalledScreen.PlacementCount(_overview.Placements)}"
            + (_overview.AmbiguousCopies > 0 ? $" · {_overview.AmbiguousCopies} ambiguous" : "")
            + (_viewRows.OmittedRows > 0 ? $" · {_viewRows.ShownCopies} shown; {_viewRows.OmittedRows} rows omitted" : "")
            + (_snapshot.IsPartial || !_snapshot.ConfiguredRootsComplete ? " · Partial scan" : "");
        _controls.Text = $"P:{(_pinFilter == PinFilter.All ? "any" : _pinFilter == PinFilter.PinnedOnly ? "pin" : "no")} G:{(_scopeFilter == ScopeFilter.All ? "any" : _scopeFilter == ScopeFilter.Project ? "proj" : _scopeFilter == ScopeFilter.Custom ? "dir" : "user")} s:{_sort.ToString()[0]}";
        RefreshFilterButtons();
        RefreshCoverage();
    }

    private static readonly string[] FilterTitles = ["Agent", "Project/location", "Source/owner", "Issue"];
    private string? FilterKey(int facet) => facet switch { 0 => _filters.Agent, 1 => _filters.Location, 2 => _filters.Source, _ => _filters.Issue };
    internal IReadOnlyList<InstalledFilterChoice> FilterChoices(int facet) => facet switch
    {
        0 => _overview.Filters?.Agents ?? [],
        1 => _overview.Filters?.Locations ?? [],
        2 => _overview.Filters?.Sources ?? [],
        _ => _overview.Filters?.IssueChoices(_issueView.Evidence, _issueView.Generation) ?? [],
    };

    internal void SelectFilter(int facet, string? key)
    {
        var choice = FilterChoices(facet).FirstOrDefault(c => c.Key == key);
        if (key is not null && choice is null) return;
        _chosenLabels[facet] = choice?.Label ?? "Any";
        _filters = facet switch
        {
            0 => _filters with { Agent = key },
            1 => _filters with { Location = key },
            2 => _filters with { Source = key },
            _ => _filters with { Issue = key }
        };
        RefreshAll();
    }

    private void ClearFilters()
    {
        _filters = new();
        Array.Fill(_chosenLabels, "Any");
        _scopeFilter = ScopeFilter.All; _pinFilter = PinFilter.All;
        _filterField.Text = "";
        RefreshAll();
    }

    private void LayoutFilters()
    {
        var width = Math.Max(60, Frame.Width);
        var extra = Math.Max(0, width - 78) / 4;
        var widths = new[] { 16 + extra, 25 + extra, 23 + extra, Math.Max(12, width - 64 - extra * 3) };
        var x = 0;
        for (var i = 0; i < 4; i++)
        { _facetButtons[i].X = x; _facetButtons[i].Width = widths[i]; x += widths[i]; }
        RefreshFilterButtons();
    }

    private void RefreshFilterButtons()
    {
        for (var i = 0; i < 4; i++)
        {
            var key = FilterKey(i);
            var choices = FilterChoices(i);
            var current = choices.FirstOrDefault(c => c.Key == key);
            var unavailable = key is not null && current is null;
            if (current is not null) _chosenLabels[i] = current.Label;
            var value = unavailable ? "!missing" : _chosenLabels[i];
            if (i == 3 && key is null && _snapshot is not null && _issueView.Evidence?.IsCurrent(_snapshot, _issueView.Generation) != true)
                value = "Any?";
            // The selection's full, sanitized label stays in the control popup
            // and detail. Compact labels are presentation only, never keys.
            var room = Math.Max(2, _facetButtons[i].Frame.Width - FilterTitles[i].Length - 4);
            if (!unavailable && key is not null)
            {
                string Compact(InstalledFilterChoice c)
                {
                    var label = c.Label;
                    if (i == 0) label = label switch { "claude-code" => "Claude", "github-copilot" => "Copilot", "gemini-cli" => "Gemini", _ => label };
                    if (i == 3 && c.Key == "unknown") label = "?";
                    if (i == 3 && c.Key == "any-finding") label = "Found";
                    if (i == 3 && c.Key == "none-observed") label = "None";
                    if (i == 1) label = label.Split('/').Last();
                    if (i == 2)
                    {
                        label = c.Key switch
                        {
                            "unknown-source" => "Unknown",
                            "conflicting-source" => "Conflict",
                            "managed-owner" => "Managed",
                            _ when c.Key.StartsWith("source:", StringComparison.Ordinal) => label.Replace("Source: ", "", StringComparison.Ordinal).Split(" / ")[0],
                            _ => label.Replace("Owner: ", "", StringComparison.Ordinal).Split(" / ").Last().Split(" [")[0],
                        };
                    }
                    return InstalledFilters.ChoiceText(label, room);
                }
                var choice = choices.First(c => c.Key == key);
                value = Compact(choice);
                var collisions = choices.Where(c => Compact(c) == value).OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
                if (collisions.Length > 1)
                {
                    var suffix = "#" + (Array.IndexOf(collisions, choice) + 1);
                    value = InstalledFilters.ChoiceText(value, Math.Max(1, room - suffix.Length)) + suffix;
                }
            }
            _facetButtons[i].Text = FilterTitles[i] + ":" + InstalledFilters.ChoiceText(value, room);
        }
    }

    private string FilterDescription()
    {
        var text = string.Join(" · ", Enumerable.Range(0, 4).Select(i => FilterTitles[i] + ": "
            + MarkdownTableFormatter.FormatCodeSpan(_chosenLabels[i])
            + (FilterKey(i) is { } key && !FilterChoices(i).Any(c => c.Key == key) ? " (unavailable; 0 matches)" : "")));
        if (_snapshot is not null && _issueView.Evidence?.IsCurrent(_snapshot, _issueView.Generation) != true)
            text += "\n\nIssues not loaded for this capture. Visit Issues to load local findings; no checks run here.";
        else if (_issueView.Evidence?.Partial == true)
            text += "\n\nIssues capture is incomplete; missing findings remain unknown.";
        return text + $"\n\nPins: {DescribePin(_pinFilter)} · Scope: {DescribeScope(_scopeFilter)} · Sort: {_sort.ToString().ToLowerInvariant()}" + "\n\nCheck updates (C) checks the whole captured catalog, including copies outside these filters; it never prepares a plan.";
    }

    private void ShowFilterChoices(int facet)
    {
        if (App is null || _snapshot is null) return;
        var choices = FilterChoices(facet);
        var key = FilterKey(facet);
        var entries = new List<InstalledFilterChoice> { new("", "Any") };
        if (key is not null && !choices.Any(c => c.Key == key)) entries.Add(new(key, _chosenLabels[facet] + " (unavailable; 0 matches)"));
        entries.AddRange(choices);
        using var dialog = new Dialog { Title = FilterTitles[facet] + " — captured choices", Width = Dim.Percent(95), Height = 12, SchemeName = SchemeNames.Dialog };
        var hint = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = 2,
            Text = facet == 3 && _issueView.Evidence?.IsCurrent(_snapshot, _issueView.Generation) != true
                ? "Issues not loaded. Visit Issues first. Unknown is not clean."
                : "Choose directly with arrows/Enter; Esc cancels. No scan runs."
        };
        var list = new ListView { X = 1, Y = 2, Width = Dim.Fill(2), Height = Dim.Fill(1) };
        list.SetSource(new ObservableCollection<string>(entries.Select(c => (c.Key == (key ?? "") ? "* " : "  ") + c.Label)));
        list.SelectedItem = Math.Max(0, entries.FindIndex(c => c.Key == (key ?? "")));
        list.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (list.SelectedItem is not { } index || index < 0 || index >= entries.Count) return;
            var selected = entries[index];
            // An unavailable selection stays selected; choosing Any explicitly
            // clears it. Labels and row positions never route copy operations.
            if (selected.Key == "" || choices.Any(c => c.Key == selected.Key)) SelectFilter(facet, selected.Key == "" ? null : selected.Key);
            App.RequestStop();
        };
        dialog.Add(hint, list);
        list.SetFocus();
        App.Run(dialog);
        RefreshAll();
        if (!_disposed && !_lifetimeToken.IsCancellationRequested) _facetButtons[facet].SetFocus();
    }

    internal void ToggleCoverage()
    {
        _coverageOpen = !_coverageOpen;
        if (_coverageOpen) { _detailOpen = true; ApplyResponsiveLayout(); RefreshSelectedDetail(); _detail.SetFocus(); }
        else BackToList();
    }

    internal bool HandleLocalNavigation(Key key)
    {
        if ((key.KeyCode & ~KeyCode.ShiftMask) == KeyCode.Tab)
        {
            var order = new View[] { _filterField, _facetButtons[0], _facetButtons[1], _facetButtons[2], _facetButtons[3], _clearFilters, _coverageButton, _table, _detailsButton, _reviewUpdate, _moreActions, _checkUpdates, _back }
                .Where(v => v.Visible && v.Enabled && v.CanFocus).ToArray();
            if (order.Length == 0) return false;
            var index = Array.FindIndex(order, v => v.HasFocus);
            var backwards = (key.KeyCode & KeyCode.ShiftMask) != 0;
            order[(index + (backwards ? order.Length - 1 : 1)) % order.Length].SetFocus();
            return true;
        }
        if (_filterField.HasFocus)
        {
            if (key.KeyCode != KeyCode.Esc) return false;
            FocusList(); return true;
        }
        if (key.AsRune.Value == '/') { _filterField.SetFocus(); _filterField.SelectAll(); return true; }
        if ((_table.HasFocus || _detail.HasFocus) && key.KeyCode is KeyCode.CursorLeft or KeyCode.CursorRight)
        { ExpandSelected(key.KeyCode == KeyCode.CursorRight); return true; }
        if (key.KeyCode == KeyCode.Esc && _detailOpen) { BackToList(); return true; }
        return false;
    }

    private void RefreshCoverage()
    {
        var blocked = SelectedRow is null && _selectedKey is not null ? "Selection missing/hidden/filtered; actions disabled · "
            : SelectedRow?.Copy?.Ambiguous == true ? "Ambiguous installation ID; actions disabled · " : "";
        _coverage.Text = blocked + (_snapshot is not null && _issueView.Evidence?.IsCurrent(_snapshot, _issueView.Generation) != true ? "Issues not loaded · " : "") + _overview.Coverage + (_availability is null ? "" : " · " + _availability.Summary);
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (key.Handled) return;
        if (HandleLocalNavigation(key)) { key.Handled = true; return; }
        if (!_filterField.HasFocus && key.AsRune.Value == 'C' && _onCheckUpdates is not null)
        {
            key.Handled = true;
            _onCheckUpdates();
            return;
        }
        if (!_filterField.HasFocus && key.AsRune.Value is 'u' or 'U' && _onReviewUpdate is not null)
        {
            key.Handled = true;
            ReviewSelectedUpdate();
            return;
        }
        if (!_filterField.HasFocus && key.KeyCode == KeyCode.Enter && !_detailsButton.HasFocus && !_reviewUpdate.HasFocus
            && !_moreActions.HasFocus && !_checkUpdates.HasFocus && !_back.HasFocus
            && !_facetButtons.Any(b => b.HasFocus) && !_coverageButton.HasFocus && !_clearFilters.HasFocus)
        { key.Handled = true; BrowseSelected(); return; }
        if ((_table.HasFocus || _detail.HasFocus) && key.KeyCode is KeyCode.CursorLeft or KeyCode.CursorRight)
        { key.Handled = true; ExpandSelected(key.KeyCode == KeyCode.CursorRight); return; }
        if (!_filterField.HasFocus && key.AsRune.Value == '/')
        { key.Handled = true; _filterField.SetFocus(); _filterField.SelectAll(); return; }
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
                if (_detailOpen) BackToList(); else _onLeaveTab();
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
                    SortMode.Name => SortMode.Source,
                    SortMode.Source => SortMode.Location,
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

    private string RenderDetail(InstalledSkill skill, int? recordedPlacementCount)
    {
        var observation = _availability?.Get(skill);
        return InstalledScreen.RenderDetail(skill, _showDetails, showShortcuts: false, descriptionWidth: _descriptionWidth, recordedPlacementCount: recordedPlacementCount)
            + (observation is null ? "" : "\n" + observation.Detail
                + (_showDetails && observation.Diagnostic.Length > 0 ? "\n````\n" + (TerminalEscapeSanitizer.Sanitize(observation.Diagnostic) ?? "").Replace("`", "'") + "\n````\n" : ""))
            + "\n" + _overview.Coverage + "\n\n**F2** Technical details · **u** Review update · **c** Cleanup\n";
    }

    private void BrowseSelected()
    {
        if (SelectedRow is null && _table.GetSelectedRow() is var index && index >= 0 && index < _rows.Count)
            _selectedKey = _rows[index].Key; // Enter is an explicit browsing selection, never an operation fallback.
        var row = SelectedRow;
        if (row?.Copy is not null) OpenSelectedDetail();
        else if (row?.Group is { } group) { if (!_expanded.Remove(group.Key)) _expanded.Add(group.Key); RefreshAll(); }
    }

    private void OpenSelectedDetail()
    {
        if (SelectedRow?.Group is null) return;
        _coverageOpen = false;
        _detailOpen = true;
        ApplyResponsiveLayout();
        RefreshSelectedDetail();
        _detail.SetFocus();
    }

    private void ExpandSelected(bool expand)
    {
        var group = SelectedRow?.Group ?? _overview.Groups.FirstOrDefault(g => g.Copies.Any(c => c.Key == _selectedKey));
        if (group is null) return;
        if (expand) _expanded.Add(group.Key); else _expanded.Remove(group.Key);
        RefreshAll();
    }

    private void BackToList()
    {
        _coverageOpen = false;
        _detailOpen = false;
        ApplyResponsiveLayout();
        FocusList();
    }

    private void ShowMoreActions()
    {
        if (GetSelectedSkill() is not { } skill || !CanActOnSelection) return;
        var actions = new List<(string Label, Action Run)> { ("Open exact location", () => _openPath(skill.ResolvedPath)), ("Remove exact copy", () => TryStartSelectedRemove()) };
        if (_onPackageCheck is not null) actions.Add(("Check package", () => _onPackageCheck(skill)));
        if (skill.FrontMatter.LocalPath is not null)
        {
            if (_onLocalReimport is not null) actions.Add(("Review local reimport", () => _onLocalReimport(skill)));
            if (_onLocalSourceSwitch is not null) actions.Add(("Review different local source", () => _onLocalSourceSwitch(skill)));
        }
        _actionsMenu?.Dispose();
        var key = _selectedKey;
        _actionsMenu = new PopoverMenu(actions.Select(a => new MenuItem
        {
            Title = a.Label,
            Action = () =>
        {
            if (CanActOnSelection && _selectedKey == key && GetSelectedSkill() is { } current
                && current.ResolvedPath == skill.ResolvedPath) a.Run();
        }
        }).ToArray())
        { App = App };
        _actionsMenu.MakeVisible();
    }

    internal void ReviewSelectedUpdate()
    {
        if (CanActOnSelection && GetSelectedSkill() is { } selected) _onReviewUpdate?.Invoke(selected);
    }

    private void CaptureIssueView() => _issueView = (_capturedIssues(), _catalogGeneration());

    internal void RefreshAvailability() => RefreshAvailability(captureIssues: true);

    private void RefreshAvailability(bool captureIssues)
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested || _availability is null) return;
        if (captureIssues) CaptureIssueView();
        _checkUpdates.Text = _availability.Running ? "Cancel check (C)" : "Check all updates (C)";
        _checkUpdates.Enabled = !_availability.Stopping;
        RefreshCoverage();
        _reviewUpdate.Enabled = CanActOnSelection && GetSelectedSkill() is not null;
        ApplyFilter();
        ApplyResponsiveLayout();
        RefreshFooter();
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

    private bool CanActOnSelection => !_disposed && !_lifetimeToken.IsCancellationRequested
        && Volatile.Read(ref _loadActive) == 0 && Volatile.Read(ref _removeActive) == 0;

    private bool TryStartSelectedRemove()
    {
        if (!CanActOnSelection) return false;
        var skill = GetSelectedSkill();
        if (skill is null
            || _snapshot is null
            || Interlocked.CompareExchange(ref _removeActive, 1, 0) != 0)
        {
            return false;
        }

        var snapshot = _snapshot;
        _idleStatusMessage = null;
        _removalErrorDetail = null;
        RefreshOperationStatus();
        _runTask(
            () => RunRemoveAsync(skill, snapshot),
            "installed.remove");
        return true;
    }

    private void RefreshOperationStatus(bool captureIssues = true)
    {
        if (captureIssues) CaptureIssueView();
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
            _footer.Text = _snapshot is null ? " loading inventory…" : " Refreshing inventory… copy actions paused";
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
        RefreshAvailability(captureIssues: false);
    }

    private void SetContentVisibility(bool loading, bool removing)
    {
        var remembered = _rememberedFocus;
        if ((loading || removing) && (_reviewUpdate.HasFocus || _moreActions.HasFocus)) FocusList();
        // Keep old rows visible while refreshing, but gate every copy action.
        if (loading && _snapshot is null && !removing)
        {
            _loading.SetLoading(true);
            if (_lastFocusedTarget == FocusTarget.Table) _loading.SetFocus();
            _table.Visible = false; _detail.Visible = false;
        }
        else
        {
            ApplyResponsiveLayout();
            if (_loading.HasFocus) RestoreFocus();
            _loading.SetLoading(false);
        }
        _reviewUpdate.Enabled = CanActOnSelection && GetSelectedSkill() is not null;
        _moreActions.Enabled = CanActOnSelection && GetSelectedSkill() is not null;
        if (loading || removing) _rememberedFocus = remembered;
    }

    private InstalledOverviewRow? SelectedRow => FindRow(_selectedKey) is var index && index >= 0 ? _rows[index] : null;

    private void RefreshSelectedDetail()
    {
        if (_snapshot is null || Volatile.Read(ref _removeActive) != 0 || Volatile.Read(ref _loadActive) != 0) return;
        var row = SelectedRow;
        _selectionReason = row is null ? "Selected copy is missing, filtered out or hidden. Select a visible exact copy to enable actions."
            : row.Copy is null ? "Select an exact copy; source groups cannot run copy actions."
            : row.Copy.Ambiguous ? "Ambiguous installation ID; all records retained, copy actions disabled." : "";
        _detailsButton.Enabled = row?.Group is not null;
        _detailsButton.Text = "Details";
        _reviewUpdate.Enabled = CanActOnSelection && GetSelectedSkill() is not null;
        _moreActions.Enabled = CanActOnSelection && GetSelectedSkill() is not null;
        if (_coverageOpen)
        {
            _detail.Text = (_overview.Filters?.CoverageDetail ?? "Inventory not loaded") + "\n\n" + FilterDescription();
            _detailsButton.Enabled = _reviewUpdate.Enabled = _moreActions.Enabled = false;
        }
        else if (row?.Copy is { } copy)
        {
            int? placementCount = copy.Records.All(s => string.Equals(s.ResolvedPath, copy.Path, StringComparison.Ordinal))
                ? copy.Records.SelectMany(s => s.Agents).Distinct().Count() : null;
            _detail.Text = (copy.Ambiguous ? "## Ambiguous copy\n\n" + _selectionReason + "\n\n" : "")
                + string.Join("\n\n---\n\n", copy.Records.Select(skill => RenderDetail(skill, placementCount))) + "\n\n" + FilterDescription();
        }
        else if (row?.Group is { } group)
            _detail.Text = "## " + MarkdownTableFormatter.FormatTableCell(group.Name) + "\n\nSource: " + MarkdownTableFormatter.FormatCodeSpan(group.Source)
                + "\n\n" + row.Summary + "\n\n" + _selectionReason + "\n\nEnter or Left/Right expands/collapses the group.\n\n" + _overview.Coverage;
        else _detail.Text = _rows.Count == 0 ? "(no matches)\n\n" + _selectionReason : _selectionReason;
        if (!_coverageOpen && row?.Copy is null) _detail.Text += "\n\n" + FilterDescription();
        RefreshCoverage();
    }

    private void SetSpinnerActive(bool active)
    {
        _spinner.Visible = active;
        _spinner.AutoSpin = active;
    }

    internal string GetFilterText() => _filterField.Text.Trim();

    internal InstalledSkill? GetSelectedSkill() => CanActOnSelection && !_coverageOpen ? SelectedRow?.Copy?.Target : null;

    internal IReadOnlyList<string> VisibleSkillNamesForTests => _rows.Where(r => r.Copy is not null).Select(r => r.Copy!.Records[0].Name).ToArray();
    internal IReadOnlyList<InstalledOverviewRow> RowsForTests => _rows;
    internal string CoverageForTests => _overview.Coverage;
    internal bool DetailOpenForTests => _detailOpen;
    internal string? SelectedKeyForTests => _selectedKey;
    internal void SetSelectedRowForTests(int row)
    {
        _table.SetSelectedRow(row);
        if (row >= 0 && row < _rows.Count) _selectedKey = _rows[row].Key;
        RefreshSelectedDetail();
    }

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
