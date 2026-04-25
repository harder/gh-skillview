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
/// JSON parse → TableView → preview subprocess → Markdown view → quit. Future
/// phases extend this with inventory, updates, cleanup, and other workflows.
public sealed class SkillViewApp
{
    private readonly TuiServices _services;
    private readonly AppOptions _options;

    private IApplication? _app;
    private TextField? _queryField;
    private TextField? _ownerField;
    private NumericUpDown<int>? _limitUpDown;
    private TableView? _resultsTable;
    private Markdown? _previewPane;
    private Markdown? _metadataPane;
    private TextView? _logPane;
    private Label? _statusLabel;
    private SpinnerView? _spinner;
    private StatusBar? _statusBarPreview;
    private StatusBar? _statusBarLogs;
    private FrameView? _leftFrame;
    private FrameView? _rightFrame;

    private List<SearchResultSkill> _results = new();
    private string? _ghPath;
    private bool _showingLogs;
    private EnvironmentReport? _lastReport;
    private volatile bool _searching;

    private string _defaultStatus = " ready — press / to search or F1 for help";
    private object? _statusToken;
    private static readonly TimeSpan StatusAutoClear = TimeSpan.FromSeconds(6);

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
        // Catch any unhandled exceptions so they get logged instead of silently crashing
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                _services.Logger.Error("CRASH", $"Unhandled: {ex}");
            }
        };

        _app = Application.Create().Init();
        using var window = BuildUi();

        // Application.Keyboard.KeyDown fires BEFORE the view hierarchy processes keys.
        // We intercept Enter here ONLY when the TableView has focus, because TG2 v2
        // RC4's internal view hierarchy steals Enter from TableView: every View has
        // Enter→Command.Accept bound (View.Keyboard.cs line 16), so if the table has
        // an internal focused subview (e.g. ScrollBar), that subview handles Enter
        // before the table ever sees it. P/V bypass because they're not base View
        // bindings. Query field Enter works fine via OnQueryFieldKey — do NOT
        // intercept it here to avoid double-dispatch.
        // TODO(tg2): remove once upstream Enter dispatch to TableView is fixed
        _app.Keyboard.KeyDown += (_, key) =>
        {
            // Only fire when our main window is the active runnable. Otherwise
            // a sub-view (Doctor, Installed, Update, Cleanup, Search…) would
            // see its keys leak into main-window actions like PreviewSelectedAsync.
            if (_app.TopRunnableView != window)
            {
                return;
            }
            try
            {
                var bareKey = key.KeyCode & ~KeyCode.CtrlMask & ~KeyCode.ShiftMask & ~KeyCode.AltMask;
                var isEnterLike = bareKey == KeyCode.Enter
                    || (int)bareKey == 0x0D   // raw CR  (Ctrl+M)
                    || (int)bareKey == 0x0A;  // raw LF  (Ctrl+J)

                // Warp workaround: Ctrl+J arrives as KeyCode.J | CtrlMask
                if (!isEnterLike && (key.KeyCode == (KeyCode.J | KeyCode.CtrlMask)))
                {
                    isEnterLike = true;
                }

                if (isEnterLike)
                {
                    if (!key.Handled && _resultsTable?.HasFocus == true)
                    {
                        key.Handled = true;
                        _services.Logger.Info("preview", "App.KeyDown Enter → calling PreviewSelectedAsync");
                        _ = PreviewSelectedAsync();
                    }
                    else if (!key.Handled && (_queryField?.HasFocus == true || _ownerField?.HasFocus == true || _limitUpDown?.HasFocus == true))
                    {
                        key.Handled = true;
                        _services.Logger.Info("search", "App.KeyDown Enter → SubmitSearch");
                        SubmitSearch();
                    }
                }

                // Right arrow on table → preview (intuitive: points at preview pane)
                if (!key.Handled && bareKey == KeyCode.CursorRight && _resultsTable?.HasFocus == true)
                {
                    key.Handled = true;
                    _ = PreviewSelectedAsync();
                }
            }
            catch (Exception ex)
            {
                _services.Logger.Error("app.KeyDown", $"Enter handler failed: {ex.Message}");
            }
        };

        ProbeGhAsync();
        try
        {
            _app.Run(window);
        }
        finally
        {
            // Dispose the application to ensure the console driver sends
            // terminal reset sequences (disable SGR mouse tracking, restore
            // cursor, etc.). Without this, mouse escape sequences leak into
            // the shell prompt — especially visible in Warp.
            (_app as IDisposable)?.Dispose();
        }
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
            Height = Dim.Fill(2),
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

        var ownerLabel = new Label { Text = "Owner:", X = 0, Y = 1 };
        _ownerField = new TextField
        {
            X = 8, Y = 1, Width = 22, Text = string.Empty,
        };
        TuiHelpers.ConfigureTextInput(_ownerField, "Base");
        _ownerField.KeyDown += OnFilterFieldKey;

        var limitLabel = new Label { Text = "Limit:", X = 32, Y = 1 };
        _limitUpDown = new NumericUpDown<int>
        {
            X = 39, Y = 1,
            Value = GhSkillSearchService.DefaultLimit,
            Increment = 10,
        };
        _limitUpDown.ValueChanging += (_, e) =>
        {
            if (e.NewValue < 1 || e.NewValue > 200) e.Handled = true;
        };

        _resultsTable = new TableView
        {
            X = 0,
            Y = 3,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true,
        };
        TuiHelpers.ConfigureTableKeyBindings(_resultsTable);
        TuiHelpers.ConfigureTableScheme(_resultsTable);

        // Handle Enter explicitly via KeyDown as a belt-and-suspenders
        // workaround. TG2 v2 RC4's Command.Accept pipeline for Enter
        // (which fires CellActivated for p/v) does not reliably reach
        // CellActivated for Enter — likely due to DefaultAcceptHandler
        // bubbling/dispatch interference. This handler fires BEFORE the
        // Command pipeline and short-circuits it for Enter.
        // TODO(tg2): remove once upstream Enter→CellActivated is reliable
        _resultsTable.KeyDown += (_, key) =>
        {
            if ((key.KeyCode == KeyCode.Enter || key.KeyCode == KeyCode.CursorRight) && !key.Handled)
            {
                key.Handled = true;
                _services.Logger.Info("preview", $"{key.KeyCode} KeyDown → calling PreviewSelectedAsync");
                _ = PreviewSelectedAsync();
            }
        };

        _resultsTable.CellActivated += (_, _) =>
        {
            _services.Logger.Info("preview", "CellActivated fired → calling PreviewSelectedAsync");
            _ = PreviewSelectedAsync();
        };
        _resultsTable.SelectedCellChanged += (_, _) =>
        {
            UpdatePreviewPlaceholder();
            UpdateMetadataPane();
        };

        // Re-distribute column widths whenever the table is resized.
        var lastResultsWidth = -1;
        _resultsTable.FrameChanged += (_, _) =>
        {
            var w = _resultsTable?.Viewport.Width ?? 0;
            if (w > 0 && w != lastResultsWidth)
            {
                lastResultsWidth = w;
                RefreshResultsTable();
            }
        };

        _leftFrame.Add(queryLabel, _queryField, ownerLabel, _ownerField, limitLabel, _limitUpDown, _resultsTable);

        _rightFrame = new FrameView
        {
            Title = "Preview",
            X = Pos.Right(_leftFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };
        _previewPane = new Markdown
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(70),
            Height = Dim.Fill(),
            Text = TuiHelpers.WelcomeHint,
        };
        TuiHelpers.ConfigureMarkdownPane(_previewPane, "Base");

        _metadataPane = new Markdown
        {
            X = Pos.Right(_previewPane),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "_(no selection)_",
        };
        TuiHelpers.ConfigureMarkdownPane(_metadataPane, "Base");

        _logPane = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = false,
        };
        TuiHelpers.ConfigureReadOnlyPane(_logPane, "Base");

        _rightFrame.Add(_previewPane, _metadataPane, _logPane);

        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(10),
            Text = " ready — press / to search or F1 for help",
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

        _statusBarPreview = new StatusBar(
        [
            new Shortcut { Title = "/", HelpText = "Search" },
            new Shortcut { Title = "i", HelpText = "Install" },
            new Shortcut { Title = "I", HelpText = "Installed" },
            new Shortcut { Title = "u", HelpText = "Update" },
            new Shortcut { Title = "c", HelpText = "Cleanup" },
            new Shortcut { Title = "d", HelpText = "Doctor" },
            new Shortcut { Title = "l", HelpText = "Logs" },
            new Shortcut { Key = Key.F1, Title = "Help" },
            new Shortcut { Title = "q", HelpText = "Quit" },
        ]);
        _statusBarLogs = new StatusBar(
        [
            new Shortcut { Title = "l", HelpText = "Preview" },
            new Shortcut { Key = Key.F1, Title = "Help" },
            new Shortcut { Title = "q", HelpText = "Quit" },
        ])
        {
            Visible = false,
        };

        TuiHelpers.ApplyScheme("Base",
            window, _leftFrame, _rightFrame,
            queryLabel, _queryField, ownerLabel, _ownerField, limitLabel, _limitUpDown,
            _resultsTable, _previewPane, _metadataPane, _logPane,
            _statusLabel, _spinner, _statusBarPreview, _statusBarLogs);

        window.Add(_leftFrame, _rightFrame, _statusLabel, _spinner, _statusBarPreview, _statusBarLogs);
        window.KeyDown += OnWindowKeyDown;

        RefreshResultsTable();
        _services.Logger.Subscribe(OnLogEntry);

        if (TuiHelpers.IsWarpTerminal)
        {
            SetDefaultStatus("Warp detected — use Ctrl+J instead of Enter (p/v also work for preview)");
        }

        return window;
    }

    private void OnWindowKeyDown(object? sender, Key key)
    {
        if (key.Handled)
        {
            return;
        }
        // Don't intercept plain-letter typing while a text input is focused.
        if (_queryField?.HasFocus == true || _ownerField?.HasFocus == true || _limitUpDown?.HasFocus == true)
        {
            return;
        }

        var rune = key.AsRune;
        if (rune.Value == '/')
        {
            _queryField?.SetFocus();
            // Select all text so the user can immediately type a new query
            if (_queryField is not null)
            {
                _queryField.SelectAll();
            }
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
        else if (rune.Value == 'i')
        {
            StageInstall();
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
        // Accept Enter and Ctrl+J as search submit triggers.
        // Ctrl+J is a workaround for Warp terminal which intercepts Enter
        // for its own block processing after the TUI enables mouse tracking.
        var isSubmit = key.KeyCode == KeyCode.Enter
            || key.KeyCode == (KeyCode.J | KeyCode.CtrlMask);

        if (isSubmit)
        {
            key.Handled = true;
            SubmitSearch();
        }
        else if (key.KeyCode == KeyCode.Esc)
        {
            key.Handled = true;
            _resultsTable?.SetFocus();
        }
    }

    /// Submit a search using the current Query/Owner/Limit fields.
    private void OnFilterFieldKey(object? sender, Key key)
    {
        var isSubmit = key.KeyCode == KeyCode.Enter
            || key.KeyCode == (KeyCode.J | KeyCode.CtrlMask);
        if (isSubmit)
        {
            key.Handled = true;
            SubmitSearch();
        }
    }

    private void SubmitSearch()
    {
        var query = _queryField?.Text.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query)) return;
        var owner = _ownerField?.Text.Trim();
        var limit = _limitUpDown?.Value ?? GhSkillSearchService.DefaultLimit;
        _ = RunSearchAsync(query, string.IsNullOrEmpty(owner) ? null : owner, limit);
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
                SetDefaultStatus("gh not found — search and preview disabled; press 'd' for Doctor");
                return;
            }
            if (!report.GhMeetsMinimum)
            {
                SetDefaultStatus($"gh {report.GhVersionRaw ?? "?"} below minimum {GhBinaryLocator.MinimumVersion} — press 'd' for Doctor");
                return;
            }
            if (!report.Capabilities.SkillSubcommandPresent)
            {
                SetDefaultStatus("`gh skill` not detected — press 'd' for Doctor");
                return;
            }
            SetDefaultStatus($"gh {report.GhVersion} — press '/' to search, 'd' for Doctor");
        }, "probe");
    }

    private async Task RunSearchAsync(string query, string? owner = null, int? limit = null)
    {
        if (_ghPath is null)
        {
            SetStatus("cannot search — gh not found");
            return;
        }
        if (_searching)
        {
            _services.Logger.Debug("search", "skipping — search already in progress");
            return;
        }

        _searching = true;
        SetBusy($"searching {query}…");
        try
        {
            var capabilities = _lastReport?.Capabilities ?? CapabilityProfile.Empty;
            var options = new GhSkillSearchService.Options(
                Owner: owner,
                Limit: limit ?? GhSkillSearchService.DefaultLimit);
            var response = await _services.SearchService
                .SearchAsync(_ghPath, query, capabilities, options)
                .ConfigureAwait(false);
            var results = response.Results;
            Invoke(() =>
            {
                _results = results.ToList();
                RefreshResultsTable();
                UpdateMetadataPane();
                _resultsTable?.SetFocus();
                _services.Logger.Info("search", $"results loaded: count={_results.Count} tableFocus={_resultsTable?.HasFocus} queryFocus={_queryField?.HasFocus}");
                if (_previewPane is not null && !_showingLogs)
                {
                    _previewPane.Text = results.Count == 0 ? TuiHelpers.WelcomeHint : TuiHelpers.PreviewHint;
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
            _searching = false;
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
                if (_previewPane is not null)
                {
                    _previewPane.Text = preview.Succeeded
                        ? preview.MarkdownBody ?? preview.Body ?? "(empty preview)"
                        : $"(preview failed: exit {preview.ExitCode})\n\n{preview.ErrorMessage}";
                }
                if (_rightFrame is not null)
                {
                    _rightFrame.Title = $"Preview — {repo}/{pick.SkillName}";
                }
                ShowPreviewPane();
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
                if (_previewPane is not null)
                {
                    _previewPane.Text = "(preview timed out)\n\nThe gh subprocess did not respond within 30 seconds.";
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
                if (_previewPane is not null)
                {
                    _previewPane.Text = snippet.Length > 0
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
        // Distribute the table's inner width across columns so the Description
        // column gets whatever's left after Skill/Repo/★ are sized. Falls back
        // to a sensible budget when the table hasn't been laid out yet
        // (Viewport.Width == 0 during initial RefreshResultsTable).
        var viewportWidth = _resultsTable.Viewport.Width;
        var available = viewportWidth > 0
            ? Math.Max(40, viewportWidth - 4 /* col separators */)
            : 80;
        var widths = TuiHelpers.DistributeWidths(available, new (int, double)[]
        {
            (12, 1.0), // Skill
            (16, 1.2), // Repo
            (3,  0.0), // ★
            (20, 2.0), // Description
        });
        int skillW = widths[0], repoW = widths[1], starsW = widths[2], descW = widths[3];

        var source = new EnumerableTableSource<SearchResultSkill>(
            _results,
            new Dictionary<string, Func<SearchResultSkill, object>>
            {
                ["Skill"] = s => TuiHelpers.Truncate(s.SkillName, skillW),
                ["Repo"] = s => TuiHelpers.Truncate(s.Repo, repoW),
                ["★"] = s => s.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["Description"] = s => TuiHelpers.Truncate(s.Description, descW),
            });
        _resultsTable.Table = source;
        TuiHelpers.ApplyColumnStyles(_resultsTable, skillW, repoW, starsW, descW);
        _resultsTable.Update();
    }

    private void UpdatePreviewPlaceholder()
    {
        if (_previewPane is null || _showingLogs || _results.Count == 0)
        {
            return;
        }

        var current = _previewPane.Text.ToString() ?? string.Empty;
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
        _previewPane.Text = $"Selected: {pick.Repo}/{pick.SkillName}\n\n{TuiHelpers.PreviewHint}";
    }

    /// Render the metadata sidebar for the currently-selected search result.
    /// Mirrors SkillsGate's metadata panel: name, description, source, URL,
    /// path, namespace, stars. The sidebar always reflects the selected row,
    /// independent of whether the SKILL.md preview has been loaded yet.
    private void UpdateMetadataPane()
    {
        if (_metadataPane is null) return;
        var row = _resultsTable?.SelectedRow ?? -1;
        if (row < 0 || row >= _results.Count)
        {
            _metadataPane.Text = "_(no selection)_";
            return;
        }
        _metadataPane.Text = RenderSearchMetadata(_results[row]);
    }

    private static string RenderSearchMetadata(SearchResultSkill s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {s.SkillName ?? "(unnamed)"}");
        sb.AppendLine();
        sb.AppendLine($"**repo**: `{s.Repo ?? "—"}`  ");
        if (s.Stars is { } stars) sb.AppendLine($"**stars**: ★ {stars.ToString(CultureInfo.InvariantCulture)}  ");
        if (!string.IsNullOrWhiteSpace(s.Repo))
        {
            sb.AppendLine($"**url**: https://github.com/{s.Repo}  ");
        }
        if (!string.IsNullOrWhiteSpace(s.Namespace))
            sb.AppendLine($"**namespace**: `{s.Namespace}`  ");
        if (!string.IsNullOrWhiteSpace(s.Path))
            sb.AppendLine($"**path**: `{s.Path}`  ");
        if (!string.IsNullOrWhiteSpace(s.Description))
        {
            sb.AppendLine();
            sb.AppendLine("### Description");
            sb.AppendLine();
            sb.AppendLine(s.Description);
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("_Press **i** to install._");
        return sb.ToString();
    }

    private void ToggleRightPane()
    {
        if (_previewPane is null || _rightFrame is null)
        {
            return;
        }

        if (_showingLogs)
        {
            ShowPreviewPane();
            _previewPane.Text = TuiHelpers.PreviewHint;
            _rightFrame.Title = "Preview";
        }
        else
        {
            ShowLogPane();
            var log = string.Join('\n', _services.Logger.Snapshot().Select(Logger.Format));
            if (_logPane is not null)
            {
                _logPane.Text = log.Length > 0 ? log : "(no log entries yet)";
            }
            _rightFrame.Title = "Logs";
        }
    }

    private void ShowPreviewPane()
    {
        _showingLogs = false;
        if (_previewPane is not null) _previewPane.Visible = true;
        if (_metadataPane is not null) _metadataPane.Visible = true;
        if (_logPane is not null) _logPane.Visible = false;
        if (_statusBarPreview is not null) _statusBarPreview.Visible = true;
        if (_statusBarLogs is not null) _statusBarLogs.Visible = false;
        if (_leftFrame is not null) _leftFrame.Visible = true;
        if (_rightFrame is not null)
        {
            _rightFrame.X = _leftFrame is not null ? Pos.Right(_leftFrame) : 0;
            _rightFrame.Width = Dim.Fill();
        }
    }

    private void ShowLogPane()
    {
        _showingLogs = true;
        if (_previewPane is not null) _previewPane.Visible = false;
        if (_metadataPane is not null) _metadataPane.Visible = false;
        if (_logPane is not null) _logPane.Visible = true;
        if (_statusBarPreview is not null) _statusBarPreview.Visible = false;
        if (_statusBarLogs is not null) _statusBarLogs.Visible = true;
        if (_leftFrame is not null) _leftFrame.Visible = false;
        if (_rightFrame is not null)
        {
            _rightFrame.X = 0;
            _rightFrame.Width = Dim.Fill();
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

    /// Stage an install of the currently-selected search result. Bound to
    /// `i` on the main view; the actual `gh skill install` invocation runs
    /// in `OpenInstallDialog`.
    private void StageInstall()
    {
        if (_resultsTable is null || _results.Count == 0)
        {
            SetStatus("no results to install");
            return;
        }
        var row = _resultsTable.SelectedRow;
        if (row < 0 || row >= _results.Count)
        {
            SetStatus("no result selected");
            return;
        }
        var pick = _results[row];
        if (string.IsNullOrEmpty(pick.Repo))
        {
            SetStatus("no repo on selected row");
            return;
        }
        OpenInstallDialog(new InstallRequest(
            Repo: pick.Repo,
            SkillName: pick.SkillName,
            RepoPath: pick.Path));
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
            if (_logPane is not null)
            {
                _logPane.Text = string.Join('\n', _services.Logger.Snapshot().Select(Logger.Format));
            }
        });
    }

    private void SetStatus(string text) => Invoke(() =>
    {
        if (_statusLabel is not null)
        {
            _statusLabel.Text = $" {text}";
        }
        ScheduleStatusAutoClear();
    });

    /// Persistent contextual status (gh version, "gh not found", etc.).
    /// Replaces the auto-clear default and is what transient `SetStatus`
    /// messages fall back to after `StatusAutoClear`.
    private void SetDefaultStatus(string text) => Invoke(() =>
    {
        _defaultStatus = $" {text}";
        if (_statusLabel is not null)
        {
            _statusLabel.Text = _defaultStatus;
        }
        CancelStatusAutoClear();
    });

    private void ScheduleStatusAutoClear()
    {
        if (_app is null) return;
        CancelStatusAutoClear();
        _statusToken = _app.AddTimeout(StatusAutoClear, () =>
        {
            _statusToken = null;
            if (_statusLabel is not null)
            {
                _statusLabel.Text = _defaultStatus;
            }
            return false;
        });
    }

    private void CancelStatusAutoClear()
    {
        if (_app is null || _statusToken is null) return;
        _app.RemoveTimeout(_statusToken);
        _statusToken = null;
    }

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
