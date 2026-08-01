using System.IO;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

/// Embedded tab 3 — the Changes workspace maintenance queue.
/// Shows a prioritised list of pending work (Update · Cleanup · Diagnostics)
/// and hands off to the appropriate specialist view when a row is activated.
///
/// Layout: full-width table over the whole pane; one compact status line at the
/// bottom. No split detail pane — the row title is the summary.
internal sealed class ChangesTabView : FrameView
{
    private const int KindColumnWidth = 11;
    private readonly Func<Action, Task> _runOnUi;
    private readonly Func<Task<InventorySnapshot>> _snapshotLoader;
    private readonly Action _onActivateUpdates;
    private readonly Action _onActivateCleanup;
    private readonly Action _onActivateDoctor;
    private readonly Action _onLeaveTab;
    private readonly Action? _onStateChange;

    private readonly TableView _table;
    private readonly Markdown _detail;
    private readonly Label _status;
    private IReadOnlyList<ChangesQueueRow> _rows = Array.Empty<ChangesQueueRow>();
    private long _loadGeneration;

    internal ChangesTabView(
        Func<Action, Task> runOnUi,
        Func<Task<InventorySnapshot>> snapshotLoader,
        Action onActivateUpdates,
        Action onActivateCleanup,
        Action onActivateDoctor,
        Action onLeaveTab,
        Action? onStateChange = null)
    {
        _runOnUi = runOnUi;
        _snapshotLoader = snapshotLoader;
        _onActivateUpdates = onActivateUpdates;
        _onActivateCleanup = onActivateCleanup;
        _onActivateDoctor = onActivateDoctor;
        _onLeaveTab = onLeaveTab;
        _onStateChange = onStateChange;

        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;

        _table = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(48),
            Height = Dim.Fill(1),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(_table);
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
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Text = " loading…",
        };

        TuiHelpers.ApplyScheme(SchemeNames.Base, this, _table, _detail, _status);

        _table.KeyDown += OnTableKeyDown;

        Add(_table, _detail, _status);
    }

    /// Load the maintenance queue from the inventory snapshot.
    internal async Task LoadAsync()
    {
        var gen = Interlocked.Increment(ref _loadGeneration);
        Visible = true;
        _status.Text = " loading inventory…";

        try
        {
            var snapshot = await _snapshotLoader().ConfigureAwait(false);
            // Only queue items backed by real pending state.
            // Update availability is unknown until the user runs a dry-run; Doctor
            // is always accessible via 'd'. Neither belongs in the pending queue.
            var cleanupItems = CleanupClassifier
                            .Classify(snapshot, snapshot.ScannedRoots)
                            .ToArray();
            var cleanup = cleanupItems.Select(c => new ChangesQueueRow(
                Kind: "Cleanup",
                Title: $"{TuiHelpers.ShortKind(c.Kind)}  {Path.GetFileName(c.Path)}",
                Detail: CleanupScreen.RenderDetail(c)));
            var summary = DescribeWorkspaceSummary(snapshot, cleanupItems.Length > 0);

            await _runOnUi(() =>
            {
                if (Interlocked.Read(ref _loadGeneration) != gen) return;
                LoadRows(
                [
                    .. cleanup,
                ],
                summary);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _runOnUi(() =>
            {
                if (Interlocked.Read(ref _loadGeneration) != gen) return;
                _status.Text = $" load failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
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
        string summary)
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
        style.ExpandLastColumn = false;
        var kindStyle = style.GetOrCreateColumnStyle(0);
        kindStyle.MinWidth = KindColumnWidth;
        kindStyle.MaxWidth = KindColumnWidth;

        RecomputeColumnWidths();
        _table.Update();
        _status.Text = BuildStatusText(summary);
        UpdateDetail();

        if (_rows.Count > 0)
        {
            _table.SetSelectedRow(0);
            _table.SetFocus();
        }

        _onStateChange?.Invoke();
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
        var kind = _rows[row].Kind;
        switch (kind)
        {
            case "Update": _onActivateUpdates(); break;
            case "Cleanup": _onActivateCleanup(); break;
            case "Diagnostics": _onActivateDoctor(); break;
        }
    }

    private void UpdateDetail()
    {
        var row = _table.GetSelectedRow();
        _detail.Text = row >= 0 && row < _rows.Count
            ? _rows[row].Detail ?? RenderDetail(_rows[row])
            : "## Changes\n\n_No pending work._";
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
            ? Math.Max(32, _table.Viewport.Width - 4)
            : 48;
        var titleWidth = Math.Max(20, available - KindColumnWidth);
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
    }

    private static string DescribeWorkspaceSummary(InventorySnapshot snapshot, bool hasPendingCleanup)
    {
        // Only show health concerns if they translate to actionable queue rows.
        // Health flags by themselves matter for the Installed detail pane but
        // don't belong in Changes unless cleanup items are actually queued.
        // This summary describes cleanup state only — update availability is
        // unknown until dry-run and diagnostics are always accessible via 'd'.
        if (hasPendingCleanup)
        {
            if (snapshot.Skills.Any(skill => !skill.IsSymlinked && skill.Validity != ValidityState.Valid))
                return "Needs review";
            if (snapshot.Skills.Any(skill => skill.IsSymlinked))
                return "Symlink";
            return "Maintenance pending";
        }

        return "No cleanup items";
    }
}
