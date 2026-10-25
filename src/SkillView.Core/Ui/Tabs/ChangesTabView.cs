using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

/// Embedded tab 3 — the Issues findings view.
/// Shows issue + physical-installation observations with frozen detail and
/// routes exact selected targets into the existing specialist workflows.
internal sealed class ChangesTabView : FrameView
{
    private const int KindColumnWidth = 8;
    private readonly string? _homeDirectory;
    private readonly Action? _onRefresh;
    private readonly Func<Action, Task> _runOnUi;
    private readonly Func<CancellationToken, Task<InventorySnapshot>> _snapshotLoader;
    private readonly Action _onLeaveTab;
    private readonly Action? _onStateChange;
    private readonly CancellationToken _lifetimeToken;
    private readonly Action<MaintenanceQueueEntry>? _onActivateEntry;
    private readonly Action<Func<Task>, string>? _runTask;
    private readonly Func<MaintenanceQueueEntry, ChangesSelectionGate.Lease, Task>? _activateEntryAsync;
    private readonly ChangesSelectionGate _selectionRequests;
    private long _selectionGeneration;
    private readonly UpdateAvailabilitySession? _availability;
    private readonly Action? _onCheckUpdates;
    private readonly Button _checkUpdates;
    private readonly Button _reviewFinding;
    private readonly Label _updateStatus;
    private InventorySnapshot? _snapshot;
    private bool _disposed;
    private string? _returnSelectionId;
    private int _returnRowOffset;

    private readonly TabLoadingView _loading = new("Loading local findings…");
    private readonly TableView _table;
    private readonly Markdown _detail;
    private readonly Label _status;
    private readonly SpinnerView _spinner;
    private IReadOnlyList<ChangesQueueRow> _rows = Array.Empty<ChangesQueueRow>();
    private string _coverageDetail = string.Empty;
    private int _omittedRows;
    private int _availabilityOmittedRows;
    private int OmittedRows => _omittedRows + _availabilityOmittedRows;
    private ChangesQueueProjection? _projection;
    private bool _partial;
    private bool _showDetails;
    private long _loadGeneration;
    private CachedQueue? _cachedQueue;
    private sealed record CachedQueue(InventorySnapshot Snapshot, ChangesQueueProjection Projection, bool Partial, InstalledIssueEvidence Evidence);
    private readonly CancellationOperationSlot _loadCancellations;
    private readonly Func<long> _catalogGeneration;
    internal InstalledIssueEvidence? CapturedIssues => _disposed || _lifetimeToken.IsCancellationRequested ? null : _cachedQueue?.Evidence;

