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
using Terminal.Gui.Drawing;
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
    private TextView? _previewRawPane;
    private Markdown? _metadataPane;
    private TextView? _logPane;
    private Label? _statusLabel;
    private SpinnerView? _spinner;
    private StatusBar? _statusBarPreview;
    private StatusBar? _statusBarLogs;
    private FrameView? _leftFrame;
    private FrameView? _rightFrame;
    private FrameView? _metadataFrame;
    private FrameView? _previewFrame;
    private Label? _itemActionsLabel;

    private const int MinMetadataHeight = 3;
    private const int MaxMetadataHeight = 8;
    private const string ItemActionsText = "  [i] Install    [o] Open in browser    [e] Raw / Rendered    [Enter] Preview";

    private List<SearchResultSkill> _results = new();
    private string? _ghPath;
    private bool _showingLogs;
    private bool _showingRawPreview;
    private EnvironmentReport? _lastReport;
    private volatile bool _searching;
    private volatile bool _userInteractedSinceLaunch;
    private volatile bool _startupInstalledShown;

    private string _defaultStatus = " ready — press / to search or F1 for help";
    private object? _statusToken;
    private static readonly TimeSpan StatusAutoClear = TimeSpan.FromSeconds(6);

    public SkillViewApp(TuiServices services, AppOptions options)
    {
        _services = services;
        _options = options;
    }

    internal static bool ShouldOpenInstalledOnStartup(InventorySnapshot snapshot) => snapshot.Skills.Length > 0;

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

        // RC5 routes Enter (View base default), p/v/CursorRight (rebound in
        // ConfigureTableKeyBindings), and Warp's Ctrl+J directly through
        // Command.Accept → the Accepted event on the table. Query field Enter
        // is handled by OnQueryFieldKey. No global key intercept needed.

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

        // Compact single-line text fields. Distinct edit-field scheme
        // (applied via ConfigureTextInput) gives them a visible inverse
        // background so they don't blend into the surrounding FrameView.
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

        // RC5: TableView.OnKeyDownNotHandled now consumes any unbound
        // printable letter key (returns true even when the type-to-search
        // matcher rejected it). Catch SkillView's single-letter shortcuts
        // here — KeyDown fires before OnKeyDownNotHandled — so they don't
        // get swallowed before bubbling to the window.
        _resultsTable.KeyDown += (_, key) =>
        {
            if (!key.Handled)
            {
                NoteUserInteraction();
                if (OnWindowShortcut(key))
                {
                    key.Handled = true;
                }
            }
        };

        // Accepted fires on Enter, double-click, p, v, CursorRight, and
        // Ctrl+J (Warp) — all routed through Command.Accept by the View
        // base or our keybindings.
        _resultsTable.Accepted += (_, _) =>
        {
            _services.Logger.Info("preview", "Accepted → calling PreviewSelectedAsync");
            _ = PreviewSelectedAsync();
        };
        _resultsTable.ValueChanged += (_, _) =>
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
            // No title — the inner Details / SKILL.md frames carry their own.
            X = Pos.Right(_leftFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };
        // Right pane stacks vertically: per-item actions on top (1 line),
        // metadata strip (auto-sized to content), then the preview body fills
        // the rest. Metadata-on-top keeps item context visible while the
        // preview scrolls, and the actions bar advertises 'i/o/e' at point
        // of use rather than burying them in the main bottom status bar.
        _itemActionsLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            Text = ItemActionsText,
        };

        // Metadata in its own bordered FrameView so the boundary between
        // skill details and the SKILL.md preview body is visually obvious.
        _metadataFrame = new FrameView
        {
            Title = "Details",
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = MinMetadataHeight + 2 /* borders */,
            BorderStyle = LineStyle.Single,
        };
        _metadataPane = new Markdown
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            Text = "_(no selection)_",
        };
        TuiHelpers.ConfigureMarkdownPane(_metadataPane, "Base");
        _metadataFrame.Add(_metadataPane);

        _previewFrame = new FrameView
        {
            Title = "SKILL.md",
            X = 0,
            Y = Pos.Bottom(_metadataFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
        };
        _previewPane = new Markdown
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            Text = TuiHelpers.WelcomeHint,
        };
        TuiHelpers.ConfigureMarkdownPane(_previewPane, "Base");

        _previewRawPane = new TextView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            Text = TuiHelpers.WelcomeHint,
            Visible = false,
        };
        TuiHelpers.ConfigureReadOnlyPane(_previewRawPane, "Base");
        _previewFrame.Add(_previewPane, _previewRawPane);

        _logPane = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = false,
        };
        TuiHelpers.ConfigureReadOnlyPane(_logPane, "Base");

        _rightFrame.Add(_itemActionsLabel, _metadataFrame, _previewFrame, _logPane);

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
            new Shortcut { Title = "o", HelpText = "Open" },
            new Shortcut { Title = "e", HelpText = "Raw/Render" },
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
            _resultsTable, _previewPane, _previewRawPane, _metadataPane, _logPane,
            _statusLabel, _spinner, _statusBarPreview, _statusBarLogs);
        // Invert the actions hint so it reads as a status-bar-style strip.
        _itemActionsLabel.SetScheme(TuiHelpers.CreateStatusScheme(TuiHelpers.NotificationLevel.Info));

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
        if (key.Handled) return;
        NoteUserInteraction();
        if (OnWindowShortcut(key))
        {
            key.Handled = true;
        }
    }

    /// Centralised single-letter shortcut dispatcher used by both
    /// `window.KeyDown` and `_resultsTable.KeyDown`. The latter is required
    /// because RC5's TableView swallows unbound printable letters in
    /// `OnKeyDownNotHandled`, preventing them from bubbling to the window.
    /// Returns true if the key was consumed.
    private bool OnWindowShortcut(Key key)
    {
        // Don't intercept plain-letter typing while a text input is focused.
        if (_queryField?.HasFocus == true || _ownerField?.HasFocus == true || _limitUpDown?.HasFocus == true)
        {
            return false;
        }

        var rune = key.AsRune;
        if (rune.Value == '/')
        {
            _queryField?.SetFocus();
            if (_queryField is not null) _queryField.SelectAll();
            return true;
        }
        if (rune.Value == 'q' || rune.Value == 'Q') { _app?.RequestStop(); return true; }
        if (rune.Value == 'l' || rune.Value == 'L' || rune.Value == 'r' || rune.Value == 'R') { ToggleRightPane(); return true; }
        if (rune.Value == 'e' || rune.Value == 'E') { TogglePreviewMode(); return true; }
        if (rune.Value == 'd' || rune.Value == 'D') { ShowDoctor(); return true; }
        if (rune.Value == 'I') { ShowInstalled(); return true; }
        if (rune.Value == 'i') { StageInstall(); return true; }
        if (rune.Value == 'o' || rune.Value == 'O') { OpenSelected(); return true; }
        if (rune.Value == 'u' || rune.Value == 'U') { ShowUpdateScreen(); return true; }
        if (rune.Value == 'c' || rune.Value == 'C') { ShowCleanupScreen(); return true; }
        if (key.KeyCode == KeyCode.F1) { ShowHelp(); return true; }
        return false;
    }

    private void OnQueryFieldKey(object? sender, Key key)
    {
        NoteUserInteraction();
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
        NoteUserInteraction();
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

            var snapshot = await _services.InventoryService.CaptureAsync(
                report.GhPath,
                report.Capabilities,
                new LocalInventoryService.Options(
                    ScanRoots: _options.ScanRoots,
                    AllowHiddenDirs: false)
            ).ConfigureAwait(false);

            Invoke(() =>
            {
                if (ShouldAutoOpenInstalledOnStartup(snapshot))
                {
                    _startupInstalledShown = true;
                    OpenInstalledSnapshot(snapshot);
                }
            });

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
            SetStatus("cannot search — gh not found", TuiHelpers.NotificationLevel.Error);
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
                if (!_showingLogs)
                {
                    SetPreviewText(results.Count == 0 ? TuiHelpers.WelcomeHint : TuiHelpers.PreviewHint);
                }
                if (_previewFrame is not null)
                {
                    _previewFrame.Title = "SKILL.md";
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
                : "search failed — see logs (l)",
                TuiHelpers.NotificationLevel.Error);
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

        var row = _resultsTable.GetSelectedRow();
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
                SetPreviewText(preview.Succeeded
                    ? preview.MarkdownBody ?? preview.Body ?? "(empty preview)"
                    : $"(preview failed: exit {preview.ExitCode})\n\n{preview.ErrorMessage}");
                if (_previewFrame is not null)
                {
                    _previewFrame.Title = $"SKILL.md — {repo}/{pick.SkillName}";
                }
                ShowPreviewPane();
                if (preview.Succeeded)
                {
                    SetStatus(preview.AssociatedFiles.Length == 0
                        ? "preview loaded"
                        : $"preview loaded · {preview.AssociatedFiles.Length} file(s)",
                        TuiHelpers.NotificationLevel.Success);
                }
                else
                {
                    SetStatus("preview failed — see logs (l)", TuiHelpers.NotificationLevel.Error);
                }
            });
        }
        catch (OperationCanceledException)
        {
            _services.Logger.Warn("preview", "preview timed out");
            Invoke(() =>
            {
                SetPreviewText("(preview timed out)\n\nThe gh subprocess did not respond within 30 seconds.");
                SetStatus("preview timed out", TuiHelpers.NotificationLevel.Error);
            });
        }
        catch (Exception ex)
        {
            _services.Logger.Error("preview", ex.Message);
            var snippet = TuiHelpers.ErrorSnippet(ex.Message);
            Invoke(() =>
            {
                SetPreviewText(snippet.Length > 0
                    ? $"(preview failed)\n\n{snippet}"
                    : "(preview failed)\n\nSee logs for details.");

                SetStatus(snippet.Length > 0
                    ? $"preview failed: {snippet}"
                    : "preview failed — see logs (l)",
                    TuiHelpers.NotificationLevel.Error);
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
        // Three columns: ★, Name, Repo. ★ leads so it can't be pushed off the
        // right edge by long names/repos, and ExpandLastColumn lets Repo soak
        // up any leftover budget. Description was dropped — the preview pane
        // and metadata strip surface it instead.
        var viewportWidth = _resultsTable.Viewport.Width;
        var available = viewportWidth > 0
            ? Math.Max(40, viewportWidth - 6 /* borders + 2 separators + slop */)
            : 80;
        var longestName = _results.Count == 0 ? 0 : _results.Max(s => (s.SkillName ?? string.Empty).Length);
        var longestRepo = _results.Count == 0 ? 0 : _results.Max(s => (s.Repo ?? string.Empty).Length);
        // Cap mins so a single very-long value can't starve the other columns.
        var nameMin = Math.Clamp(longestName, 12, 28);
        var repoMin = Math.Clamp(longestRepo, 14, 32);
        var widths = TuiHelpers.DistributeWidths(available, new (int, double)[]
        {
            (5,       0.0), // ★
            (nameMin, 1.0), // Name
            (repoMin, 1.0), // Repo
        });
        int starsW = widths[0], nameW = widths[1], repoW = widths[2];

        var source = new EnumerableTableSource<SearchResultSkill>(
            _results,
            new Dictionary<string, Func<SearchResultSkill, object>>
            {
                ["★"] = s => s.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["Name"] = s => TuiHelpers.Truncate(s.SkillName, nameW),
                ["Repo"] = s => TuiHelpers.Truncate(s.Repo, repoW),
            });
        _resultsTable.Table = source;
        TuiHelpers.ApplyColumnStyles(_resultsTable, nameW, repoW, starsW, 0);
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

        var row = _resultsTable?.GetSelectedRow() ?? -1;
        if (row < 0 || row >= _results.Count)
        {
            return;
        }

        var pick = _results[row];
        SetPreviewText($"Selected: {pick.Repo}/{pick.SkillName}\n\n{TuiHelpers.PreviewHint}");
    }

    /// Render the metadata sidebar for the currently-selected search result.
    /// Mirrors SkillsGate's metadata panel: name, description, source, URL,
    /// path, namespace, stars. The sidebar always reflects the selected row,
    /// independent of whether the SKILL.md preview has been loaded yet.
    private void UpdateMetadataPane()
    {
        if (_metadataPane is null) return;
        var row = _resultsTable?.GetSelectedRow() ?? -1;
        string text;
        if (row < 0 || row >= _results.Count)
        {
            text = "_(no selection)_";
        }
        else
        {
            text = RenderSearchMetadata(_results[row]);
        }
        _metadataPane.Text = text;

        // Auto-size metadata height so short entries don't waste space and
        // longer ones don't scroll. Count rendered lines (non-empty terminal
        // lines — Markdown renderer keeps our single-line fields intact).
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var nonBlank = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        var height = Math.Clamp(nonBlank, MinMetadataHeight, MaxMetadataHeight);
        if (_metadataFrame is not null) _metadataFrame.Height = height + 2; // borders
    }

    private static string RenderSearchMetadata(SearchResultSkill s)
    {
        // One **label**: value pair per line. Labels are bold to anchor the
        // eye on the left edge; values are plain so URLs / paths read clean.
        // Avoid Markdown headings — TG2's renderer expands them into taller
        // blocks and consumes vertical space we'd rather give the preview.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**Skill** : {s.SkillName ?? "(unnamed)"}");
        sb.AppendLine($"**Repo**  : {s.Repo ?? "—"}");
        if (s.Stars is { } st)
            sb.AppendLine($"**Stars** : ★ {st.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(s.Repo))
            sb.AppendLine($"**URL**   : https://github.com/{s.Repo}");
        if (!string.IsNullOrWhiteSpace(s.Path))
            sb.AppendLine($"**Path**  : {s.Path}");
        if (!string.IsNullOrWhiteSpace(s.Namespace))
            sb.AppendLine($"**Ns**    : {s.Namespace}");
        if (!string.IsNullOrWhiteSpace(s.Description))
            sb.AppendLine($"**About** : {s.Description}");
        return sb.ToString();
    }

    /// TG2 RC4's Markdown renderer collapses tight bullet lists into one
    /// paragraph (consecutive `- foo` lines render inline). Normalizing to
    /// a loose list — blank line before the list and between items — forces
    /// per-item line breaks. Also handles `*` and `+` markers and numbered
    /// lists.
    private static string NormalizeMarkdownLists(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder(markdown.Length + 128);
        var prevBlank = true;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var isList = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ")
                || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[\.\)]\s");
            if (isList && !prevBlank)
            {
                sb.Append('\n');
            }
            sb.Append(line);
            sb.Append('\n');
            prevBlank = string.IsNullOrWhiteSpace(line);
        }
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
            SetPreviewText(TuiHelpers.PreviewHint);
            if (_previewFrame is not null) _previewFrame.Title = "SKILL.md";
        }
        else
        {
            ShowLogPane();
            var log = string.Join('\n', _services.Logger.Snapshot().Select(Logger.Format));
            if (_logPane is not null)
            {
                _logPane.Text = log.Length > 0 ? log : "(no log entries yet)";
            }
        }
    }

    /// Mirror text into both the rendered Markdown pane and the raw
    /// TextView pane so toggling between them via `e` keeps the same content.
    private void SetPreviewText(string text)
    {
        if (_previewPane is not null) _previewPane.Text = NormalizeMarkdownLists(text);
        if (_previewRawPane is not null) _previewRawPane.Text = text;
    }

    private void TogglePreviewMode()
    {
        if (_previewPane is null || _previewRawPane is null || _showingLogs) return;
        _showingRawPreview = !_showingRawPreview;
        _previewPane.Visible = !_showingRawPreview;
        _previewRawPane.Visible = _showingRawPreview;
        SetStatus(_showingRawPreview ? "preview: raw SKILL.md" : "preview: rendered");
    }

    private void ShowPreviewPane()
    {
        _showingLogs = false;
        if (_previewPane is not null) _previewPane.Visible = !_showingRawPreview;
        if (_previewRawPane is not null) _previewRawPane.Visible = _showingRawPreview;
        if (_metadataFrame is not null) _metadataFrame.Visible = true;
        if (_previewFrame is not null) _previewFrame.Visible = true;
        if (_itemActionsLabel is not null) _itemActionsLabel.Visible = true;
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
        if (_previewRawPane is not null) _previewRawPane.Visible = false;
        if (_metadataFrame is not null) _metadataFrame.Visible = false;
        if (_previewFrame is not null) _previewFrame.Visible = false;
        if (_itemActionsLabel is not null) _itemActionsLabel.Visible = false;
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

    /// Open the GitHub page for the selected search result in the system
    /// browser. Bound to `o` on the main view.
    private void OpenSelected()
    {
        if (_resultsTable is null || _results.Count == 0)
        {
            SetStatus("no result to open");
            return;
        }
        var row = _resultsTable.GetSelectedRow();
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
        var url = $"https://github.com/{pick.Repo}";
        if (TuiHelpers.OpenInDefaultHandler(url))
        {
            SetStatus($"opened {url}", TuiHelpers.NotificationLevel.Success);
        }
        else
        {
            SetStatus("open failed — see logs (l)", TuiHelpers.NotificationLevel.Error);
            _services.Logger.Warn("open", $"failed to open {url}");
        }
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
        var row = _resultsTable.GetSelectedRow();
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
            SetStatus($"installed {result.Repo}{(result.SkillName is null ? "" : "/" + result.SkillName)} — rescanning…", TuiHelpers.NotificationLevel.Success);
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
                    SetStatus($"installed — inventory now {snapshot.Skills.Length} skill(s)", TuiHelpers.NotificationLevel.Success));
            }, "rescan");
        }
        else if (installScreen.LastResult is { } failed)
        {
            SetStatus($"install failed (exit {failed.ExitCode}) — see logs (l)", TuiHelpers.NotificationLevel.Error);
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
                    SetStatus("update succeeded — rescanning…", TuiHelpers.NotificationLevel.Success);
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
                            SetStatus($"updated — inventory now {post.Skills.Length} skill(s)", TuiHelpers.NotificationLevel.Success));
                    }, "rescan");
                }
                else if (screen.LastResult is { Succeeded: false } failed)
                {
                    SetStatus($"update failed (exit {failed.ExitCode}) — see logs (l)", TuiHelpers.NotificationLevel.Error);
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
                OpenInstalledSnapshot(snapshot);
            });
        }, "installed");
    }

    private void OpenInstalledSnapshot(InventorySnapshot snapshot)
    {
        if (_app is null) return;
        InstalledScreen.Show(
            _app,
            snapshot,
            target => OpenRemoveDialog(target, snapshot),
            FocusSearchFromInstalled);
    }

    private void FocusSearchFromInstalled()
    {
        _queryField?.SetFocus();
        _queryField?.SelectAll();
        SetStatus("search ready");
    }

    private void OpenRemoveDialog(InstalledSkill target, InventorySnapshot snapshot)
    {
        if (_app is null) return;
        var validation = RemoveValidator.Validate(target, snapshot.ScannedRoots, snapshot.Skills);
        var screen = new RemoveScreen(_app, _services.RemoveService, _services.Logger, target, validation);
        screen.Show();
        if (screen.LastReport is { Succeeded: true } report)
        {
            SetStatus($"removed {target.Name} ({report.FilesDeleted} file(s)) — rescanning…", TuiHelpers.NotificationLevel.Success);
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
                    SetStatus($"removed — inventory now {post.Skills.Length} skill(s)", TuiHelpers.NotificationLevel.Success));
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

    private void SetStatus(string text) => SetStatus(text, TuiHelpers.NotificationLevel.Info);

    private void SetStatus(string text, TuiHelpers.NotificationLevel level) => Invoke(() =>
    {
        if (_statusLabel is not null)
        {
            _statusLabel.Text = $" {text}";
            _statusLabel.SetScheme(TuiHelpers.CreateStatusScheme(level));
            _statusLabel.SetNeedsDraw();
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
            _statusLabel.SetScheme(TuiHelpers.CreateStatusScheme(TuiHelpers.NotificationLevel.Info));
            _statusLabel.SetNeedsDraw();
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
                _statusLabel.SetScheme(TuiHelpers.CreateStatusScheme(TuiHelpers.NotificationLevel.Info));
                _statusLabel.SetNeedsDraw();
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

    private bool ShouldAutoOpenInstalledOnStartup(InventorySnapshot snapshot) =>
        !_startupInstalledShown
        && !_userInteractedSinceLaunch
        && ShouldOpenInstalledOnStartup(snapshot);

    private void NoteUserInteraction()
    {
        _userInteractedSinceLaunch = true;
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
                        : $"{operation} failed — see logs (l)",
                        TuiHelpers.NotificationLevel.Error);
                });
            }
        });
    }
}
