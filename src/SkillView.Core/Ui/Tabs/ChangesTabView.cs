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

/// Embedded tab 3 — the Changes workspace maintenance queue.
/// Shows issue + physical-installation observations with frozen detail and
/// routes exact selected targets into the existing specialist workflows.
internal sealed class ChangesTabView : FrameView
{
    private const int KindColumnWidth = 11;
    private readonly Action? _onRefresh;
    private readonly Func<Action, Task> _runOnUi;
    private readonly Func<CancellationToken, Task<InventorySnapshot>> _snapshotLoader;
    private readonly Action _onActivateUpdates;
    private readonly Action _onActivateCleanup;
    private readonly Action _onActivateDoctor;
    private readonly Action _onLeaveTab;
    private readonly Action? _onStateChange;
    private readonly CancellationToken _lifetimeToken;
    private readonly Action<MaintenanceQueueEntry>? _onActivateEntry;
    private readonly Action<Func<Task>, string>? _runTask;
    private readonly Func<MaintenanceQueueEntry, ChangesSelectionGate.Lease, Task>? _activateEntryAsync;
    private readonly ChangesSelectionGate _selectionRequests;
    private long _selectionGeneration;

    private readonly TabLoadingView _loading = new("Loading maintenance observations…");
    private readonly TableView _table;
    private readonly Markdown _detail;
    private readonly Label _status;
    private readonly SpinnerView _spinner;
    private IReadOnlyList<ChangesQueueRow> _rows = Array.Empty<ChangesQueueRow>();
    private string _coverageDetail = string.Empty;
    private int _omittedRows;
    private bool _showDetails;
    private long _loadGeneration;
    private CachedQueue? _cachedQueue;
    private sealed record CachedQueue(InventorySnapshot Snapshot, ChangesQueueProjection Projection, bool Partial);
    private readonly CancellationOperationSlot _loadCancellations;

    internal ChangesTabView(
        Func<Action, Task> runOnUi,
        Func<CancellationToken, Task<InventorySnapshot>> snapshotLoader,
        Action onActivateUpdates,
        Action onActivateCleanup,
        Action onActivateDoctor,
        Action onLeaveTab,
        Action? onStateChange = null,
        CancellationToken lifetimeToken = default,
        Action<AggregateException>? onCancellationCallbackException = null,
        Action<MaintenanceQueueEntry>? onActivateEntry = null,
        Action<Func<Task>, string>? runTask = null,
        Func<MaintenanceQueueEntry, ChangesSelectionGate.Lease, Task>? activateEntryAsync = null,
        Action? onRefresh = null)
    {
        _onRefresh = onRefresh;
        _runOnUi = runOnUi;
        _snapshotLoader = snapshotLoader;
        _onActivateUpdates = onActivateUpdates;
        _onActivateCleanup = onActivateCleanup;
        _onActivateDoctor = onActivateDoctor;
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

        _table = new TableView
        {
            X = 0,
            Y = 0,
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
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            Text = "## Changes\n\n_Select a queue item to inspect its details._",
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
        KeyDown += (_, key) =>
        {
            if (key.Handled || key.KeyCode != KeyCode.F2 || !_rows.Any(row => row.Entry is not null)) return;
            key.Handled = true;
            _showDetails = !_showDetails;
            UpdateDetail();
            _status.Text = BuildStatusText(string.Empty);
        };

        Add(_table, _detail, _spinner, _status, _loading);
        Disposing += (_, _) => CancelPendingLoad();
    }

    /// Load the maintenance queue from the inventory snapshot.
    internal async Task LoadAsync()
    {
        CancelSelectedRequest();
        var gen = Interlocked.Increment(ref _loadGeneration);
        using var loadCancellation = _loadCancellations.Replace(_lifetimeToken);
        Visible = true;
        _rows = Array.Empty<ChangesQueueRow>();
        _table.Table = null;
        _detail.Text = string.Empty;
        _coverageDetail = string.Empty;
        _showDetails = false;
        SetLoading(true);
        _status.Text = " loading inventory…";

        try
        {
            var snapshot = await _snapshotLoader(loadCancellation.Token).ConfigureAwait(false);
            var cached = Volatile.Read(ref _cachedQueue);
            if (cached is null || !ReferenceEquals(cached.Snapshot, snapshot))
            {
                // A cached snapshot can complete synchronously on the UI thread.
                // Classification still performs filesystem observations.
                cached = await Task.Run(() =>
                {
                    var queue = MaintenanceQueueBuilder.Build(snapshot, loadCancellation.Token);
                    return new CachedQueue(snapshot, ChangesQueueBuilder.Build(queue, loadCancellation.Token), queue.Partial);
                }, loadCancellation.Token).ConfigureAwait(false);
            }
            var projection = cached.Projection;
            var summary = (cached.Partial ? "Partial inventory · " : "")
                + "update availability and sessions unknown · r refresh disk inventory";

            await _runOnUi(() =>
            {
                if (Interlocked.Read(ref _loadGeneration) != gen || loadCancellation.Token.IsCancellationRequested) return;
                _cachedQueue = cached;
                _coverageDetail = projection.CoverageDetail;
                _omittedRows = projection.OmittedRows;
                if (projection.OmittedRows > 0)
                    summary += $" · {projection.OmittedRows} rows omitted by presentation limit; queue --json captures all observations";
                LoadRows(projection.Rows, summary, loadCancellation.Token);
                if (projection.Rows.Count == 0)
                    _detail.Text = "## Maintenance observations\n\nNo matching physical installations or cleanup observations. Update availability and active sessions remain unknown.\n"
                        + _coverageDetail;
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
                _detail.Text = "## Could not load changes\n\nPress **r** to retry. See the status below for details.";
                SetLoading(false);
                _table.SetFocus();
            }).ConfigureAwait(false);
        }
    }

    /// Bind pre-computed string lists to the table. Exposed for unit tests.
    internal void Load(
        IEnumerable<string> updates,
        IEnumerable<string> cleanup,
        IEnumerable<string> diagnostics,
        string summary = "Healthy")
    {
        LoadRows(ChangesQueueBuilder.BuildForTests(updates, cleanup, diagnostics), summary);
    }

    private void LoadRows(
        IReadOnlyList<ChangesQueueRow> rows,
        string summary,
        CancellationToken renderToken = default)
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
        _status.Text = BuildStatusText(summary);
        UpdateDetail(renderToken);

        SetLoading(false);
        if (_rows.Count > 0)
        {
            _table.SetSelectedRow(0);
            _table.SetFocus();
        }

        _onStateChange?.Invoke();
        SetLoading(false);
    }

