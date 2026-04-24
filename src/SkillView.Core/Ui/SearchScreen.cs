using System.Globalization;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Phase 3 dedicated Search screen: owner / limit / page inputs, results
/// table, preview pane, install-from-search handoff (the install dialog
/// itself is Phase 4 — here we stage the pick and surface it through
/// `LastInstallRequest`).
public sealed class SearchScreen
{
    private readonly IApplication _app;
    private readonly GhSkillSearchService _search;
    private readonly GhSkillPreviewService _preview;
    private readonly Logger _logger;
    private readonly string _ghPath;
    private readonly CapabilityProfile _capabilities;

    private TextField? _queryField;
    private TextField? _ownerField;
    private NumericUpDown<int>? _limitUpDown;
    private TableView? _resultsTable;
    private Markdown? _previewPane;
    private FrameView? _previewFrame;
    private Label? _statusLabel;
    private SpinnerView? _spinner;

    private List<SearchResultSkill> _results = new();

    public InstallRequest? LastInstallRequest { get; private set; }

    public SearchScreen(
        IApplication app,
        GhSkillSearchService search,
        GhSkillPreviewService preview,
        Logger logger,
        string ghPath,
        CapabilityProfile capabilities)
    {
        _app = app;
        _search = search;
        _preview = preview;
        _logger = logger;
        _ghPath = ghPath;
        _capabilities = capabilities;
    }

    public void Show()
    {
        using var window = new Window
        {
            Title = "Search — gh skill search",
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        var queryLabel = new Label { Text = "Query :", X = 0, Y = 0 };
        _queryField = new TextField { X = 8, Y = 0, Width = Dim.Percent(40), Text = string.Empty };
        TuiHelpers.ConfigureTextInput(_queryField, "Base");

        var ownerLabel = new Label { Text = "Owner :", X = Pos.Right(_queryField) + 2, Y = 0 };
        _ownerField = new TextField { X = Pos.Right(ownerLabel) + 1, Y = 0, Width = Dim.Percent(20), Text = string.Empty };
        TuiHelpers.ConfigureTextInput(_ownerField, "Base");

        var limitLabel = new Label { Text = "Limit :", X = Pos.Right(_ownerField) + 2, Y = 0 };
        _limitUpDown = new NumericUpDown<int>
        {
            X = Pos.Right(limitLabel) + 1,
            Y = 0,
            Value = GhSkillSearchService.DefaultLimit,
            Increment = 10,
        };
        _limitUpDown.ValueChanging += (_, e) =>
        {
            if (e.NewValue < 1 || e.NewValue > 200) e.Handled = true;
        };

        var hint = new Label
        {
            Text = "Enter search  ·  p/v preview  ·  i install selected  ·  Esc/q close",
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
        };

        _resultsTable = new TableView
        {
            X = 0,
            Y = 3,
            Width = Dim.Percent(55),
            Height = Dim.Fill(3),
            FullRowSelect = true,
        };
        TuiHelpers.ConfigureTableKeyBindings(_resultsTable);
        TuiHelpers.ConfigureTableScheme(_resultsTable);
        // Enter via KeyDown workaround — same as SkillViewApp
        // TODO(tg2): remove once upstream Enter→CellActivated is reliable
        _resultsTable.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter && !key.Handled)
            {
                key.Handled = true;
                _ = PreviewSelectedAsync();
            }
        };
        _resultsTable.CellActivated += (_, _) => _ = PreviewSelectedAsync();
        _resultsTable.SelectedCellChanged += (_, _) => UpdatePreviewPlaceholder();