    internal ChangesTabView(
        Func<Action, Task> runOnUi,
        Func<CancellationToken, Task<InventorySnapshot>> snapshotLoader,
        Action onLeaveTab,
        Action? onStateChange = null,
        CancellationToken lifetimeToken = default,
        Action<AggregateException>? onCancellationCallbackException = null,
        Action<MaintenanceQueueEntry>? onActivateEntry = null,
        Action<Func<Task>, string>? runTask = null,
        Func<MaintenanceQueueEntry, ChangesSelectionGate.Lease, Task>? activateEntryAsync = null,
        Action? onRefresh = null, string? homeDirectory = null,
        UpdateAvailabilitySession? availability = null, Action? onCheckUpdates = null, Func<long>? catalogGeneration = null)
    {
        _catalogGeneration = catalogGeneration ?? (() => 0);
        _homeDirectory = homeDirectory;
        _availability = availability;
        _onCheckUpdates = onCheckUpdates;
        _onRefresh = onRefresh;
        _runOnUi = runOnUi;
        _snapshotLoader = snapshotLoader;
        _onLeaveTab = onLeaveTab;
        _onStateChange = onStateChange;
        _lifetimeToken = lifetimeToken;
        _onActivateEntry = onActivateEntry;
        _runTask = runTask;
        _activateEntryAsync = activateEntryAsync;
        _selectionRequests = new ChangesSelectionGate(onCancellationCallbackException);
        _loadCancellations = new CancellationOperationSlot(onCancellationCallbackException);

        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;
        _checkUpdates = new SingleRowButton { X = 0, Y = 0, Text = "Check updates (C)", Visible = availability is not null };
        _reviewFinding = new SingleRowButton { X = Pos.Right(_checkUpdates) + 1, Y = 0, Text = "Review (Enter)", Visible = availability is not null };
        _updateStatus = new Label { X = Pos.Right(_reviewFinding) + 1, Y = 0, Width = Dim.Fill(), Text = "Updates not checked", Visible = availability is not null };
        _checkUpdates.Accepting += (_, e) => { e.Handled = true; _onCheckUpdates?.Invoke(); };
        _reviewFinding.Accepting += (_, e) => { e.Handled = true; if (!_loading.Visible) ActivateSelectedRow(); };

        _table = new TableView
        {
            X = 0,
            Y = availability is null ? 0 : 1,
            Width = Dim.Percent(60),
            Height = Dim.Fill(1),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(_table);
        TuiHelpers.ConfigureTableNavigation(_table);
        TuiHelpers.ConfigureTableChrome(_table);
        _table.ValueChanged += (_, _) => UpdateDetail();
        _table.FrameChanged += (_, _) => RecomputeColumnWidths();

        _detail = new Markdown
        {
            X = Pos.Right(_table) + 1,
            Y = availability is null ? 0 : 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            Text = "## Issues\n\n_Select a finding to inspect its details._",
        };
        TuiHelpers.ConfigureMarkdownPane(_detail, SchemeNames.Base);

        _status = new Label
        {
            X = 2,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(2),
            Text = " loading…",
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

        TuiHelpers.ApplyScheme(SchemeNames.Base, this, _table, _detail, _spinner, _status);

        _table.KeyDown += OnTableKeyDown;
        _loading.KeyDown += OnTableKeyDown;
        _detail.KeyDown += OnTableKeyDown;
        KeyDown += (_, key) =>
        {
            if (key.Handled || _loading.Visible || key.KeyCode != KeyCode.F2 || _rows.Count == 0 && _coverageDetail.Length == 0) return;
            key.Handled = true;
            _showDetails = !_showDetails;
            UpdateDetail();
            _status.Text = BuildStatusText();
        };

        Add(_table, _detail, _spinner, _status, _loading, _checkUpdates, _reviewFinding, _updateStatus);
        Disposing += (_, _) => { _disposed = true; CancelPendingLoad(); };
    }

    /// Load the maintenance queue from the inventory snapshot.
    internal async Task LoadAsync()
    {
        var selectedRow = _table.GetSelectedRow();
        if (selectedRow >= 0 && selectedRow < _rows.Count)
        {
            _returnSelectionId = _rows[selectedRow].Entry.Id;
            _returnRowOffset = _table.RowOffset;
        }
        CancelSelectedRequest();
        var gen = Interlocked.Increment(ref _loadGeneration);
        using var loadCancellation = _loadCancellations.Replace(_lifetimeToken);
        Visible = true;
        _rows = Array.Empty<ChangesQueueRow>();
        _table.Table = null;
        _detail.Text = string.Empty;
        _coverageDetail = string.Empty;
        _showDetails = false;
        _projection = null;
        _partial = false;
        SetLoading(true);
        _status.Text = " loading inventory…";

        try
        {
            var catalogGeneration = _catalogGeneration();
            var snapshot = await _snapshotLoader(loadCancellation.Token).ConfigureAwait(false);
            var cached = Volatile.Read(ref _cachedQueue);
            if (cached is null || !ReferenceEquals(cached.Snapshot, snapshot) || cached.Evidence.Generation != catalogGeneration)
            {
                // A cached snapshot can complete synchronously on the UI thread.
                // Classification still performs filesystem observations.
                cached = await Task.Run(() =>
                {
                    var queue = MaintenanceQueueBuilder.Build(snapshot, loadCancellation.Token);
                    return new CachedQueue(snapshot, ChangesQueueBuilder.Build(queue, loadCancellation.Token), queue.Partial, InstalledIssueEvidence.Capture(snapshot, catalogGeneration, queue));
                }, loadCancellation.Token).ConfigureAwait(false);
            }
            var projection = cached.Projection;

            await _runOnUi(() =>
            {
                if (Interlocked.Read(ref _loadGeneration) != gen || loadCancellation.Token.IsCancellationRequested
                    || _disposed || _lifetimeToken.IsCancellationRequested) return;
                if (catalogGeneration != _catalogGeneration())
                {
                    SetLoading(false);
                    _status.Text = " Inventory changed; refresh Issues to load current findings.";
                    return;
                }
                _snapshot = snapshot;
                _cachedQueue = cached;
                _projection = projection;
                _partial = cached.Partial || projection.CoverageDetail.Length > 0;
                _coverageDetail = projection.CoverageDetail;
                _omittedRows = projection.OmittedRows;
                LoadRows(AvailabilityRows(), loadCancellation.Token);
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
                if (Interlocked.Read(ref _loadGeneration) != gen || loadCancellation.Token.IsCancellationRequested) return;
                _status.Text = $" load failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
                _detail.Text = "## Could not load issues\n\nPress **r** to retry. See the status below for details.";
                SetLoading(false);
                _table.SetFocus();
            }).ConfigureAwait(false);
        }
    }

    private void LoadRows(IReadOnlyList<ChangesQueueRow> rows, CancellationToken renderToken = default)
    {
        _rows = rows;
        var source = new EnumerableTableSource<ChangesQueueRow>(
            _rows,
            new Dictionary<string, Func<ChangesQueueRow, object>>
            {
                ["Kind"] = r => r.Kind,
                ["Title"] = r => r.Title,
            });
        _table.Table = source;

        var style = _table.Style;
        style.ExpandLastColumn = true;
        style.ShowVerticalCellLineForLastColumn = true;
        var kindStyle = style.GetOrCreateColumnStyle(0);
        kindStyle.MinWidth = KindColumnWidth;
        kindStyle.MaxWidth = KindColumnWidth;

        RecomputeColumnWidths();
        _table.Update();
        var restoredRow = rows.Select((row, index) => (row, index))
            .FirstOrDefault(item => item.row.Entry.Id == _returnSelectionId);
        _table.SetSelectedRow(restoredRow.row is null ? 0 : restoredRow.index);
        _table.RowOffset = _returnRowOffset;
        _status.Text = BuildStatusText();
        UpdateDetail(renderToken);

        SetLoading(false);
        if (_rows.Count > 0)
        {
            _table.SetFocus();
        }
        else
        {
            // Terminal.Gui's empty table can consume printable keys before
            // they reach the shell. Keep update/help/navigation usable.
            _detail.SetFocus();
        }

        _onStateChange?.Invoke();
    }

    // Tests only -----------------------------------------------------------

    internal int RowCountForTests => _rows.Count;
    internal string StatusTextForTests => _status.Text.ToString();

    internal string GetQueueLabel() => (_projection?.Counts ?? string.Empty).Replace(
        ChangesQueueProjection.UpdatesNotChecked, _availability?.Summary ?? ChangesQueueProjection.UpdatesNotChecked, StringComparison.Ordinal);

    // Internals ------------------------------------------------------------

    private void ActivateSelectedRow()
    {
        var row = _table.GetSelectedRow();
        if (row < 0 || row >= _rows.Count) return;
        if (_rows[row].Entry is { } entry)
        {
            if (_activateEntryAsync is not null && _runTask is not null)
            {
                var generation = Interlocked.Increment(ref _selectionGeneration);
                _selectionRequests.Cancel();
                _runTask(async () =>
                {
                    if (Interlocked.Read(ref _selectionGeneration) != generation || !Visible || _lifetimeToken.IsCancellationRequested) return;
                    using var request = _selectionRequests.Begin(_lifetimeToken);
                    if (request is null) return;
                    try
                    {
                        if (request.IsCurrent) await _activateEntryAsync(entry, request).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
                }, "changes.selected-review");
                return;
            }
            _onActivateEntry?.Invoke(entry);
            return;
        }
    }

    private void UpdateDetail(CancellationToken token = default)
    {
        if (!token.CanBeCanceled) token = _lifetimeToken;
        if (token.IsCancellationRequested) return;
        try
        {
            var row = _table.GetSelectedRow();
            var detail = row >= 0 && row < _rows.Count
                ? ChangesQueueBuilder.Render(_rows[row], token, _showDetails, _homeDirectory)
                : "## Issues\n\n" + (_partial
                    ? "No local problems found in checked locations. Discovery is incomplete.\n"
                    : $"No local problems found. {ChangesQueueProjection.UpdatesNotChecked}.\n");
            if (_rows.Count > 0 || _partial) detail += $"\n{ChangesQueueProjection.UpdatesNotChecked}.\n";
            if (_availability is not null)
                detail = detail.Replace(ChangesQueueProjection.UpdatesNotChecked, _availability.Summary, StringComparison.Ordinal);
            detail += "\n**C:** Check updates / cancel. **Enter / u:** review selected finding. **r:** refresh local inventory. Routine inspection is in **Installed (2)**.\n";
            if (_showDetails) detail += _coverageDetail;
            else if (_coverageDetail.Length > 0) detail += "\nDiscovery/checks incomplete. Press **F2** for coverage details.\n";
            if (OmittedRows > 0) detail += $"\n{OmittedRows} additional findings omitted by the row limit. Counts include these findings; Installed retains per-copy update results; queue --json makes a fresh local capture.\n";
            token.ThrowIfCancellationRequested();
            _detail.Text = detail;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private string BuildStatusText()
    {
        if (_projection is null) return string.Empty;
        return " " + (_partial ? "Scan incomplete · " : "") + _projection.Counts.Replace(ChangesQueueProjection.UpdatesNotChecked, _availability?.Summary ?? ChangesQueueProjection.UpdatesNotChecked, StringComparison.Ordinal)
            + (OmittedRows > 0 ? $" · {_rows.Count} shown, {OmittedRows} omitted" : "")
            + (_rows.Count > 0 || _coverageDetail.Length > 0 ? $" · F2 {(_showDetails ? "Summary" : "Details")}" : "");
    }

    private void RecomputeColumnWidths()
    {
        if (_table.Table is null)
        {
            return;
        }

        var available = _table.Viewport.Width > 0
            ? Math.Max(1, _table.Viewport.Width - 3)
            : 48;
        var titleWidth = Math.Max(1, available - KindColumnWidth);
        var titleStyle = _table.Style.GetOrCreateColumnStyle(1);
        titleStyle.MinWidth = titleWidth;
        titleStyle.MaxWidth = titleWidth;
        _table.SetNeedsDraw();
    }

    private void OnTableKeyDown(object? sender, Key key)
    {
        if (key.Handled) return;
        if (key.KeyCode == KeyCode.Esc)
        {
            key.Handled = true;
            _onLeaveTab();
            return;
        }
        if (key.AsRune.Value == 'C' && _onCheckUpdates is not null)
        {
            key.Handled = true;
            _onCheckUpdates();
            return;
        }
        if (key.KeyCode == KeyCode.Enter || key.AsRune.Value is 'u' or 'U')
        {
            key.Handled = true;
            if (!_loading.Visible) ActivateSelectedRow();
        }
        if (key.AsRune.Value == 'r' && _runTask is not null)
        {
            key.Handled = true;
            _onRefresh?.Invoke();
            _runTask(LoadAsync, "changes.refresh-disk");
        }
    }

    private IReadOnlyList<ChangesQueueRow> AvailabilityRows()
    {
        if (_projection is null) return [];
        if (_availability is null || _snapshot is null) return _projection.Rows;
        var updates = _snapshot.Skills.Select(skill => (Skill: skill, Check: _availability.Get(skill)))
            .Where(item => item.Check.Status == UpdateAvailabilityStatus.Available)
            .Select(item => new ChangesQueueRow("Updates", item.Skill.Name + " · " + DisplayPath.Abbreviate(item.Skill.ResolvedPath, _homeDirectory),
                [new MaintenanceQueueEntry("update-" + UpdateAvailability.Address(item.Skill), "upstream-update", item.Skill.Name,
                    OperationSubject.From(item.Skill), [item.Skill.ResolvedPath], "gh-dry-run", item.Check.Reason,
                    "review-update", false, item.Skill.Pinned)]) { Availability = item.Check }).ToArray();
        _availabilityOmittedRows = Math.Max(0, _projection.Rows.Count + updates.Length - ChangesQueueBuilder.MaxRows);
        return _projection.Rows.Where(row => row.Kind == "Problem").Concat(updates)
            .Concat(_projection.Rows.Where(row => row.Kind != "Problem")).Take(ChangesQueueBuilder.MaxRows).ToArray();
    }

    internal void RefreshAvailability()
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested || _availability is null) return;
        _checkUpdates.Text = _availability.Running ? "Cancel check (C)" : "Check updates (C)";
        _checkUpdates.Enabled = !_availability.Stopping;
        _updateStatus.Text = _availability.Summary;
        _reviewFinding.Enabled = !_loading.Visible && _rows.Count > 0;
        if (_loading.Visible || _projection is null) return;
        var selected = _table.GetSelectedRow();
        var id = selected >= 0 && selected < _rows.Count ? _rows[selected].Entry.Id : null;
        var offset = _table.RowOffset;
        var detailOffset = _detail.Viewport.Location;
        var rows = AvailabilityRows();
        // Progress/reasons can change without changing any findings.
        if (!_rows.Select(row => row.Entry.Id).SequenceEqual(rows.Select(row => row.Entry.Id)))
        {
            var hadFocus = _table.HasFocus;
            _rows = rows;
            _table.Table = new EnumerableTableSource<ChangesQueueRow>(_rows, new Dictionary<string, Func<ChangesQueueRow, object>>
            { ["Kind"] = row => row.Kind, ["Title"] = row => row.Title });
            var restored = rows.Select((row, i) => (row, i)).FirstOrDefault(item => item.row.Entry.Id == id);
            _table.SetSelectedRow(restored.row is null ? Math.Clamp(selected, 0, Math.Max(0, rows.Count - 1)) : restored.i);
            _table.RowOffset = offset;
            RecomputeColumnWidths();
            if (hadFocus && rows.Count == 0) _detail.SetFocus();
        }
        else _rows = rows;
        _table.Update();
        UpdateDetail();
        var currentRow = _table.GetSelectedRow();
        if (id is not null && currentRow >= 0 && currentRow < _rows.Count && _rows[currentRow].Entry.Id == id)
            TuiHelpers.RestoreMarkdownScroll(_detail, detailOffset);
        _status.Text = BuildStatusText();
        _onStateChange?.Invoke();
    }

    internal void CancelPendingLoad()
    {
        CancelSelectedRequest();
        Interlocked.Increment(ref _loadGeneration);
        _loadCancellations.Cancel();
        SetLoading(false);
    }

    private void SetLoading(bool loading)
    {
        _loading.SetContentLoading(loading, _table, _table, _detail);
        if (loading) _loading.SetFocus();
        _spinner.Visible = loading;
        _spinner.AutoSpin = loading;
    }

    private void CancelSelectedRequest()
    {
        Interlocked.Increment(ref _selectionGeneration);
        _selectionRequests.Cancel();
    }
}
