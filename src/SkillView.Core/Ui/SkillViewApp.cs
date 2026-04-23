using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Phase 0 feasibility shell. Implements the end-to-end slice mandated by §22
/// Phase 0 exit criteria: boot → search subprocess → JSON parse → TableView →
/// preview subprocess → TextView → quit. Future phases extend this (Installed
/// collection, inventory, updates, cleanup, etc.).
public sealed class SkillViewApp
{
    private readonly TuiServices _services;
    private readonly AppOptions _options;

    private IApplication? _app;
    private TextField? _queryField;
    private TableView? _resultsTable;
    private TextView? _rightPane;
    private Label? _statusLabel;
    private SpinnerView? _spinner;
    private FrameView? _leftFrame;
    private FrameView? _rightFrame;

    private List<SearchResultSkill> _results = new();
    private string? _ghPath;
    private bool _showingLogs;
    private EnvironmentReport? _lastReport;

    public SkillViewApp(TuiServices services, AppOptions options)
    {
        _services = services;
        _options = options;
    }

    // TODO(tg2): upstream — IApplication.Init is flagged RequiresUnreferencedCode /
    // RequiresDynamicCode in rc.4 via ConfigurationManager reflection. Surface to
    // gui-cs/Terminal.Gui with an AOT-friendly config model, then drop this.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "TG2 v2 init uses config reflection; tracked via TODO(tg2) for upstream fix.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "TG2 v2 init uses config reflection; tracked via TODO(tg2) for upstream fix.")]
    public int Run()
    {
        _app = Application.Create().Init();
        // TODO(tg2): upstream — IApplication in rc.4 exposes neither a public
        // `Dispose` nor accessible `ResetState`, and static `Application.Shutdown`
        // is marked `[Obsolete]`. For Phase 0 we let process exit handle cleanup;
        // revisit once TG2 exposes a stable teardown API.
        using var window = BuildUi();
        ProbeGhAsync();
        _app.Run(window);
        return ExitCodes.Success;
    }

    private Window BuildUi()
    {
        var invocationHint = _options.InvocationMode == InvocationMode.GhExtension
            ? "gh skillview"
            : "skillview";
        var window = new Window
        {
            Title = $"SkillView — {invocationHint}",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        _leftFrame = new FrameView
        {
            Title = "Search",
            X = 0,
            Y = 0,
            Width = Dim.Percent(50),
            Height = Dim.Fill(1),
        };

        var queryLabel = new Label
        {
            Text = "Query:",
            X = 0,
            Y = 0,
        };
        _queryField = new TextField
        {
            X = 8,
            Y = 0,
            Width = Dim.Fill(),
            Text = string.Empty,
        };
        _queryField.KeyDown += OnQueryFieldKey;

        _resultsTable = new TableView
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true,
        };
        _resultsTable.CellActivated += (_, _) => _ = PreviewSelectedAsync();

        _leftFrame.Add(queryLabel, _queryField, _resultsTable);

        _rightFrame = new FrameView
        {
            Title = "Preview",
            X = Pos.Right(_leftFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _rightPane = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
            Text = "Press '/' to focus search. 'v' previews selection. 'l' toggles logs. 'q' quits.",
        };
        _rightFrame.Add(_rightPane);

        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(10),
            Text = " ready",
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

        window.Add(_leftFrame, _rightFrame, _statusLabel, _spinner);
        window.KeyDown += OnWindowKeyDown;

        RefreshResultsTable();
        _services.Logger.Subscribe(OnLogEntry);
        return window;
    }

    private void OnWindowKeyDown(object? sender, Key key)
    {
        if (key.Handled)
        {
            return;
        }
        // Don't intercept plain-letter typing while the query field is focused.
        if (_queryField is not null && _queryField.HasFocus)
        {
            return;
        }

        var rune = key.AsRune;
        if (rune.Value == '/')
        {
            _queryField?.SetFocus();
            key.Handled = true;
        }
        else if (rune.Value == 'q' || rune.Value == 'Q')
        {
            _app?.RequestStop();
            key.Handled = true;
        }
        else if (rune.Value == 'v' || rune.Value == 'V')
        {
            _ = PreviewSelectedAsync();
            key.Handled = true;
        }
        else if (rune.Value == 'l' || rune.Value == 'L' || rune.Value == 'r' || rune.Value == 'R')
        {
            ToggleRightPane();
            key.Handled = true;
        }
        else if (rune.Value == 'd' || rune.Value == 'D')
        {
            ShowDoctor();
            key.Handled = true;
        }
        else if (rune.Value == 'I')
        {
            ShowInstalled();
            key.Handled = true;
        }
        else if (key.KeyCode == KeyCode.F1)
        {
            ShowHelp();
            key.Handled = true;
        }
    }

    private void OnQueryFieldKey(object? sender, Key key)
    {
        if (key.KeyCode == KeyCode.Enter)
        {
            key.Handled = true;
            var query = _queryField?.Text.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(query))
            {
                _ = RunSearchAsync(query);
            }
        }
        else if (key.KeyCode == KeyCode.Esc)
        {
            key.Handled = true;
            _resultsTable?.SetFocus();
        }
    }

    private void ProbeGhAsync()
    {
        _ = Task.Run(async () =>
        {
            var report = await _services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
            _lastReport = report;
            _ghPath = report.GhPath;

            if (!report.GhFound)
            {
                SetStatus("gh not found — search and preview disabled; press 'd' for Doctor");
                return;
            }
            if (!report.GhMeetsMinimum)
            {
                SetStatus($"gh {report.GhVersionRaw ?? "?"} below minimum {GhBinaryLocator.MinimumVersion} — press 'd' for Doctor");
                return;
            }
            if (!report.Capabilities.SkillSubcommandPresent)
            {
                SetStatus("`gh skill` not detected — press 'd' for Doctor");
                return;
            }
            SetStatus($"gh {report.GhVersion} — press '/' to search, 'd' for Doctor");
        });
    }

