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
/// Layout: full-width table over the whole pane; bottom status bar with
/// shortcuts. No split detail pane — the row title is the summary.
internal sealed class ChangesTabView : FrameView
{
    private readonly Func<Action, Task> _runOnUi;
    private readonly Func<Task<InventorySnapshot>> _snapshotLoader;
    private readonly Action _onActivateUpdates;
    private readonly Action _onActivateCleanup;
    private readonly Action _onActivateDoctor;
    private readonly Action _onLeaveTab;

    private readonly TableView _table;
    private readonly Label _status;
    private readonly StatusBar _statusBar;

    private IReadOnlyList<ChangesQueueRow> _rows = Array.Empty<ChangesQueueRow>();
    private long _loadGeneration;

    internal ChangesTabView(
        Func<Action, Task> runOnUi,
        Func<Task<InventorySnapshot>> snapshotLoader,
        Action onActivateUpdates,
        Action onActivateCleanup,
        Action onActivateDoctor,
        Action onLeaveTab)
    {
        _runOnUi = runOnUi;
        _snapshotLoader = snapshotLoader;
        _onActivateUpdates = onActivateUpdates;
        _onActivateCleanup = onActivateCleanup;
        _onActivateDoctor = onActivateDoctor;
        _onLeaveTab = onLeaveTab;

        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;

        _table = new TableView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(_table);
        TuiHelpers.ConfigureTableChrome(_table);

        _status = new Label
        {
            X = 0, Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Text = " loading…",
        };

        _statusBar = new StatusBar(TuiHelpers.WithMarkdownShortcuts(
        [
            new Shortcut { Key = Key.Enter, Title = "Enter", HelpText = "Open" },
            new Shortcut { Key = Key.Esc,   Title = "Esc",   HelpText = "Back" },
        ], includeOpenLink: false));

        TuiHelpers.ApplyScheme(SchemeNames.Base, this, _table, _status, _statusBar);

        _table.KeyDown += OnTableKeyDown;

        Add(_table, _status, _statusBar);
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
            var cleanup = CleanupClassifier
                            .Classify(snapshot, snapshot.ScannedRoots)
                            .Select(c => $"{TuiHelpers.ShortKind(c.Kind)}  {Path.GetFileName(c.Path)}");

            await _runOnUi(() =>
            {
                if (Interlocked.Read(ref _loadGeneration) != gen) return;
                Load(updates: [], cleanup, diagnostics: []);
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
        IEnumerable<string> diagnostics)
    {
        _rows = ChangesQueueBuilder.BuildForTests(updates, cleanup, diagnostics);

        var source = new EnumerableTableSource<ChangesQueueRow>(
            _rows,
            new Dictionary<string, Func<ChangesQueueRow, object>>
            {
                ["Kind"]  = r => r.Kind,
                ["Title"] = r => r.Title,
            });
        _table.Table = source;

        var style = _table.Style;
        style.ExpandLastColumn = true;
        var kindStyle = style.GetOrCreateColumnStyle(0);
        kindStyle.MinWidth = 11;
        kindStyle.MaxWidth = 14;

        _table.Update();
        _status.Text = _rows.Count == 0
            ? " no pending changes — everything looks good"
            : $" {_rows.Count} item(s) pending · Enter to open · Esc to go back";

        if (_rows.Count > 0)
            _table.SetFocus();
    }

    // Tests only -----------------------------------------------------------

    internal int RowCountForTests => _rows.Count;
    internal string StatusTextForTests => _status.Text.ToString();

    // Internals ------------------------------------------------------------

    private void ActivateSelectedRow()
    {
        var row = _table.GetSelectedRow();
        if (row < 0 || row >= _rows.Count) return;
        var kind = _rows[row].Kind;
        switch (kind)
        {
            case "Update":      _onActivateUpdates(); break;
            case "Cleanup":     _onActivateCleanup(); break;
            case "Diagnostics": _onActivateDoctor();  break;
        }
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
}