    // Tests only -----------------------------------------------------------

    internal int RowCountForTests => _rows.Count;
    internal string StatusTextForTests => _status.Text.ToString();

    internal int GetPendingCount() => _rows.Count;

    internal string GetQueueLabel() => DescribeQueueLabel();

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
        var kind = _rows[row].Kind;
        switch (kind)
        {
            case "Update": _onActivateUpdates(); break;
            case "Cleanup": _onActivateCleanup(); break;
            case "Diagnostics": _onActivateDoctor(); break;
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
                ? (_rows[row].Entry is { } entry ? MaintenanceQueueFormatter.Render(entry, token, _showDetails)
                    : _rows[row].Detail ?? RenderDetail(_rows[row]))
                    + (_showDetails ? _coverageDetail + (_omittedRows > 0
                        ? $"\n{_omittedRows} additional observations are omitted from the list. Use queue --json for all observations.\n" : string.Empty)
                        : string.IsNullOrEmpty(_coverageDetail) ? string.Empty
                        : "\nSome inventory locations could not be fully checked. Press **F2** for coverage details.\n")
                : "## Changes\n\n_No pending work._";
            token.ThrowIfCancellationRequested();
            _detail.Text = detail;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static string RenderDetail(ChangesQueueRow row)
    {
        var summary = row.Kind switch
        {
            "Update" => "Run the selected update workflow to inspect package-level changes before applying them.",
            "Cleanup" => "Review the pending cleanup item, then confirm removal from the cleanup workflow.",
            "Diagnostics" => "Open Doctor for the selected diagnostic item and inspect the current environment report.",
            _ => "Inspect the selected queue item.",
        };

        return $"## {row.Kind}\n\n- **Item:** `{row.Title}`\n\n{summary}";
    }

    private string BuildStatusText(string summary)
    {
        if (_rows.Count == 0)
        {
            return $" {summary}";
        }

        if (_rows.Any(row => row.Entry is not null))
        {
            var count = _omittedRows > 0 ? $"{_rows.Count} shown + {_omittedRows} omitted" : $"{_rows.Count} observations";
            return $" {(_coverageDetail.Length > 0 ? "Limited scan · " : "")}{count} · Enter Review · F2 {(_showDetails ? "Summary" : "Details")} · r Refresh";
        }
        var label = DescribeQueueLabel();
        return label == "Changes"
            ? $" {summary} · {_rows.Count} items"
            : $" {label} · {_rows.Count}";
    }

    private string DescribeQueueLabel()
    {
        if (_rows.Count == 0)
        {
            return string.Empty;
        }
        if (_rows.Any(row => row.Entry is not null)) return "Maintenance observations";

        var distinctKinds = _rows
            .Select(row => row.Kind)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctKinds.Length != 1)
        {
            return "Changes";
        }

        return distinctKinds[0] switch
        {
            "Cleanup" => _rows.Count == 1 ? "Cleanup candidate" : "Cleanup candidates",
            "Update" => _rows.Count == 1 ? "Update" : "Updates",
            "Diagnostics" => _rows.Count == 1 ? "Diagnostic" : "Diagnostics",
            _ => distinctKinds[0],
        };
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
        if (key.KeyCode == KeyCode.Enter)
        {
            key.Handled = true;
            ActivateSelectedRow();
        }
        if (key.AsRune.Value == 'r' && _runTask is not null)
        {
            key.Handled = true;
            _onRefresh?.Invoke();
            _runTask(LoadAsync, "changes.refresh-disk");
        }
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
        _loading.SetLoading(loading);
        _table.Visible = !loading;
        _detail.Visible = !loading;
        _spinner.Visible = loading;
        _spinner.AutoSpin = loading;
    }

    private void CancelSelectedRequest()
    {
        Interlocked.Increment(ref _selectionGeneration);
        _selectionRequests.Cancel();
    }
}
