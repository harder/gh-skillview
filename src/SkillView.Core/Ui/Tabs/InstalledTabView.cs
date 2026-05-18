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
    private enum SortMode { Name, Package, Scope }

    /// winget-tui-style pin filter cycle for the Installed table.
    /// Maps to: All rows · only pinned · only unpinned.
    internal enum PinFilter { All, PinnedOnly, UnpinnedOnly }

    private readonly Func<Action, Task> _runOnUi;
    private readonly Func<Task<InventorySnapshot>> _snapshotLoader;
    private readonly Action<InstalledSkill, InventorySnapshot> _onRemove;
    private readonly Action _onLeaveTab;
    private readonly Action _onGoToSearch;

    private readonly TextField _filterField;
    private readonly TableView _table;
    private readonly Markdown _detail;
    private readonly Label _footer;
    private readonly StatusBar _statusBar;

    private InventorySnapshot? _snapshot;
    private IReadOnlyList<InstalledSkill> _rows = Array.Empty<InstalledSkill>();
    private IReadOnlyList<InstalledSkill> _all = Array.Empty<InstalledSkill>();
    private SortMode _sort = SortMode.Name;
    private PinFilter _pinFilter = PinFilter.All;
    private bool _hasPackages;
    private int _packageCount;
    private int _lastWidth = -1;
    private int _nameW = 12;
    private int _pkgW = 18;
    private int _agentsW = 8;
    private long _loadGeneration;

    internal InstalledTabView(
        Func<Action, Task> runOnUi,
        Func<Task<InventorySnapshot>> snapshotLoader,
        Action<InstalledSkill, InventorySnapshot> onRemove,
        Action onLeaveTab,
        Action onGoToSearch)
    {
        _runOnUi = runOnUi;
        _snapshotLoader = snapshotLoader;
        _onRemove = onRemove;
        _onLeaveTab = onLeaveTab;
        _onGoToSearch = onGoToSearch;

        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;

        var filterLabel = new Label { Text = "Filter:", X = 0, Y = 0 };
        _filterField = new TextField
        {
            X = 8, Y = 0,
            Width = Dim.Percent(60) - 8,
            Text = string.Empty,
        };
        TuiHelpers.ConfigureTextInput(_filterField, SchemeNames.Base);

        _table = new TableView
        {
            X = 0,
            Y = 2,
            Width = Dim.Percent(60),
            Height = Dim.Fill(2),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(_table);
        TuiHelpers.ConfigureTableChrome(_table);

        _detail = new Markdown
        {
            X = Pos.Right(_table),
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Text = "(no skills loaded)",
        };
        TuiHelpers.ConfigureMarkdownPane(_detail, SchemeNames.Base);

        _footer = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Text = " loading inventory…",
        };

        // Per-tab StatusBar lives at the bottom of the tab view so its hints
        // ride along with visibility instead of competing with the global
        // window status bar for the same row.
        // Per-tab StatusBar — extend BuildShortcuts with the tab-only P
        // pin-filter shortcut so users see all the keys winget-tui style.
        _statusBar = new StatusBar(WithPinShortcut(InstalledScreen.BuildShortcuts(canRemove: true, hasPackages: false)));

        _filterField.TextChanged += (_, _) => RefreshAll();
        _table.ValueChanged += (_, _) =>
        {
            var row = _table.GetSelectedRow();
            if (row >= 0 && row < _rows.Count)
            {
                _detail.Text = InstalledScreen.RenderDetail(_rows[row]);
            }
        };
        _table.FrameChanged += (_, _) =>
        {
            var w = _table.Viewport.Width;
            if (w > 0 && w != _lastWidth)
            {
                _lastWidth = w;
                RecomputeColumnWidths();
            }
        };

        KeyDown += OnKeyDown;

        TuiHelpers.ApplyScheme(SchemeNames.Base,
            this, filterLabel, _filterField, _table, _detail, _footer, _statusBar);

        Add(filterLabel, _filterField, _table, _detail, _footer, _statusBar);
    }

    /// Kick off a snapshot load and reveal the tab. Safe to call repeatedly —
    /// each call refreshes the inventory.
    internal async Task LoadAsync()
    {
        var loadGeneration = Interlocked.Increment(ref _loadGeneration);
        Visible = true;
        _footer.Text = " loading inventory…";
        try
        {
            var snapshot = await _snapshotLoader().ConfigureAwait(false);
            await _runOnUi(() =>
            {
                if (!IsCurrentLoad(loadGeneration))
                {
                    return;
                }

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

                _footer.Text = $" inventory load failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
            }).ConfigureAwait(false);
        }
    }

    /// Populate the tab from an already-captured snapshot without triggering
    /// another async load. Used by startup auto-open so we don't double-scan.
    internal void LoadSeeded(InventorySnapshot snapshot)
    {
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

        // Per-tab status bar adapts to whether packages are present (Sort key
        // is only useful when there are multiple sort modes worth cycling).
        var newShortcuts = WithPinShortcut(InstalledScreen.BuildShortcuts(canRemove: true, hasPackages: _hasPackages));
        _statusBar.RemoveAll();
        foreach (var s in newShortcuts) _statusBar.Add(s);

        ApplyFilter();
        BuildTableSource();
        RecomputeColumnWidths();
        RefreshFooter();
        _detail.Text = _rows.Count == 0 ? "(no matches)" : InstalledScreen.RenderDetail(_rows[0]);
        _table.SetFocus();
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
        source = _pinFilter switch
        {
            PinFilter.PinnedOnly   => source.Where(s => s.Pinned),
            PinFilter.UnpinnedOnly => source.Where(s => !s.Pinned),
            _                      => source,
        };
        _rows = ApplySort(source);
    }

    private static Shortcut[] WithPinShortcut(Shortcut[] existing)
    {
        var pin = new Shortcut { Title = "P", HelpText = "Pin filter" };
        // Insert before the trailing Esc/q pair so it sits with the other
        // filter/sort keys.
        var insertAt = existing.Length;
        for (var i = 0; i < existing.Length; i++)
        {
            if (existing[i].Title == "Esc")
            {
                insertAt = i;
                break;
            }
        }
        var list = existing.ToList();
        list.Insert(insertAt, pin);
        return list.ToArray();
    }

    internal static PinFilter CyclePin(PinFilter current) => current switch
    {
        PinFilter.All          => PinFilter.PinnedOnly,
        PinFilter.PinnedOnly   => PinFilter.UnpinnedOnly,
        _                      => PinFilter.All,
    };

    internal static string DescribePin(PinFilter f) => f switch
    {
        PinFilter.PinnedOnly   => "pinned only",
        PinFilter.UnpinnedOnly => "unpinned only",
        _                      => "all",
    };

    private IReadOnlyList<InstalledSkill> ApplySort(IEnumerable<InstalledSkill> input) => _sort switch
    {
        SortMode.Package => input
            .OrderBy(s => s.Package?.Source ?? "~", StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        SortMode.Scope => input
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
            ["Scope"] = s => DisplayScope(s.Scope),
        };
        if (_hasPackages)
        {
            columns["Package"] = s => TuiHelpers.Truncate(s.Package?.Source ?? "", _pkgW);
        }
        columns["!"] = s => s.Validity == ValidityState.Valid ? "" : "!";
        columns["Lnk"] = s => s.IsSymlinked ? "↩" : "";
        columns["Agents"] = s => TuiHelpers.Truncate(
            TuiHelpers.AgentBadges(s.Agents.Select(a => a.AgentId)),
            _agentsW);

        _table.Table = new InstalledTableSource(_rows, columns);
        var style = _table.Style;
        style.ExpandLastColumn = true;
        for (var i = 0; i < _table.Table.Columns; i++)
        {
            var cs = style.GetOrCreateColumnStyle(i);
            switch (_table.Table.ColumnNames[i])
            {
                case "Name": cs.MinWidth = 8; cs.MaxWidth = _nameW; break;
                case "Package": cs.MinWidth = 8; cs.MaxWidth = _pkgW; break;
                case "Agents": cs.MinWidth = 6; break;
            }
        }
        _table.Update();
    }

    private void RecomputeColumnWidths()
    {
        var viewportWidth = _table.Viewport.Width;
        var available = viewportWidth > 0
            ? Math.Max(40, viewportWidth - 6)
            : 70;
        var fixedCols = 6 + 1 + 3;
        var remaining = Math.Max(20, available - fixedCols);
        if (_hasPackages)
        {
            _pkgW = Math.Max(12, (int)Math.Round(remaining * 0.30));
            _nameW = Math.Max(12, (int)Math.Round(remaining * 0.40));
            _agentsW = Math.Max(6, remaining - _pkgW - _nameW);
        }
        else
        {
            _nameW = Math.Max(12, (int)Math.Round(remaining * 0.65));
            _agentsW = Math.Max(6, remaining - _nameW);
        }
        var style = _table.Style;
        for (var i = 0; i < (_table.Table?.Columns ?? 0); i++)
        {
            var name = _table.Table!.ColumnNames[i];
            var cs = style.GetOrCreateColumnStyle(i);
            if (name == "Name") cs.MaxWidth = _nameW;
            else if (name == "Package") cs.MaxWidth = _pkgW;
        }
        _table.SetNeedsDraw();
    }

    private void RefreshAll()
    {
        if (_snapshot is null) return;
        ApplyFilter();
        BuildTableSource();
        RecomputeColumnWidths();
        RefreshFooter();
        _detail.Text = _rows.Count == 0
            ? "(no matches)"
            : InstalledScreen.RenderDetail(_rows[Math.Clamp(_table.GetSelectedRow(), 0, _rows.Count - 1)]);
    }

    private void RefreshFooter()
    {
        if (_snapshot is null)
        {
            _footer.Text = " (inventory not loaded)";
            return;
        }
        var counts = _rows.Count == _all.Count
            ? $" {_all.Count} skill(s) across {_snapshot.ScannedRoots.Length} root(s)"
            : $" {_rows.Count} of {_all.Count} skill(s) (filtered) · {_snapshot.ScannedRoots.Length} root(s)";
        var pkgs = _packageCount > 0 ? $" · {_packageCount} package(s)" : "";
        var srcSuffix = _snapshot.UsedGhSkillList ? " · gh data + scan" : " · scan only";
        var sortLabel = _sort switch
        {
            SortMode.Package => "package",
            SortMode.Scope => "scope",
            _ => "name",
        };
        var pinSuffix = _pinFilter == PinFilter.All ? "" : $" · 📌 {DescribePin(_pinFilter)}";
        _footer.Text = $"{counts}{pkgs} · sort: {sortLabel}{pinSuffix}{srcSuffix}";
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
                    SortMode.Name => _hasPackages ? SortMode.Package : SortMode.Scope,
                    SortMode.Package => SortMode.Scope,
                    _ => SortMode.Name,
                };
                RefreshAll();
                break;
            case InstalledScreen.ShortcutCommand.Remove:
            {
                var i = _table.GetSelectedRow();
                if (i >= 0 && i < _rows.Count && _snapshot is not null)
                {
                    _onRemove(_rows[i], _snapshot);
                }
                break;
            }
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

    private static string DisplayScope(Scope s) => s switch
    {
        Scope.User => "Global",
        _ => s.ToString(),
    };

    private sealed class InstalledTableSource : EnumerableTableSource<InstalledSkill>
    {
        public InstalledTableSource(IReadOnlyList<InstalledSkill> rows, Dictionary<string, Func<InstalledSkill, object>> cols)
            : base(rows, cols) { }
    }

    private bool IsCurrentLoad(long loadGeneration) =>
        Interlocked.Read(ref _loadGeneration) == loadGeneration;

    internal IReadOnlyList<string> VisibleSkillNamesForTests => _rows.Select(s => s.Name).ToArray();
}