    private async Task RunSearchAsync(string query)
    {
        if (_ghPath is null)
        {
            SetStatus("cannot search — gh not found");
            return;
        }

        SetBusy($"searching {query}…");
        try
        {
            var results = await _services.SearchService
                .SearchAsync(_ghPath, query)
                .ConfigureAwait(false);
            Invoke(() =>
            {
                _results = results.ToList();
                RefreshResultsTable();
                SetStatus(results.Count == 0
                    ? "no matches"
                    : $"{results.Count} result(s) — Enter to preview");
            });
        }
        catch (Exception ex)
        {
            _services.Logger.Error("search", ex.Message);
            SetStatus("search failed — see logs (l)");
        }
        finally
        {
            Invoke(ClearBusy);
        }
    }

    private async Task PreviewSelectedAsync()
    {
        if (_resultsTable is null || _results.Count == 0 || _ghPath is null)
        {
            return;
        }

        var row = _resultsTable.SelectedRow;
        if (row < 0 || row >= _results.Count)
        {
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
            var text = await _services.PreviewService
                .PreviewAsync(_ghPath, repo, pick.SkillName)
                .ConfigureAwait(false);
            Invoke(() =>
            {
                if (_rightPane is not null)
                {
                    _rightPane.Text = text;
                }
                if (_rightFrame is not null)
                {
                    _rightFrame.Title = $"Preview — {repo}/{pick.SkillName}";
                }
                _showingLogs = false;
                SetStatus("preview loaded");
            });
        }
        finally
        {
            Invoke(ClearBusy);
        }
    }

    private void RefreshResultsTable()
    {
        if (_resultsTable is null)
        {
            return;
        }
        var source = new EnumerableTableSource<SearchResultSkill>(
            _results,
            new Dictionary<string, Func<SearchResultSkill, object>>
            {
                ["Skill"] = s => s.SkillName ?? string.Empty,
                ["Repo"] = s => s.Repo ?? string.Empty,
                ["★"] = s => s.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["Description"] = s => s.Description ?? string.Empty,
            });
        _resultsTable.Table = source;
        _resultsTable.Update();
    }

    private void ToggleRightPane()
    {
        if (_rightPane is null || _rightFrame is null)
        {
            return;
        }

        if (_showingLogs)
        {
            _rightPane.Text = "Press Enter on a result to preview.";
            _rightFrame.Title = "Preview";
            _showingLogs = false;
        }
        else
        {
            var log = string.Join('\n', _services.Logger.Snapshot().Select(Logger.Format));
            _rightPane.Text = log.Length > 0 ? log : "(no log entries yet)";
            _rightFrame.Title = "Logs";
            _showingLogs = true;
        }
    }

    private void ShowHelp()
    {
        if (_app is null) return;
        MessageBox.Query(
            _app,
            "SkillView — keys",
            "/  focus search\n" +
            "v  preview selected\n" +
            "l  toggle logs pane\n" +
            "r  toggle logs pane\n" +
            "d  doctor (environment + gh capabilities)\n" +
            "I  installed skills inventory\n" +
            "F1 this help\n" +
            "q  quit",
            "OK");
    }

    private void ShowInstalled()
    {
        if (_app is null) return;
        SetBusy("scanning inventory…");
        _ = Task.Run(async () =>
        {
            var report = _lastReport ?? await _services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
            _lastReport = report;
            var snapshot = await _services.InventoryService.CaptureAsync(
                report.GhPath,
                report.Capabilities,
                new LocalInventoryService.Options(
                    ScanRoots: _options.ScanRoots,
                    AllowHiddenDirs: false)
            ).ConfigureAwait(false);
            Invoke(() =>
            {
                ClearBusy();
                SetStatus($"{snapshot.Skills.Length} installed skill(s)");
                InstalledScreen.Show(_app!, snapshot);
            });
        });
    }

    private void ShowDoctor()
    {
        if (_app is null) return;
        // Freeze UI on the last report if we have one; otherwise probe now.
        if (_lastReport is not null)
        {
            DoctorScreen.Show(_app, _lastReport);
            return;
        }
        SetBusy("probing environment…");
        _ = Task.Run(async () =>
        {
            var report = await _services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
            _lastReport = report;
            Invoke(() =>
            {
                ClearBusy();
                DoctorScreen.Show(_app!, report);
            });
        });
    }

    private void OnLogEntry(LogEntry _)
    {
        if (!_showingLogs) return;
        Invoke(() =>
        {
            if (_rightPane is not null)
            {
                _rightPane.Text = string.Join('\n', _services.Logger.Snapshot().Select(Logger.Format));
            }
        });
    }

    private void SetStatus(string text) => Invoke(() =>
    {
        if (_statusLabel is not null)
        {
            _statusLabel.Text = $" {text}";
        }
    });

    private void SetBusy(string text) => Invoke(() =>
    {
        if (_spinner is not null)
        {
            _spinner.Visible = true;
            _spinner.AutoSpin = true;
        }
        if (_statusLabel is not null)
        {
            _statusLabel.Text = $" {text}";
        }
    });

    private void ClearBusy()
    {
        if (_spinner is not null)
        {
            _spinner.AutoSpin = false;
            _spinner.Visible = false;
        }
    }

    private void Invoke(Action action)
    {
        if (_app is null)
        {
            action();
            return;
        }
        _app.Invoke(action);
    }
}
