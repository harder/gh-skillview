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
    private TextField? _limitField;
    private TableView? _resultsTable;
    private TextView? _previewPane;
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
        using var dialog = new Dialog
        {
            Title = "Search — gh skill search",
            Width = Dim.Percent(90),
            Height = Dim.Percent(90),
        };

        var queryLabel = new Label { Text = "Query :", X = 0, Y = 0 };
        _queryField = new TextField { X = 8, Y = 0, Width = Dim.Percent(40), Text = string.Empty };

        var ownerLabel = new Label { Text = "Owner :", X = Pos.Right(_queryField) + 2, Y = 0 };
        _ownerField = new TextField { X = Pos.Right(ownerLabel) + 1, Y = 0, Width = Dim.Percent(20), Text = string.Empty };

        var limitLabel = new Label { Text = "Limit :", X = Pos.Right(_ownerField) + 2, Y = 0 };
        _limitField = new TextField
        {
            X = Pos.Right(limitLabel) + 1,
            Y = 0,
            Width = 6,
            Text = GhSkillSearchService.DefaultLimit.ToString(CultureInfo.InvariantCulture),
        };

        var hint = new Label
        {
            Text = "Enter: search  ·  v: preview  ·  i: stage install  ·  Esc/q: close",
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
        };

        _resultsTable = new TableView
        {
            X = 0,
            Y = 3,
            Width = Dim.Percent(55),
            Height = Dim.Fill(2),
            FullRowSelect = true,
        };
        _resultsTable.CellActivated += (_, _) => _ = PreviewSelectedAsync();
        _resultsTable.SelectedCellChanged += (_, _) => UpdatePreviewPlaceholder();

        _previewFrame = new FrameView
        {
            Title = "Preview",
            X = Pos.Right(_resultsTable),
            Y = 3,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };
        _previewPane = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
            Text = "(no selection)",
        };
        _previewFrame.Add(_previewPane);

        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(10),
            Text = " ready — type a query and press Enter",
        };
        _spinner = new SpinnerView
        {
            X = Pos.AnchorEnd(10),
            Y = Pos.AnchorEnd(1),
            Width = 1,
            Height = 1,
            Visible = false,
            AutoSpin = false,
        };

        dialog.Add(
            queryLabel, _queryField,
            ownerLabel, _ownerField,
            limitLabel, _limitField,
            hint,
            _resultsTable, _previewFrame,
            _statusLabel, _spinner);

        _queryField.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;
                _ = RunSearchAsync();
            }
        };

        dialog.KeyDown += (_, key) =>
        {
            if (_queryField.HasFocus || _ownerField.HasFocus || _limitField.HasFocus) return;
            var rune = key.AsRune.Value;
            if (key.KeyCode == KeyCode.Esc || rune == 'q' || rune == 'Q')
            {
                _app.RequestStop();
                key.Handled = true;
            }
            else if (rune == 'v' || rune == 'V')
            {
                _ = PreviewSelectedAsync();
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
                key.Handled = true;
            }
        };

        RefreshResultsTable();
        _queryField.SetFocus();
        _app.Run(dialog);
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
        var limit = GhSkillSearchService.DefaultLimit;
        if (int.TryParse(_limitField?.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            limit = n;
        }

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
                if (!response.Succeeded)
                {
                    SetStatus($"search failed (exit {response.ExitCode}) — see logs");
                }
                else
                {
                    SetStatus(_results.Count == 0
                        ? "no matches"
                        : $"{_results.Count} result(s) — Enter to preview, i to stage install");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error("search", ex.Message);
            Invoke(() => SetStatus("search failed — see logs"));
        }
        finally
        {
            Invoke(ClearBusy);
        }
    }

    private async Task PreviewSelectedAsync()
    {
        if (_resultsTable is null || _results.Count == 0) return;
        var row = _resultsTable.SelectedRow;
        if (row < 0 || row >= _results.Count) return;

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
            var result = await _preview.PreviewAsync(_ghPath, repo, pick.SkillName).ConfigureAwait(false);
            Invoke(() =>
            {
                if (_previewPane is not null)
                {
                    _previewPane.Text = result.Succeeded
                        ? result.Body
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
        SetStatus($"install staged: {pick.Repo}{(pick.SkillName is null ? "" : "/" + pick.SkillName)} — Phase 4 executes");
    }

    private void RefreshResultsTable()
    {
        if (_resultsTable is null) return;
        _resultsTable.Table = new EnumerableTableSource<SearchResultSkill>(
            _results,
            new Dictionary<string, Func<SearchResultSkill, object>>
            {
                ["Skill"] = s => s.SkillName ?? string.Empty,
                ["Repo"] = s => s.Repo ?? string.Empty,
                ["★"] = s => s.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["Description"] = s => s.Description ?? string.Empty,
            });
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
        _previewPane.Text = $"Selected: {pick.Repo}/{pick.SkillName}\n\nPress v to load preview.";
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
