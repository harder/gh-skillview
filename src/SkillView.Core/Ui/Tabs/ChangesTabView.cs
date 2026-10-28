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
    private readonly GuidedRepairSequence? _guided;
    private readonly Func<GuidedRepairItem, ChangesSelectionGate.Lease, Task>? _activateGuided;
    private readonly Func<string> _context;
    private readonly Button _guide;
    private readonly Button _next;
    private readonly Button _skip;
    private readonly Button _stop;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly InlineCopiesView _copies;
    private readonly Button _back;
    private readonly Button _cleanupFilter;
    private bool _cleanupOnly;
    private OperationSubject? _cleanupTarget;
    private bool _detailOpen;
    private bool _restoringRows;
    private bool _layoutActive;
    private string? _missingSelection;
    private readonly Action<InstalledSkill>? _reviewCopy;
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
    private int _selectionWork;
    private int _guidedWork;
    private readonly ChangesSelectionGate _guidedRequests;
    private readonly Func<FindingBinding, ChangesSelectionGate.Lease, Task<bool>>? _validateFinding;
    private readonly UpdateAvailabilitySession? _availability;
    private readonly Func<PackageCheckFact?> _packageCheck;
    private readonly Action<PackageCheckFact>? _onPackageCheck;
    private int _packageProblems;
    private readonly Action? _onCheckUpdates;
    private readonly Button _checkUpdates;
    private readonly Button _reviewFinding;
    private readonly Button _keepBoth;
    private readonly Button _resetChoices;
    private readonly Func<ChangesSelectionGate.Lease, Task>? _resetIntentional;
    private readonly Func<CapturedFinding, ChangesSelectionGate.Lease, Task<string>>? _setIntentional;
    private bool _preferenceBusy;
    private string? _preferenceNotice;
    private string? _preferenceNoticeFamily;
    private bool Narrow => Viewport.Width is > 0 and < 110;
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
        UpdateAvailabilitySession? availability = null, Action? onCheckUpdates = null, Func<long>? catalogGeneration = null,
        Func<PackageCheckFact?>? packageCheck = null, Action<PackageCheckFact>? onPackageCheck = null,
        Func<CapturedFinding, ChangesSelectionGate.Lease, Task<string>>? setIntentional = null,
        Func<ChangesSelectionGate.Lease, Task>? resetIntentional = null,
        Func<string>? context = null, Action<InstalledSkill>? reviewCopy = null,
        GuidedRepairSequence? guided = null, Func<GuidedRepairItem, ChangesSelectionGate.Lease, Task>? activateGuided = null,
        Func<FindingBinding, ChangesSelectionGate.Lease, Task<bool>>? validateFinding = null)
    {
        _resetIntentional = resetIntentional;
        _setIntentional = setIntentional;
        _validateFinding = validateFinding;
        _guidedRequests = new ChangesSelectionGate(onCancellationCallbackException);
        _guided = guided; _activateGuided = activateGuided; _context = context ?? (() => "");
        _guide = new SingleRowButton { X = 0, Y = 0, Text = ActionLabels.Guided.ButtonText, Visible = guided is not null };
        _next = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = ActionLabels.ReviewNext.ButtonText, Visible = false };
        _skip = new SingleRowButton { X = Pos.Right(_next) + 1, Y = Pos.AnchorEnd(1), Text = ActionLabels.SkipGuided.ButtonText, Visible = false };
        _stop = new SingleRowButton { X = Pos.Right(_skip) + 1, Y = Pos.AnchorEnd(1), Text = ActionLabels.StopGuided.ButtonText, Visible = false };
        _guide.Accepting += (_, e) => { e.Handled = true; StartGuided(); };
        _next.Accepting += (_, e) => { e.Handled = true; ReviewNext(); };
        _skip.Accepting += (_, e) => { e.Handled = true; _guided?.Skip(); RefreshGuided(); };
        _stop.Accepting += (_, e) => { e.Handled = true; StopGuided(); };
        _reviewCopy = reviewCopy;
        _copies = new InlineCopiesView(runOnUi, runTask ?? ((_, _) => { }), catalogGeneration ?? (() => 0), context ?? (() => ""),
            BackToList, lifetimeToken, onCancellationCallbackException);
        _back = new SingleRowButton { X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(1), Text = "Back (Esc)", Visible = false };
        _back.Accepting += (_, e) => { e.Handled = true; BackToList(); };
        _cleanupFilter = new SingleRowButton { X = 0, Y = 0, Text = "c" };
        _cleanupFilter.Accepting += (_, e) => { e.Handled = true; CleanupSelected(); };
        _packageCheck = packageCheck ?? (() => null);
        _onPackageCheck = onPackageCheck;
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
        _checkUpdates = new SingleRowButton { X = 0, Y = 0, Text = ActionLabels.CheckUpdates.ButtonText, Visible = availability is not null };
        _reviewFinding = new SingleRowButton { X = Pos.Right(_checkUpdates) + 1, Y = 0, Text = ActionLabels.ReviewExactIssue.ButtonText, Visible = availability is not null };
        _keepBoth = new SingleRowButton { X = Pos.Right(_reviewFinding) + 1, Y = 0, Text = ActionLabels.KeepBoth.ButtonText, Visible = setIntentional is not null, Enabled = false };
        _keepBoth.Accepting += (_, e) => { e.Handled = true; SetIntentional(); };
        _resetChoices = new SingleRowButton { X = Pos.Right(_keepBoth) + 1, Y = 0, Text = ActionLabels.Choices.ButtonText, Visible = resetIntentional is not null };
        _resetChoices.Accepting += (_, e) => { e.Handled = true; ResetIntentional(); };
        _updateStatus = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = 1, Text = "Updates not checked", Visible = availability is not null };
        _checkUpdates.Accepting += (_, e) => { e.Handled = true; ClearPreferenceFeedback(); _onCheckUpdates?.Invoke(); };
        _reviewFinding.Accepting += (_, e) => { e.Handled = true; if (!_loading.Visible) ReviewSelected(); };

        _guide.X = Pos.Right(_resetChoices) + 1;
        _cleanupFilter.X = Pos.Right(_guide) + 1;

        _table = new TableView
        {
            X = 0,
            Y = 2,
            Width = Dim.Percent(60),
            Height = Dim.Fill(2),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(_table);
        TuiHelpers.ConfigureTableNavigation(_table);
        TuiHelpers.ConfigureTableChrome(_table);
        _table.ValueChanged += (_, _) =>
        {
            if (_restoringRows) return;
            _missingSelection = null; _cleanupTarget = null; _copies.Retire(); CancelSelectedRequest();
            UpdateDetail(); ApplyLayout();
        };
        _table.FrameChanged += (_, _) => RecomputeColumnWidths();

        _detail = new Markdown
        {
            X = Pos.Right(_table) + 1,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Text = "## Issues\n\n_Select a finding to inspect its details._",
        };
        TuiHelpers.ConfigureMarkdownPane(_detail, SchemeNames.Base);

        _status = new Label
        {
            X = 2,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(2),
            Text = " loading…",
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

        TuiHelpers.ApplyScheme(SchemeNames.Base, this, _table, _detail, _spinner, _status);

        _table.KeyDown += OnTableKeyDown;
        _loading.KeyDown += OnTableKeyDown;
        KeyDown += OnTableKeyDown;
        _detail.KeyDown += OnTableKeyDown;
        KeyDown += (_, key) =>
        {
            if (key.Handled || _loading.Visible || key.KeyCode != KeyCode.F2 || _rows.Count == 0 && _coverageDetail.Length == 0) return;
            key.Handled = true;
            _showDetails = !_showDetails;
            UpdateDetail();
            _status.Text = BuildStatusText();
        };

        Add(_table, _detail, _spinner, _status, _loading, _checkUpdates, _reviewFinding, _keepBoth, _resetChoices, _updateStatus, _back, _cleanupFilter, _copies, _guide, _next, _skip, _stop);
        FrameChanged += (_, _) => ApplyLayout();
        Disposing += (_, _) => { _disposed = true; CancelPendingLoad(); _guidedRequests.Dispose(); };
    }

    /// Load the maintenance queue from the inventory snapshot.
    internal async Task LoadAsync()
    {
        var selectedRow = _table.GetSelectedRow();
        if (selectedRow >= 0 && selectedRow < _rows.Count)
        {
            _returnSelectionId = _rows[selectedRow].Key;
            _returnRowOffset = _table.RowOffset;
        }
        _copies.Retire();
        CancelSelectedRequest();
        var gen = Interlocked.Increment(ref _loadGeneration);
        using var loadCancellation = _loadCancellations.Replace(_lifetimeToken);
        Visible = true;
        _restoringRows = true;
        try
        {
            _rows = Array.Empty<ChangesQueueRow>();
            _table.Table = null;
        }
        finally { _restoringRows = false; }
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
        _restoringRows = true;
        _rows = rows;
        _reviewFinding.Enabled = !_preferenceBusy && rows.Count > 0;
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
            .FirstOrDefault(item => item.row.Key == _returnSelectionId);
        _missingSelection = _returnSelectionId is not null && restoredRow.row is null ? _returnSelectionId : null;
        _table.SetSelectedRow(restoredRow.row is null ? 0 : restoredRow.index);
        _restoringRows = false;
        _table.RowOffset = _returnRowOffset;
        _status.Text = BuildStatusText();
        UpdateDetail(renderToken);

        SetLoading(false);
        ApplyLayout();
        if (_rows.Count > 0)
        {
            if (Narrow && _detailOpen) _detail.SetFocus(); else _table.SetFocus();
        }
        else
        {
            // Terminal.Gui's empty table can consume printable keys before
            // they reach the shell. Keep update/help/navigation usable.
            _detail.SetFocus();
        }

        RefreshGuided();
        _onStateChange?.Invoke();
    }

    // Tests only -----------------------------------------------------------

    internal int RowCountForTests => _rows.Count;
    internal IReadOnlyList<ChangesQueueRow> RowsForTests => _rows;
    internal void LoadRowsForTests(IReadOnlyList<ChangesQueueRow> rows) => LoadRows(rows);
    internal string StatusTextForTests => _status.Text.ToString();

    internal string GetQueueLabel() => Counts.Replace(
        ChangesQueueProjection.UpdatesNotChecked, _availability?.Summary ?? ChangesQueueProjection.UpdatesNotChecked, StringComparison.Ordinal);

    private string Counts => _projection is null ? string.Empty
        : $"{_projection.Problems + _packageProblems} Problems · {ChangesQueueProjection.UpdatesNotChecked} · {_projection.ReviewSuggestions} Review · {_projection.Informational} Info";

    private bool GuidedBusy => _guided?.HasOpenItemOrWork == true || Volatile.Read(ref _guidedWork) != 0;
    private bool SequenceActive => _guided?.HasActiveSequence == true;
    private bool CanStartGuided => _guided is not null && _snapshot is not null && SelectionCurrent
        && !SequenceActive && !GuidedBusy && !_preferenceBusy && Volatile.Read(ref _selectionWork) == 0 && !_copies.Busy
        && _rows.Any(row => row.ExactEntry is { } entry && (row.Availability is not null || entry.Cleanup is { ReviewOnly: false }));

    private void StartGuided()
    {
        if (!CanStartGuided) return;
        _guided!.Start(_rows.Where(row => row.ExactEntry is { } entry
                && (row.Availability is not null || entry.Cleanup is { ReviewOnly: false })).Select(row => row.ExactEntry!), _snapshot!, _context());
        RefreshGuided();
    }

    private void ReviewNext()
    {
        if (_guided is null || _activateGuided is null || _runTask is null || !SelectionCurrent
            || _preferenceBusy || Volatile.Read(ref _selectionWork) != 0) return;
        var item = _guided.ReviewNext();
        if (item is null) return;
        CancelSelectedRequest();
        Interlocked.Increment(ref _guidedWork);
        RefreshGuided();
        _runTask(async () =>
        {
            try
            {
                using var request = _guidedRequests.Begin(_lifetimeToken);
                if (request is null) { _guided.Complete(item, "Skipped: another dispatch owns the review"); return; }
                try { await _activateGuided(item, request).ConfigureAwait(false); }
                catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { _guided.CloseReview(item); }
                catch (Exception ex) { _guided.Complete(item, "Skipped/refused: " + TuiHelpers.ErrorSnippet(ex.Message)); }
            }
            finally
            {
                Interlocked.Decrement(ref _guidedWork);
                await _runOnUi(() => { if (!_disposed && !_lifetimeToken.IsCancellationRequested && Visible) RefreshGuided(); }).ConfigureAwait(false);
            }
        }, "issues.guided-next");
    }

    private void StopGuided()
    {
        _guidedRequests.Cancel();
        _guided?.Stop();
        RefreshGuided();
    }

    private bool RefuseDuringGuidedReview()
    {
        if (!GuidedBusy) return false;
        _status.Text = "Finish or Stop the guided review first";
        return true;
    }

    internal void RefreshGuided()
    {
        var visible = SequenceActive;
        _next.Visible = _skip.Visible = _stop.Visible = visible;
        _guide.Enabled = CanStartGuided;
        _resetChoices.Enabled = !GuidedBusy && !_preferenceBusy && SelectionCurrent;
        _cleanupFilter.Enabled = !GuidedBusy && !_preferenceBusy && SelectionCurrent;
        _updateStatus.Text = _guided is { HasSequence: true } ? _guided.Summary : _availability?.Summary ?? ChangesQueueProjection.UpdatesNotChecked;
        UpdateDetail();
        ApplyToolbarLayout();
    }

    private void ApplyToolbarLayout()
    {
        var narrow = Frame.Width < 100;
        _guide.X = narrow ? 0 : Pos.Right(_resetChoices) + 1;
        _guide.Y = narrow ? Pos.AnchorEnd(1) : 0;
        _cleanupFilter.X = narrow ? Pos.Right(_resetChoices) + 1 : Pos.Right(_guide) + 1;
        _next.X = narrow && _guide.Visible ? Pos.Right(_guide) + 1 : 0;
    }

    private void CleanupSelected()
    {
        if (RefuseDuringGuidedReview() || _preferenceBusy) return;
        if (_rows.ElementAtOrDefault(_table.GetSelectedRow())?.ExactEntry?.Cleanup is not null) ActivateOperation();
        else ShowCleanup();
    }

    internal void ShowCleanup(OperationSubject? selected = null)
    {
        if (RefuseDuringGuidedReview() || _preferenceBusy || !SelectionCurrent) return;
        _copies.Retire(); CancelSelectedRequest();
        _cleanupOnly = !_cleanupOnly || selected is not null;
        _cleanupFilter.Text = "c";
        _cleanupTarget = selected;
        if (selected is null && _table.GetSelectedRow() is var index && index >= 0 && index < _rows.Count)
            _returnSelectionId = _rows[index].Key;
        _returnRowOffset = _table.RowOffset;
        if (_projection is not null) LoadRows(AvailabilityRows());
        ApplyLayout();
    }

    private void ToggleFamily(ChangesQueueRow row, bool? expand)
    {
        _copies.Retire(); CancelSelectedRequest();
        if (expand ?? !_expanded.Contains(row.Key)) _expanded.Add(row.Key); else _expanded.Remove(row.Key);
        _returnSelectionId = row.Key; _returnRowOffset = _table.RowOffset;
        LoadRows(AvailabilityRows());
    }

    private void BackToList()
    {
        var state = _copies.Request?.ReturnState as InlineBrowseReturnState;
        _copies.Retire(); CancelSelectedRequest(); _detailOpen = false;
        if (state is not null)
        {
            var index = _rows.Select((row, index) => (row, index)).FirstOrDefault(item => item.row.Key == state.Selection);
            _restoringRows = true;
            try
            {
                if (index.row is not null) _table.SetSelectedRow(index.index);
                else _missingSelection = state.Selection;
                _table.RowOffset = Math.Clamp(state.TopRow, 0, Math.Max(0, _rows.Count - 1));
            }
            finally { _restoringRows = false; }
        }
        ApplyLayout(); UpdateDetail(); _table.SetFocus();
    }

    private void ApplyLayout()
    {
        ApplyToolbarLayout();
        if (_layoutActive || _loading.Visible || _snapshot is null) return;
        _layoutActive = true;
        try
        {
            var wide = Frame.Width >= 110;
            var showList = (wide || !_detailOpen) && _rows.Count > 0;
            _table.Width = wide ? Dim.Percent(55) : Dim.Fill();
            _detail.X = wide && _rows.Count > 0 ? Pos.Right(_table) + 1 : 0;
            _detail.Width = Dim.Fill();
            _table.Visible = showList;
            _detail.Visible = !_copies.Open && (wide || _detailOpen || _rows.Count == 0);
            _copies.X = _detail.X; _copies.Y = 2; _copies.Width = _detail.Width; _copies.Height = Dim.Fill(2);
            _back.Visible = _detailOpen && !_copies.Open;
            RecomputeColumnWidths();
        }
        finally { _layoutActive = false; }
    }

    // Internals ------------------------------------------------------------

    private bool SelectionCurrent => !_loading.Visible && _cachedQueue?.Evidence.Generation == _catalogGeneration();

    private void ActivateSelectedRow()
    {
        if (!SelectionCurrent || _preferenceBusy) return;
        var index = _table.GetSelectedRow();
        if (_missingSelection is not null) { _missingSelection = null; UpdateDetail(); return; }
        if (index < 0 || index >= _rows.Count) return;
        var row = _rows[index];
        if (row.IsFamily) { ToggleFamily(row, null); return; }
        if (row.Finding?.Subject is FindingRegistry || row.ExactEntry is null)
        { _detailOpen = true; ApplyLayout(); _detail.SetFocus(); return; }
        if (!row.IsExactChild)
        {
            if (Narrow && !_detailOpen) { _detailOpen = true; ApplyLayout(); _detail.SetFocus(); return; }
            ActivateOperation(); return;
        }
        if (_snapshot is not { } snapshot) return;
        var entry = row.ExactEntry;
        var target = InlineCopyRequest.ResolveDisplayed(snapshot, entry.Subject);
        if (target is null) { _detail.Text = "Selected copy is missing or ambiguous; refresh Issues."; return; }
        var state = new InlineBrowseReturnState(row.Key, _table.RowOffset, _expanded.ToArray(), _cleanupOnly, _table);
        var binding = row.Finding is null ? null : FindingBinding.Capture(row.Finding);
        if (row.Finding is null) { ShowCopies(); return; }
        if (binding is null || _validateFinding is null || _runTask is null)
        { _detail.Text = "Finding changed; refresh"; return; }
        CancelSelectedRequest();
        var generation = Interlocked.Read(ref _selectionGeneration);
        var context = _context();
        Interlocked.Increment(ref _selectionWork);
        _runTask(async () =>
        {
            CancellationToken selectedToken = default;
            try
            {
                using var request = _selectionRequests.Begin(_lifetimeToken);
                if (request is null) return;
                selectedToken = request.Token;
                var valid = await _validateFinding(binding, request).ConfigureAwait(false);
                await _runOnUi(() =>
                {
                    if (_disposed || !Visible || !request.IsCurrent || !SelectionCurrent || generation != Interlocked.Read(ref _selectionGeneration)
                        || !ReferenceEquals(snapshot, _snapshot) || context != _context() || _rows.ElementAtOrDefault(_table.GetSelectedRow())?.Key != row.Key) return;
                    if (!valid) { _detailOpen = true; _copies.Retire(); _detail.Text = "Finding changed; refresh"; ApplyLayout(); _detail.SetFocus(); return; }
                    if (request.TryEnterDispatch()) ShowCopies();
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (selectedToken.IsCancellationRequested || _lifetimeToken.IsCancellationRequested || generation != Interlocked.Read(ref _selectionGeneration)) { }
            finally { Interlocked.Decrement(ref _selectionWork); }
        }, "issues.finding-membership");
        void ShowCopies()
        {
            _detailOpen = true;
            _copies.Show(snapshot, target, state, binding);
            _detail.Visible = false;
            ApplyLayout();
        }
    }

    private void ReviewSelected()
    {
        if (_missingSelection is not null || !SelectionCurrent || _preferenceBusy || GuidedBusy) return;
        var index = _table.GetSelectedRow();
        if (index < 0 || index >= _rows.Count || _rows[index].IsFamily) return;
        if (_rows[index].IsExactChild && _rows[index].ExactEntry is { } exact && _snapshot is not null)
        {
            var target = InlineCopyRequest.ResolveDisplayed(_snapshot, exact.Subject);
            _copies.Retire();
            if (target is not null) _reviewCopy?.Invoke(target);
            return;
        }
        ActivateOperation();
    }

    private void ActivateOperation()
    {
        if (!SelectionCurrent || _missingSelection is not null || _preferenceBusy || GuidedBusy) return;
        var row = _table.GetSelectedRow();
        if (row < 0 || row >= _rows.Count || _preferenceBusy) return;
        ClearPreferenceFeedback();
        if (_rows[row].Finding is { Subject: FindingRegistry }) return;
        if (_rows[row].PackageCheck is { } package)
        {
            if (ReferenceEquals(package, _packageCheck())) _onPackageCheck?.Invoke(package);
            return;
        }
        if (_rows[row].ExactEntry is { } entry)
        {
            if (_activateEntryAsync is not null && _runTask is not null)
            {
                var generation = Interlocked.Increment(ref _selectionGeneration);
                _selectionRequests.Cancel();
                Interlocked.Increment(ref _selectionWork);
                _runTask(async () =>
                {
                    try
                    {
                        if (Interlocked.Read(ref _selectionGeneration) != generation || !Visible || _lifetimeToken.IsCancellationRequested) return;
                        using var request = _selectionRequests.Begin(_lifetimeToken);
                        if (request is null) return;
                        try
                        {
                            if (request.IsCurrent) await _activateEntryAsync(entry, request).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
                    }
                    finally { Interlocked.Decrement(ref _selectionWork); }
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
            var selectedFinding = row >= 0 && row < _rows.Count ? _rows[row].Finding : null;
            _keepBoth.Enabled = SelectionCurrent && !_preferenceBusy && !GuidedBusy && _rows.ElementAtOrDefault(row)?.IsFamily == true && selectedFinding?.Difference is not null;
            _keepBoth.Text = selectedFinding?.Intentional == true ? ActionLabels.RestoreSuggestions.ButtonText : ActionLabels.KeepBoth.ButtonText;
            _reviewFinding.Enabled = SelectionCurrent && _missingSelection is null && row >= 0 && row < _rows.Count && !_preferenceBusy && !GuidedBusy && _rows[row].ExactEntry is not null;
            if (_copies.Open) return;
            var detail = row >= 0 && row < _rows.Count
                ? ChangesQueueBuilder.Render(_rows[row], token, _showDetails, _homeDirectory)
                : "## Issues\n\n" + (_partial
                    ? "No local problems found in checked locations. Discovery is incomplete.\n"
                    : $"No local problems found. {ChangesQueueProjection.UpdatesNotChecked}.\n");
            if (_rows.Count > 0 || _partial) detail += $"\n{ChangesQueueProjection.UpdatesNotChecked}.\n";
            if (_availability is not null)
                detail = detail.Replace(ChangesQueueProjection.UpdatesNotChecked, _availability.Summary, StringComparison.Ordinal);
            if (_preferenceNotice is not null && selectedFinding?.Id == _preferenceNoticeFamily)
            {
                var notice = MaintenanceQueueFormatter.RenderFields([("Preference operation details", _preferenceNotice)], token, 256 * 1024);
                detail = (notice.OmittedFields == 0 ? notice.Text
                    : "Preference operation details exceed the 256 KiB display bound. Use **Choices (K)** to inspect the exact preference store and supported recovery route. Raw state remains preserved.") + "\n\n" + detail;
            }
            detail += "\nPackage Problems come only from the last explicit check of one exact copy. Other copies remain package not checked for this inventory capture; Reviews stay in Installed/full report. Queue JSON contains captured scan findings only.\n";
            detail += "\n**C:** Check updates / cancel. **Enter / u:** review an exact finding; Enter expands a family or opens child details. **r:** refresh local inventory. Routine inspection is in **Installed (2)**.\n";
            if (_showDetails) detail += _coverageDetail;
            else if (_coverageDetail.Length > 0) detail += "\nDiscovery/checks incomplete. Press **F2** for coverage details.\n";
            if (OmittedRows > 0) detail += $"\n{OmittedRows} additional findings omitted by the row limit. Counts include these findings; Installed retains per-copy update results; queue --json makes a fresh local capture.\n";
            token.ThrowIfCancellationRequested();
            if (SequenceActive)
                detail = "## Guided per-item reviews\n\n" + MarkdownTableFormatter.FormatCodeSpan(_guided!.Summary)
                    + "\n\nOnly the first 128 eligible captured findings are included. Review next prepares one item after prior reconciliation; Skip leaves it untouched. Stop prevents unstarted work. Each source/retention decision remains separate.\n\n"
                    + string.Join("\n\n", _guided!.Outcomes.Select(outcome => MarkdownTableFormatter.FormatCodeSpan(outcome.Path + ": " + outcome.Status + (outcome.SavedOperationId is null ? "" : "; saved operation " + outcome.SavedOperationId + " — F4 History / Inspect"))))
                    + "\n\n---\n\n" + detail;
            _detail.Text = _missingSelection is null ? detail : "## Selection changed\n\nThe selected finding is missing or changed. Choose a visible exact item deliberately before review.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private string BuildStatusText()
    {
        if (_projection is null) return string.Empty;
        return " " + (_partial ? "Scan incomplete · " : "") + Counts.Replace(ChangesQueueProjection.UpdatesNotChecked, _availability?.Summary ?? ChangesQueueProjection.UpdatesNotChecked, StringComparison.Ordinal)
            + (_snapshot?.IntentionalDifferences.CapacityNotice is not null ? (_snapshot.IntentionalDifferences.NearCapacity ? " · Choices >=75%: K" : " · Choices backup: K") : "")
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
            if (_detailOpen || _copies.Open) BackToList(); else _onLeaveTab();
            return;
        }
        if (key.AsRune.Value == 'K') { key.Handled = true; ResetIntentional(); return; }
        if (key.AsRune.Value == 'k') { key.Handled = true; SetIntentional(); return; }
        if (key.AsRune.Value == 'C' && _onCheckUpdates is not null)
        {
            key.Handled = true;
            ClearPreferenceFeedback();
            _onCheckUpdates();
            return;
        }
        if (key.KeyCode == KeyCode.Enter || key.AsRune.Value is 'u' or 'U')
        {
            key.Handled = true;
            if (!_loading.Visible) { if (key.KeyCode == KeyCode.Enter) ActivateSelectedRow(); else ReviewSelected(); }
        }
        if (key.AsRune.Value == 'g')
        {
            if (CanStartGuided) { key.Handled = true; StartGuided(); }
            return;
        }
        if (SequenceActive && key.AsRune.Value is 'n' or 's' or 'x')
        {
            key.Handled = true;
            switch (key.AsRune.Value)
            {
                case 'n': ReviewNext(); break;
                case 's': _guided!.Skip(); RefreshGuided(); break;
                case 'x': StopGuided(); break;
            }
            return;
        }
        if (key.AsRune.Value == 'c')
        {
            key.Handled = true;
            CleanupSelected();
            return;
        }
        if (key.KeyCode is KeyCode.CursorRight or KeyCode.CursorLeft)
        {
            var index = _table.GetSelectedRow();
            if (index >= 0 && index < _rows.Count && _rows[index].IsFamily)
            { key.Handled = true; ToggleFamily(_rows[index], key.KeyCode == KeyCode.CursorRight); }
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
        var package = _packageCheck();
        _packageProblems = package is { Problems: > 0 } && _snapshot is not null
            && package.Matches(_snapshot, _catalogGeneration(), package.Request.Id, package.Request.Target.ResolvedPath) ? 1 : 0;
        var packageRows = _packageProblems == 0 ? Array.Empty<ChangesQueueRow>() : new[]
        {
            new ChangesQueueRow("Problem", InstalledFilters.ChoiceText(package!.Request.Target.Name + " · missing root reference · " + package.Request.Target.ResolvedPath, ChangesQueueBuilder.MaxTitleCharacters),
                [new MaintenanceQueueEntry(package.Key, "package-reference", package.Request.Target.Name,
                    OperationSubject.From(package.Request.Target), [package.Request.Target.ResolvedPath], "explicit-package-check", "Captured root reference missing", "inspect-report", false, package.Request.Target.Pinned)])
            { PackageCheck = package }
        };
        var updates = (_availability is null || _snapshot is null ? [] : _snapshot.Skills.Select(skill => (Skill: skill, Check: _availability.Get(skill))))
            .Where(item => item.Check.Status == UpdateAvailabilityStatus.Available)
            .Select(item => new ChangesQueueRow("Updates", item.Skill.Name + " · " + DisplayPath.Abbreviate(item.Skill.ResolvedPath, _homeDirectory),
                [new MaintenanceQueueEntry("update-" + UpdateAvailability.Address(item.Skill), "upstream-update", item.Skill.Name,
                    OperationSubject.From(item.Skill), [item.Skill.ResolvedPath], "gh-dry-run", item.Check.Reason,
                    "review-update", false, item.Skill.Pinned)])
            { Availability = item.Check }).ToArray();
        _availabilityOmittedRows = Math.Max(0, _projection.Rows.Count + updates.Length + packageRows.Length - ChangesQueueBuilder.MaxRows);
        if (_cleanupTarget is { } selectedTarget)
        {
            var matches = _projection.Rows.SelectMany(row => row.Entries.Where(entry => (entry.Cleanup is not null || row.IsFamily)
                && entry.Subject.InstallationId == selectedTarget.InstallationId && entry.Subject.Path == selectedTarget.Path)
                .Select(entry => (row, entry))).Take(2).ToArray();
            _returnSelectionId = "unavailable-cleanup-target:" + selectedTarget.InstallationId;
            if (matches.Length == 1)
            {
                var match = matches[0];
                if (match.row.IsFamily) { _expanded.Add(match.row.Key); _returnSelectionId = ChildRow(match.row, match.entry, 0).Key; }
                else _returnSelectionId = match.row.Key;
            }
        }
        var baseRows = _projection.Rows.Where(row => row.Kind == "Problem").Concat(packageRows).Concat(updates)
            .Concat(_projection.Rows.Where(row => row.Kind != "Problem"));
        var rows = new List<ChangesQueueRow>();
        foreach (var row in baseRows)
        {
            if (_cleanupOnly && row.ExactEntry?.Cleanup is null && (!row.IsFamily
                || !row.Entries.Any(entry => entry.Cleanup is not null || _cleanupTarget is { } context
                    && entry.Subject.InstallationId == context.InstallationId && entry.Subject.Path == context.Path))) continue;
            rows.Add(row);
            if (row.IsFamily && _expanded.Contains(row.Key))
                foreach (var child in row.Entries.Select((entry, index) => (entry, index)))
                    rows.Add(ChildRow(row, child.entry, child.index));
        }
        _availabilityOmittedRows = Math.Max(0, rows.Count - ChangesQueueBuilder.MaxRows);
        return rows.Take(ChangesQueueBuilder.MaxRows).ToArray();
    }

    private static ChangesQueueRow ChildRow(ChangesQueueRow parent, MaintenanceQueueEntry entry, int index) =>
        new("Copy", "  " + parent.LocationLabels.ElementAtOrDefault(index), [entry])
        { Finding = parent.Finding, IsExactChild = true, ParentKey = parent.Key };

    internal void RefreshAvailability()
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested) return;
        _copies.ValidateObservation();
        if (_availability is not null)
        {
            _checkUpdates.Text = _availability.Running ? ActionLabels.CancelCheck.ButtonText : ActionLabels.CheckUpdates.ButtonText;
            _checkUpdates.Enabled = !_availability.Stopping;
        }
        _updateStatus.Text = _guided is { HasSequence: true } ? _guided.Summary : _availability?.Summary ?? ChangesQueueProjection.UpdatesNotChecked;
        _reviewFinding.Enabled = !_loading.Visible && !_preferenceBusy && _rows.Count > 0;
        if (_loading.Visible || _projection is null) return;
        var selected = _table.GetSelectedRow();
        var id = selected >= 0 && selected < _rows.Count ? _rows[selected].Key : null;
        var offset = _table.RowOffset;
        var detailOffset = _detail.Viewport.Location;
        var rows = AvailabilityRows();
        // Progress/reasons can change without changing any findings.
        if (!_rows.Select(row => row.Key).SequenceEqual(rows.Select(row => row.Key)))
        {
            var hadFocus = _table.HasFocus;
            _restoringRows = true;
            _rows = rows;
            _table.Table = new EnumerableTableSource<ChangesQueueRow>(_rows, new Dictionary<string, Func<ChangesQueueRow, object>>
            { ["Kind"] = row => row.Kind, ["Title"] = row => row.Title });
            var restored = rows.Select((row, i) => (row, i)).FirstOrDefault(item => item.row.Key == id);
            _missingSelection = id is not null && restored.row is null ? id : null;
            _table.SetSelectedRow(restored.row is null ? Math.Clamp(selected, 0, Math.Max(0, rows.Count - 1)) : restored.i);
            _table.RowOffset = offset;
            _restoringRows = false;
            RecomputeColumnWidths();
            if (hadFocus && rows.Count == 0) _detail.SetFocus();
        }
        else _rows = rows;
        _reviewFinding.Enabled = !_preferenceBusy && _rows.Count > 0;
        _table.Update();
        UpdateDetail();
        var currentRow = _table.GetSelectedRow();
        if (id is not null && currentRow >= 0 && currentRow < _rows.Count && _rows[currentRow].Key == id)
            TuiHelpers.RestoreMarkdownScroll(_detail, detailOffset);
        _status.Text = BuildStatusText();
        RefreshGuided();
        _onStateChange?.Invoke();
    }

    internal void CancelPendingLoad()
    {
        _copies.Retire();
        CancelSelectedRequest();
        _guidedRequests.Cancel();
        Interlocked.Increment(ref _loadGeneration);
        _loadCancellations.Cancel();
        SetLoading(false);
    }

    private void SetLoading(bool loading)
    {
        _loading.SetContentLoading(loading, _table, _table, _detail);
        if (loading) _loading.SetFocus();
        else ApplyLayout();
        _keepBoth.Enabled = !loading && !_preferenceBusy && !GuidedBusy && _rows.ElementAtOrDefault(_table.GetSelectedRow()) is { IsFamily: true, Finding.Difference: not null };
        _spinner.Visible = loading;
        _spinner.AutoSpin = loading;
    }

    private void SetIntentional()
    {
        var selected = _rows.ElementAtOrDefault(_table.GetSelectedRow())?.Finding;
        if (RefuseDuringGuidedReview() || !SelectionCurrent || _rows.ElementAtOrDefault(_table.GetSelectedRow())?.IsFamily != true
            || _preferenceBusy || selected?.Difference is null || _setIntentional is null || _runTask is null) return;
        CancelSelectedRequest();
        var generation = Interlocked.Read(ref _selectionGeneration);
        _preferenceBusy = true;
        _keepBoth.Enabled = _reviewFinding.Enabled = false;
        _status.Text = selected.Intentional ? " Restoring suggestions…" : " Saving intentional difference…";
        _runTask(async () =>
        {
            using var request = _selectionRequests.Begin(_lifetimeToken);
            try
            {
                if (request is null || !request.IsCurrent || generation != Interlocked.Read(ref _selectionGeneration) || !Visible) return;
                string message;
                try { message = await _setIntentional(selected, request).ConfigureAwait(false); }
                catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { return; }
                catch (Exception ex) { message = "Preference operation did not complete. " + ex.Message; }
                await _runOnUi(() =>
                {
                    if (_disposed || _lifetimeToken.IsCancellationRequested || !request.IsCurrent || !Visible || generation != Interlocked.Read(ref _selectionGeneration)) return;
                    _cachedQueue = null;
                    var expectedLoad = Interlocked.Read(ref _loadGeneration) + 1;
                    _runTask(async () =>
                    {
                        await LoadAsync().ConfigureAwait(false);
                        await _runOnUi(() =>
                        {
                            if (_disposed || _lifetimeToken.IsCancellationRequested || !Visible || Interlocked.Read(ref _loadGeneration) != expectedLoad
                                || _rows.ElementAtOrDefault(_table.GetSelectedRow())?.Finding?.Id != selected.Id) return;
                            _preferenceNoticeFamily = selected.Id;
                            _preferenceNotice = message;
                            UpdateDetail();
                        }).ConfigureAwait(false);
                    }, "issues.preference-refresh");
                }).ConfigureAwait(false);
            }
            finally
            {
                await _runOnUi(() =>
                {
                    if (_disposed || _lifetimeToken.IsCancellationRequested) return;
                    _preferenceBusy = false;
                    _status.Text = BuildStatusText();
                    _reviewFinding.Enabled = !_loading.Visible && !_preferenceBusy && _rows.Count > 0;
                    UpdateDetail();
                }).ConfigureAwait(false);
            }
        }, "issues.intentional-difference");
    }

    private void CancelSelectedRequest()
    {
        Interlocked.Increment(ref _selectionGeneration);
        _selectionRequests.Cancel();
        ClearPreferenceFeedback();
    }

    private void ClearPreferenceFeedback()
    {
        var redraw = _preferenceNotice is not null;
        _preferenceNotice = _preferenceNoticeFamily = null;
        if (_projection is not null) _status.Text = BuildStatusText();
        if (redraw && !_disposed) UpdateDetail();
    }

    private void ResetIntentional()
    {
        if (RefuseDuringGuidedReview() || !SelectionCurrent || _preferenceBusy || _resetIntentional is null || _runTask is null) return;
        CancelSelectedRequest();
        var generation = Interlocked.Read(ref _selectionGeneration);
        _preferenceBusy = true;
        RefreshGuided();
        _runTask(async () =>
        {
            try
            {
                if (_disposed || _lifetimeToken.IsCancellationRequested || !Visible || generation != Interlocked.Read(ref _selectionGeneration)) return;
                using var request = _selectionRequests.Begin(_lifetimeToken);
                if (request is null) return;
                try
                {
                    await _resetIntentional(request).ConfigureAwait(false);
                    await _runOnUi(() =>
                    {
                        if (_disposed || _lifetimeToken.IsCancellationRequested || !Visible || !request.IsCurrent) return;
                        _cachedQueue = null;
                        _runTask(LoadAsync, "issues.reset-refresh");
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
            }
            finally
            {
                await _runOnUi(() => { if (!_disposed && !_lifetimeToken.IsCancellationRequested) { _preferenceBusy = false; RefreshGuided(); } }).ConfigureAwait(false);
            }
        }, "issues.reset-choices");
    }
}