        _previewFrame = new FrameView
        {
            Title = "Preview",
            X = Pos.Right(_resultsTable),
            Y = 3,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
        };
        _previewPane = new Markdown
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "(no selection)",
        };
        TuiHelpers.ConfigureMarkdownPane(_previewPane, "Base");
        _previewFrame.Add(_previewPane);

        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(10),
            Text = " ready — type a query and press Enter",
        };
        _spinner = new SpinnerView
        {
            X = Pos.AnchorEnd(10),
            Y = Pos.AnchorEnd(2),
            Width = 1,
            Height = 1,
            Visible = false,
            AutoSpin = false,
        };

        var statusBar = new StatusBar(
        [
            new Shortcut { Title = "/", HelpText = "Query" },
            new Shortcut { Title = "Enter", HelpText = "Preview" },
            new Shortcut { Title = "i", HelpText = "Install" },
            new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Back" },
            new Shortcut { Title = "q", HelpText = "Quit" },
        ]);

        TuiHelpers.ApplyScheme("Base",
            window,
            queryLabel, _queryField,
            ownerLabel, _ownerField,
            limitLabel, _limitUpDown,
            hint, _resultsTable, _previewFrame, _previewPane, _statusLabel, _spinner, statusBar);

        window.Add(
            queryLabel, _queryField,
            ownerLabel, _ownerField,
            limitLabel, _limitUpDown,
            hint,
            _resultsTable, _previewFrame,
            _statusLabel, _spinner, statusBar);

        _queryField.KeyDown += (_, key) =>
        {
            var isSubmit = key.KeyCode == KeyCode.Enter
                || key.KeyCode == (KeyCode.J | KeyCode.CtrlMask);
            if (isSubmit)
            {
                key.Handled = true;
                _ = RunSearchAsync();
            }
        };

        window.KeyDown += (_, key) =>
        {
            // Handle Enter at dialog level BEFORE the view hierarchy.
            // Same TG2 v2 RC4 issue as SkillViewApp — internal focused subviews
            // (e.g. ScrollBar) have Enter→Command.Accept and steal Enter before
            // the TableView sees it. P/V bypass because they're not base View bindings.
            // Ctrl+J is an alternative for Warp terminal.
            // TODO(tg2): remove once upstream Enter dispatch to TableView is fixed
            var isEnterLike = key.KeyCode == KeyCode.Enter
                || key.KeyCode == (KeyCode.J | KeyCode.CtrlMask);
            if (isEnterLike && !key.Handled)
            {
                if (_resultsTable.HasFocus)
                {
                    key.Handled = true;
                    _ = PreviewSelectedAsync();
                    return;
                }
                else if (_queryField.HasFocus || _ownerField.HasFocus || _limitUpDown.HasFocus)
                {
                    key.Handled = true;
                    _ = RunSearchAsync();
                    return;
                }
            }

            // Right arrow on table → preview
            if (key.KeyCode == KeyCode.CursorRight && !key.Handled && _resultsTable.HasFocus)
            {
                key.Handled = true;
                _ = PreviewSelectedAsync();
                return;
            }

            if (_queryField.HasFocus || _ownerField.HasFocus || _limitUpDown.HasFocus) return;
            var rune = key.AsRune.Value;
            if (key.KeyCode == KeyCode.Esc || rune == 'q' || rune == 'Q')
            {
                _app.RequestStop();
                key.Handled = true;
            }
            else if (rune == 'i' || rune == 'I')
            {
                StageInstall();
                key.Handled = true;
            }
            else if (rune == '/' )
            {
                _queryField.SetFocus();
                _queryField.SelectAll();
                key.Handled = true;
            }
        };

        RefreshResultsTable();
        _queryField.SetFocus();
        _app.Run(window);
    }

    private async Task RunSearchAsync()
    {
        var query = _queryField?.Text.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            SetStatus("enter a query");
            return;
        }
        if (!_capabilities.SkillSubcommandPresent)
        {
            SetStatus("gh skill not available");
            return;
        }

        var owner = _ownerField?.Text.Trim();
        var limit = _limitUpDown?.Value ?? GhSkillSearchService.DefaultLimit;

        SetBusy($"searching '{query}'…");
        try
        {
            var response = await _search.SearchAsync(
                _ghPath,
                query,
                _capabilities,
                new GhSkillSearchService.Options(
                    Owner: string.IsNullOrEmpty(owner) ? null : owner,
                    Limit: limit)
            ).ConfigureAwait(false);
            Invoke(() =>
            {
                _results = response.Results.ToList();
                RefreshResultsTable();
                _resultsTable?.SetFocus();
                if (_previewPane is not null)
                {
                    _previewPane.Text = _results.Count == 0 ? "(no selection)" : TuiHelpers.PreviewHint;
                }
                if (_previewFrame is not null)
                {
                    _previewFrame.Title = "Preview";
                }
                if (!response.Succeeded)
                {
                    var snippet = TuiHelpers.ErrorSnippet(response.ErrorMessage);
                    SetStatus(snippet.Length > 0
                        ? $"search failed (exit {response.ExitCode}): {snippet}"
                        : $"search failed (exit {response.ExitCode}) — see logs");
                }
                else
                {
                    SetStatus(_results.Count == 0
                        ? "no matches"
                        : $"{_results.Count} result(s) — Enter, p, or v to preview; i to install");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error("search", ex.Message);
            var snippet = TuiHelpers.ErrorSnippet(ex.Message);
            Invoke(() => SetStatus(snippet.Length > 0
                ? $"search failed: {snippet}"
                : "search failed — see logs"));
        }
        finally
        {
            Invoke(ClearBusy);
        }
    }

    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(30);

    private async Task PreviewSelectedAsync()
    {
        if (_resultsTable is null || _results.Count == 0) return;
        var row = _resultsTable.SelectedRow;
        if (row < 0 || row >= _results.Count)
        {
            _logger.Debug("preview", $"SelectedRow={row} out of range (count={_results.Count})");
            return;
        }

        var pick = _results[row];
        var repo = pick.Repo ?? string.Empty;
        if (string.IsNullOrEmpty(repo))
        {
            SetStatus("no repo on selected row");
            return;
        }

        SetBusy($"preview {repo}/{pick.SkillName}…");
        try
        {
            using var cts = new CancellationTokenSource(PreviewTimeout);
            var result = await _preview.PreviewAsync(_ghPath, repo, pick.SkillName, cancellationToken: cts.Token).ConfigureAwait(false);
            Invoke(() =>
            {
                if (_previewPane is not null)
                {
                    _previewPane.Text = result.Succeeded
                        ? result.MarkdownBody ?? result.Body
                        : $"(preview failed: exit {result.ExitCode})\n\n{result.ErrorMessage}";
                }
                if (_previewFrame is not null)
                {
                    _previewFrame.Title = result.AssociatedFiles.Length == 0
                        ? $"Preview — {repo}/{pick.SkillName}"
                        : $"Preview — {repo}/{pick.SkillName} · {result.AssociatedFiles.Length} file(s)";
                }
                SetStatus(result.Succeeded ? "preview loaded" : "preview failed");
            });
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("preview", "preview timed out");
            Invoke(() =>
            {
                if (_previewPane is not null)
                {
                    _previewPane.Text = "(preview timed out)\n\nThe gh subprocess did not respond within 30 seconds.";
                }
                SetStatus("preview timed out");
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error("preview", ex.Message);
            var snippet = TuiHelpers.ErrorSnippet(ex.Message);
            Invoke(() =>
            {
                if (_previewPane is not null)
                {
                    _previewPane.Text = snippet.Length > 0
                        ? $"(preview failed)\n\n{snippet}"
                        : "(preview failed)\n\nSee logs for details.";
                }

                SetStatus(snippet.Length > 0
                    ? $"preview failed: {snippet}"
                    : "preview failed — see logs");
            });
        }
        finally
        {
            Invoke(ClearBusy);
        }
    }

    private void StageInstall()
    {
        if (_resultsTable is null || _results.Count == 0) return;
        var row = _resultsTable.SelectedRow;
        if (row < 0 || row >= _results.Count) return;
        var pick = _results[row];
        if (string.IsNullOrEmpty(pick.Repo))
        {
            SetStatus("no repo on selected row");
            return;
        }
        LastInstallRequest = new InstallRequest(
            Repo: pick.Repo,
            SkillName: pick.SkillName,
            RepoPath: pick.Path);
        SetStatus($"install staged: {pick.Repo}{(pick.SkillName is null ? "" : "/" + pick.SkillName)}");
    }

    private void RefreshResultsTable()
    {
        if (_resultsTable is null) return;
        _resultsTable.Table = new EnumerableTableSource<SearchResultSkill>(
            _results,
            new Dictionary<string, Func<SearchResultSkill, object>>
            {
                ["Skill"] = s => TuiHelpers.Truncate(s.SkillName, 22),
                ["Repo"] = s => TuiHelpers.Truncate(s.Repo, 28),
                ["★"] = s => s.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["Description"] = s => TuiHelpers.Truncate(s.Description, 60),
            });
        TuiHelpers.ApplyColumnStyles(_resultsTable);
        _resultsTable.Update();
    }

    private void UpdatePreviewPlaceholder()
    {
        if (_previewPane is null || _results.Count == 0) return;
        var row = _resultsTable?.SelectedRow ?? -1;
        if (row < 0 || row >= _results.Count) return;
        var pick = _results[row];
        if (!string.IsNullOrEmpty(_previewPane.Text.ToString()) && !_previewPane.Text.ToString()!.StartsWith("(no selection)"))
        {
            return;
        }
        _previewPane.Text = $"Selected: {pick.Repo}/{pick.SkillName}\n\nPress Enter, p, or v to load the preview.";
    }

    private void SetStatus(string text) => Invoke(() =>
    {
        if (_statusLabel is not null) _statusLabel.Text = $" {text}";
    });

    private void SetBusy(string text) => Invoke(() =>
    {
        if (_spinner is not null) { _spinner.Visible = true; _spinner.AutoSpin = true; }
        if (_statusLabel is not null) _statusLabel.Text = $" {text}";
    });

    private void ClearBusy()
    {
        if (_spinner is not null) { _spinner.AutoSpin = false; _spinner.Visible = false; }
    }

    private void Invoke(Action action) => _app.Invoke(action);
}

/// Staged install request produced by the Search screen. Phase 4 consumes
/// this to drive the install dialog (agent multi-select, scope, version).
public sealed record InstallRequest(string Repo, string? SkillName, string? RepoPath);
