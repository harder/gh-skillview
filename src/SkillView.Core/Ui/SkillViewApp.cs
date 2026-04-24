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

/// Initial shell for the end-to-end TUI slice: boot → search subprocess →
/// JSON parse → TableView → preview subprocess → TextView → quit. Future
/// phases extend this with inventory, updates, cleanup, and other workflows.
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
        TuiHelpers.ConfigureTextInput(_queryField, "Base");

        _resultsTable = new TableView
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true,
        };
        TuiHelpers.ConfigureTableKeyBindings(_resultsTable);
        _resultsTable.CellActivated += (_, _) =>
        {
            _services.Logger.Info("preview", "CellActivated fired → calling PreviewSelectedAsync");
            _ = PreviewSelectedAsync();
        };
        _resultsTable.SelectedCellChanged += (_, _) => UpdatePreviewPlaceholder();

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
            Text = TuiHelpers.WelcomeHint,
        };
        TuiHelpers.ConfigureReadOnlyPane(_rightPane, "Base");
        _rightFrame.Add(_rightPane);

        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(10),
            Text = " ready — press / to search or F1 for help",
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

        TuiHelpers.ApplyScheme("Base",
            window, _leftFrame, _rightFrame,
            queryLabel, _queryField, _resultsTable, _rightPane, _statusLabel, _spinner);

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
        else if (rune.Value == 's' || rune.Value == 'S')
        {
            ShowSearchScreen();
            key.Handled = true;
        }
        else if (rune.Value == 'u' || rune.Value == 'U')
        {
            ShowUpdateScreen();
            key.Handled = true;
        }
        else if (rune.Value == 'c' || rune.Value == 'C')
        {
            ShowCleanupScreen();
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
        RunBackground(async () =>
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
        }, "probe");
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
                _resultsTable?.SetFocus();
                _services.Logger.Info("search", $"results loaded: count={_results.Count} tableFocus={_resultsTable?.HasFocus} queryFocus={_queryField?.HasFocus}");
                if (_rightPane is not null && !_showingLogs)
                {
                    _rightPane.Text = results.Count == 0 ? TuiHelpers.WelcomeHint : TuiHelpers.PreviewHint;
                }
                if (_rightFrame is not null)
                {
                    _rightFrame.Title = "Preview";
                }
                SetStatus(results.Count == 0
                    ? "no matches"
                    : $"{results.Count} result(s) — Enter, p, or v to preview");
            });
        }
        catch (Exception ex)
        {
            _services.Logger.Error("search", ex.Message);
            var snippet = TuiHelpers.ErrorSnippet(ex.Message);
            SetStatus(snippet.Length > 0
                ? $"search failed: {snippet}"
                : "search failed — see logs (l)");
        }
        finally
        {
            Invoke(ClearBusy);
        }
    }

    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(30);

    private async Task PreviewSelectedAsync()
    {
        _services.Logger.Debug("preview", $"PreviewSelectedAsync entered: table={_resultsTable is not null} results={_results.Count} ghPath={_ghPath is not null}");
        if (_resultsTable is null || _results.Count == 0 || _ghPath is null)
        {
            _services.Logger.Warn("preview", $"guard failed: table={_resultsTable is not null} results={_results.Count} ghPath={_ghPath ?? "(null)"}");
            return;
        }

        var row = _resultsTable.SelectedRow;
        if (row < 0 || row >= _results.Count)
        {
            _services.Logger.Warn("preview", $"SelectedRow={row} out of range (count={_results.Count})");
            return;
        }

        var pick = _results[row];
        var repo = pick.Repo ?? string.Empty;
        _services.Logger.Debug("preview", $"picked: repo={repo} skill={pick.SkillName}");
        if (string.IsNullOrEmpty(repo))
        {
            SetStatus("no repo on selected row");
            return;
        }

        SetBusy($"preview {repo}/{pick.SkillName}…");
        try
        {
            using var cts = new CancellationTokenSource(PreviewTimeout);
            _services.Logger.Info("preview", $"loading {repo}/{pick.SkillName}…");
            var preview = await _services.PreviewService
                .PreviewAsync(_ghPath, repo, pick.SkillName, cancellationToken: cts.Token)
                .ConfigureAwait(false);
            _services.Logger.Debug("preview", $"PreviewAsync returned: succeeded={preview.Succeeded} exit={preview.ExitCode} bodyLen={preview.Body?.Length ?? 0}");
            Invoke(() =>
            {
                if (_rightPane is not null)
                {
                    _rightPane.Text = preview.Succeeded
                        ? (preview.Body ?? "(empty preview)")
                        : $"(preview failed: exit {preview.ExitCode})\n\n{preview.ErrorMessage}";
                }
                if (_rightFrame is not null)
                {
                    _rightFrame.Title = $"Preview — {repo}/{pick.SkillName}";
                }
                _showingLogs = false;
                SetStatus(preview.Succeeded
                    ? (preview.AssociatedFiles.Length == 0
                        ? "preview loaded"
                        : $"preview loaded · {preview.AssociatedFiles.Length} file(s)")
                    : "preview failed — see logs (l)");
            });
        }
        catch (OperationCanceledException)
        {
            _services.Logger.Warn("preview", "preview timed out");
            Invoke(() =>
            {
                if (_rightPane is not null)
                {
                    _rightPane.Text = "(preview timed out)\n\nThe gh subprocess did not respond within 30 seconds.";
                }
                SetStatus("preview timed out");
            });
        }
        catch (Exception ex)
        {
            _services.Logger.Error("preview", ex.Message);
            var snippet = TuiHelpers.ErrorSnippet(ex.Message);
            Invoke(() =>
            {
                if (_rightPane is not null)
                {
                    _rightPane.Text = snippet.Length > 0
                        ? $"(preview failed)\n\n{snippet}"
                        : "(preview failed)\n\nSee logs for details.";
                }

                SetStatus(snippet.Length > 0
                    ? $"preview failed: {snippet}"
                    : "preview failed — see logs (l)");
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
                ["Skill"] = s => TuiHelpers.Truncate(s.SkillName, 24),
                ["Repo"] = s => TuiHelpers.Truncate(s.Repo, 30),
                ["★"] = s => s.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["Description"] = s => TuiHelpers.Truncate(s.Description, 50),
            });
        _resultsTable.Table = source;
        _resultsTable.Update();
    }

    private void UpdatePreviewPlaceholder()
    {
        if (_rightPane is null || _showingLogs || _results.Count == 0)
        {
            return;
        }

        var current = _rightPane.Text.ToString() ?? string.Empty;
        if (current.Length > 0
            && current != TuiHelpers.PreviewHint
            && current != TuiHelpers.WelcomeHint
            && !current.StartsWith("Selected: ", StringComparison.Ordinal)
            && !current.StartsWith("(no selection)", StringComparison.Ordinal))
        {
            return;
        }

        var row = _resultsTable?.SelectedRow ?? -1;
        if (row < 0 || row >= _results.Count)
        {
            return;
        }

        var pick = _results[row];
        _rightPane.Text = $"Selected: {pick.Repo}/{pick.SkillName}\n\n{TuiHelpers.PreviewHint}";
    }

    private void ToggleRightPane()
    {
        if (_rightPane is null || _rightFrame is null)
        {
            return;
        }

        if (_showingLogs)
        {
            _rightPane.Text = TuiHelpers.PreviewHint;
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
            TuiHelpers.HelpText,
            "OK");
    }

    private void ShowSearchScreen()
    {
        if (_app is null) return;
        if (_ghPath is null || _lastReport is null)
        {
            SetStatus("gh not ready — press 'd' for Doctor");
            return;
        }
        var screen = new SearchScreen(
            _app,
            _services.SearchService,
            _services.PreviewService,
            _services.Logger,
            _ghPath,
            _lastReport.Capabilities);
        screen.Show();
        if (screen.LastInstallRequest is { } req)
        {
            OpenInstallDialog(req);
        }
    }

    private void OpenInstallDialog(InstallRequest request)
    {
        if (_app is null || _ghPath is null || _lastReport is null) return;
        var installScreen = new InstallScreen(
            _app,
            _services.InstallService,
            _services.Logger,
            _ghPath,
            _lastReport.Capabilities,
            request);
        installScreen.Show();
        if (installScreen.LastResult is { Succeeded: true } result)
        {
            SetStatus($"installed {result.Repo}{(result.SkillName is null ? "" : "/" + result.SkillName)} — rescanning…");
            RunBackground(async () =>
            {
                var report = _lastReport;
                if (report is null) return;
                var snapshot = await _services.InventoryService.CaptureAsync(
                    report.GhPath,
                    report.Capabilities,
                    new LocalInventoryService.Options(
                        ScanRoots: _options.ScanRoots,
                        AllowHiddenDirs: false)
                ).ConfigureAwait(false);
                Invoke(() =>
                    SetStatus($"installed — inventory now {snapshot.Skills.Length} skill(s)"));
            }, "rescan");
        }
        else if (installScreen.LastResult is { } failed)
        {
            SetStatus($"install failed (exit {failed.ExitCode}) — see logs (l)");
        }
    }

    private void ShowUpdateScreen()
    {
        if (_app is null) return;
        if (_ghPath is null || _lastReport is null)
        {
            SetStatus("gh not ready — press 'd' for Doctor");
            return;
        }
        SetBusy("scanning inventory for update picker…");
        RunBackground(async () =>
        {
            var report = _lastReport;
            if (report is null) return;
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
                var screen = new UpdateScreen(
                    _app!,
                    _services.UpdateService,
                    _services.Logger,
                    _ghPath!,
                    report.Capabilities,
                    snapshot.Skills);
                screen.Show();
                if (screen.LastResult is { DryRun: false, Succeeded: true })
                {
                    SetStatus("update succeeded — rescanning…");
                    RunBackground(async () =>
                    {
                        var post = await _services.InventoryService.CaptureAsync(
                            report.GhPath,
                            report.Capabilities,
                            new LocalInventoryService.Options(
                                ScanRoots: _options.ScanRoots,
                                AllowHiddenDirs: false)
                        ).ConfigureAwait(false);
                        Invoke(() =>
                            SetStatus($"updated — inventory now {post.Skills.Length} skill(s)"));
                    }, "rescan");
                }
                else if (screen.LastResult is { Succeeded: false } failed)
                {
                    SetStatus($"update failed (exit {failed.ExitCode}) — see logs (l)");
                }
            });
        }, "update");
    }

    private void ShowInstalled()
    {
        if (_app is null) return;
        SetBusy("scanning inventory…");
        RunBackground(async () =>
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
                InstalledScreen.Show(_app!, snapshot, target => OpenRemoveDialog(target, snapshot));
            });
        }, "installed");
    }

    private void OpenRemoveDialog(InstalledSkill target, InventorySnapshot snapshot)
    {
        if (_app is null) return;
        var validation = RemoveValidator.Validate(target, snapshot.ScannedRoots, snapshot.Skills);
        var screen = new RemoveScreen(_app, _services.RemoveService, _services.Logger, target, validation);
        screen.Show();
        if (screen.LastReport is { Succeeded: true } report)
        {
            SetStatus($"removed {target.Name} ({report.FilesDeleted} file(s)) — rescanning…");
            RunBackground(async () =>
            {
                var report2 = _lastReport;
                if (report2 is null) return;
                var post = await _services.InventoryService.CaptureAsync(
                    report2.GhPath,
                    report2.Capabilities,
                    new LocalInventoryService.Options(
                        ScanRoots: _options.ScanRoots,
                        AllowHiddenDirs: false)
                ).ConfigureAwait(false);
                Invoke(() =>
                    SetStatus($"removed — inventory now {post.Skills.Length} skill(s)"));
            }, "rescan");
        }
    }

    private void ShowCleanupScreen()
    {
        if (_app is null) return;
        SetBusy("scanning for cleanup candidates…");
        RunBackground(async () =>
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
            var candidates = CleanupClassifier.Classify(snapshot, snapshot.ScannedRoots);
            Invoke(() =>
            {
                ClearBusy();
                var screen = new CleanupScreen(
                    _app!, _services.RemoveService, _services.Logger,
                    candidates, snapshot.ScannedRoots, snapshot.Skills);
                screen.Show();
                SetStatus($"cleanup: removed {screen.RemovedCount}, ignored {screen.IgnoredCount}");
            });
        }, "cleanup");
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
        RunBackground(async () =>
        {
            var report = await _services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
            _lastReport = report;
            Invoke(() =>
            {
                ClearBusy();
                DoctorScreen.Show(_app!, report);
            });
        }, "doctor");
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

    /// Fire-and-forget background work with exception guard. Catches any
    /// unhandled exception, logs it, and shows a status bar message so
    /// failures are never silently swallowed.
    private void RunBackground(Func<Task> work, string operation)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _services.Logger.Error(operation, ex.Message);
                Invoke(() =>
                {
                    ClearBusy();
                    var snippet = TuiHelpers.ErrorSnippet(ex.Message);
                    SetStatus(snippet.Length > 0
                        ? $"{operation} failed: {snippet}"
                        : $"{operation} failed — see logs (l)");
                });
            }
        });
    }
}
