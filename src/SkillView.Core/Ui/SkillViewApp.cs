using System.Collections.Immutable;
using System.Globalization;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
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
    private readonly Func<IApplication> _applicationFactory;
    private readonly bool _probeOnRun;
    private readonly SearchAgentMetadataCache _searchAgentMetadata = new();
    private readonly SkillViewWorkflowCoordinator _workflows;

    private IApplication? _app;
    private CancellationTokenSource? _runLifetime;
    private bool _hasRunLifetime;
    private TextField? _queryField;
    private TextField? _ownerField;
    private TextField? _agentField;
    private NumericUpDown<int>? _limitUpDown;
    private CheckBox? _hiddenDirsBox;
    private TableView? _resultsTable;
    private Markdown? _previewPane;
    private Editor? _previewRawPane;
    private Editor? _logPane;
    private ContextBarView? _contextBar;
    private StatusStripView? _statusStrip;
    private SpinnerView? _spinner;
    private TabBarView? _tabBar;
    private SkillViewTab _activeTab = SkillViewTab.Discover;
    private FrameView? _leftFrame;
    private SkillDetailPaneView? _detailPane;
    private FrameView? _rightFrame;
    private FrameView? _metadataFrame;
    private FrameView? _previewFrame;
    private Label? _itemActionsLabel;
    private SkillView.Ui.Tabs.InstalledTabView? _installedTab;
    private SkillView.Ui.Tabs.UpdatesTabView? _updatesTab;
    private SkillView.Ui.Tabs.DoctorTabView? _doctorTab;
    private SkillView.Ui.Tabs.DiscoverTabView? _discoverTab;
    private SkillView.Ui.Tabs.ChangesTabView? _changesTab;
    // Remembered before Doctor took over so Esc returns to where the user was.
    private SkillViewTab _tabBeforeDoctor = SkillViewTab.Discover;
    private bool _inDoctor;
    // Set when Updates is drilled into from the Changes queue so Esc returns to Changes.
    private bool _openedUpdatesFromChanges;

    private const string ItemActionsText = "  [h] Hidden dirs    [i] Install    [o] Open in browser    [e] Raw / Rendered    [Enter] Preview";

    private List<SearchResultSkill> _results = new();
    // Original gh-skill-search ordering for the current query — preserved so
    // the `S` sort cycle's "Off" mode can restore it. Re-set by RunSearchAsync
    // on each fresh fetch.
    private List<SearchResultSkill> _resultsNaturalOrder = new();
    private SearchSort _searchSort = SearchSort.Off;
    private string? _ghPath;
    private bool _showingLogs;
    private bool _showingRawPreview;
    private EnvironmentReport? _lastReport;
    private volatile bool _searching;
    // Monotonic generation counter — bumped on each RunSearchAsync invocation
    // and captured at submit time. Result painting checks for stale generation
    // and silently drops out-of-band completions. Mirrors winget-tui's
    // app.view_generation pattern in src/app.rs.
    private long _searchGeneration;
    private volatile bool _userInteractedSinceLaunch;
    private volatile bool _startupInstalledShown;
    private volatile bool _startupFocusPrimed;

    /// Sort modes for the Search tab results table. Mirrors winget-tui's
    /// app.sort_field cycle in src/app.rs. Off restores the natural ordering
    /// returned by `gh skill search` (which is itself relevance-ranked).
    internal enum SearchSort { Off, StarsDesc, NameAsc, NameDesc, RepoAsc }

    private string _defaultStatus = "ready — press / to search or F1 for help";
    private string _currentStatus = string.Empty;
    private object? _statusToken;
    private static readonly TimeSpan StatusAutoClear = TimeSpan.FromSeconds(6);

    public SkillViewApp(TuiServices services, AppOptions options)
        : this(services, options, static () => Application.Create().Init(), probeOnRun: true)
    {
    }

    internal SkillViewApp(
        TuiServices services,
        AppOptions options,
        Func<IApplication> applicationFactory,
        bool probeOnRun)
    {
        _services = services;
        _options = options;
        _applicationFactory = applicationFactory;
        _probeOnRun = probeOnRun;
        _workflows = new SkillViewWorkflowCoordinator(
            services,
            options,
            () => _app,
            () => _ghPath,
            () => _lastReport,
            report => _lastReport = report,
            SetBusy,
            ClearBusy,
            SetStatus,
            SetStatus,
            Invoke,
            RunBackground,
            FocusSearchFromInstalled);
    }

    internal static bool ShouldOpenInstalledOnStartup(InventorySnapshot snapshot) => snapshot.Skills.Length > 0;

    internal static bool ShouldAutoOpenInstalledOnStartup(
        InventorySnapshot snapshot,
        bool startupInstalledShown,
        bool userInteractedSinceLaunch) =>
        !startupInstalledShown
        && !userInteractedSinceLaunch
        && ShouldOpenInstalledOnStartup(snapshot);

    // SkillView pins the Terminal.Gui 2.2.2-develop.18 release; this startup path
    // stays aligned with TG2 AOT guidance and the modern lifecycle.
    public int Run() => RunAsync().GetAwaiter().GetResult();

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        TuiHelpers.SetTheme(_options.Theme);
        ConfigurationManager.Enable(ConfigLocations.All);
        SkillView.Ui.Theming.WingetTuiTheme.Register(_options.Theme);
        if (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Success;
        }

        IApplication? app = null;
        Window? window = null;
        using var runLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _hasRunLifetime = true;
        _runLifetime = runLifetime;

        UnhandledExceptionEventHandler onUnhandledException = (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogUnhandledException(ex);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += onUnhandledException;

        try
        {
            app = _applicationFactory();
            if (runLifetime.IsCancellationRequested)
            {
                return ExitCodes.Success;
            }

            _app = app;
            window = BuildUi();

            // TableView routes Enter (View base default), p/v/CursorRight (rebound in
            // ConfigureTableKeyBindings), and Warp's Ctrl+J directly through
            // Command.Accept → the Accepted event on the table. Query field Enter
            // is handled by OnQueryFieldKey. No global key intercept needed.

            if (_probeOnRun)
            {
                ProbeGhAsync();
            }

            await app.RunAsync(window, runLifetime.Token, HandleRunLoopException).ConfigureAwait(false);
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= onUnhandledException;
            CancelStatusAutoClear();
            runLifetime.Cancel();
            _hasRunLifetime = false;
            _runLifetime = null;
            _app = null;
            window?.Dispose();
            app?.Dispose();
        }
        return ExitCodes.Success;
    }

    private bool HandleRunLoopException(Exception ex)
    {
        LogUnhandledException(ex);
        return false;
    }

    private void LogUnhandledException(Exception ex)
    {
        _services.Logger.Error("CRASH", $"Unhandled: {ex}");
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

        // Top header strip: tabs on the right, "Skill View" wordmark on the
        // left. Stays present across all tab views, mirroring winget-tui's
        // Search / Installed / Upgrades header.
        _tabBar = new TabBarView
        {
            X = 0,
            Y = 0,
        };
        _tabBar.TabActivated += (_, tab) => ActivateTab(tab);

        // Discover workspace — owns the 60/40 search-shell layout (left frame
        // with query controls + results table, right detail pane).  All inner
        // controls are exposed as properties so existing event-handler and
        // rendering code below can reference them without further changes.
        _discoverTab = new SkillView.Ui.Tabs.DiscoverTabView(ItemActionsText, TuiHelpers.WelcomeHint)
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };

        // Pull control references from the workspace so the rest of BuildUi
        // and all existing call sites continue to work unchanged.
        _leftFrame       = _discoverTab.LeftFrame;
        _queryField      = _discoverTab.QueryField;
        _ownerField      = _discoverTab.OwnerField;
        _limitUpDown     = _discoverTab.LimitUpDown;
        _agentField      = _discoverTab.AgentField;
        _hiddenDirsBox   = _discoverTab.HiddenDirsBox;
        _resultsTable    = _discoverTab.ResultsTable;
        _detailPane      = _discoverTab.DetailPane;
        _rightFrame      = _discoverTab.DetailPane;
        _itemActionsLabel = _discoverTab.DetailPane.ItemActionsLabel;
        _metadataFrame   = _discoverTab.DetailPane.MetadataFrame;
        _previewFrame    = _discoverTab.DetailPane.PreviewFrame;
        _previewPane     = _discoverTab.DetailPane.PreviewPane;
        _previewRawPane  = _discoverTab.DetailPane.PreviewRawPane;
        _logPane         = _discoverTab.DetailPane.LogPane;

        // Wire event handlers that require SkillViewApp state.
        _queryField.KeyDown += OnQueryFieldKey;
        _ownerField.KeyDown += OnFilterFieldKey;
        _agentField.KeyDown += OnFilterFieldKey;
        _limitUpDown.ValueChanging += (_, e) =>
        {
            NoteUserInteraction();
            if (e.NewValue < 1 || e.NewValue > 200) e.Handled = true;
        };
        _hiddenDirsBox.ValueChanged += (_, _) =>
        {
            NoteUserInteraction();
            RefreshHiddenDirUi();
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

        _contextBar = new ContextBarView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
        };

        _statusStrip = new StatusStripView
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
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

        TuiHelpers.ApplyScheme(SkillViewStyling.BaseSchemeName, window, _spinner);
        RefreshHiddenDirUi();

        Func<Action, Task> runOnUi = action =>
        {
            var tcs = new TaskCompletionSource();
            Invoke(() =>
            {
                try { action(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        };

        _installedTab = new SkillView.Ui.Tabs.InstalledTabView(
            runOnUi: runOnUi,
            snapshotLoader: () => _workflows.CaptureInventorySnapshotAsync(GetRunLifetimeToken()),
            onRemove: (skill, snap) => _workflows.OpenRemoveDialog(skill, snap),
            onLeaveTab: () => ActivateTab(SkillViewTab.Discover),
            onGoToSearch: () => { ActivateTab(SkillViewTab.Discover); FocusSearchFromInstalled(); },
            onFilterChange: UpdateContextBar)
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Visible = false,
        };

        _updatesTab = new SkillView.Ui.Tabs.UpdatesTabView(
            runOnUi: runOnUi,
            snapshotLoader: () => _workflows.CaptureInventorySnapshotAsync(GetRunLifetimeToken()),
            updateServiceFactory: () => _services.UpdateService,
            ghPathProvider: () => _ghPath,
            capabilitiesProvider: () => _lastReport?.Capabilities ?? CapabilityProfile.Empty,
            logger: _services.Logger,
            onLeaveTab: LeaveUpdates,
            onUpdateApplied: () => _services.ListAdapter.Invalidate())
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Visible = false,
        };

        _doctorTab = new SkillView.Ui.Tabs.DoctorTabView(
            onLeaveTab: LeaveDoctor)
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Visible = false,
        };

        _changesTab = new SkillView.Ui.Tabs.ChangesTabView(
            runOnUi: runOnUi,
            snapshotLoader: () => _workflows.CaptureInventorySnapshotAsync(GetRunLifetimeToken()),
            onActivateUpdates: () =>
            {
                _openedUpdatesFromChanges = true;
                _workflows.OpenUpdatesFromChanges(
                    hideChanges: () => _changesTab!.Visible = false,
                    activateUpdates: hideChanges => _updatesTab!.ActivateFromChanges(hideChanges));
            },
            onActivateCleanup: () => _workflows.ShowCleanupScreen(),
            onActivateDoctor: () => _doctorTab?.ActivateFromChanges(
                hideChanges: () => { if (_changesTab is not null) _changesTab.Visible = false; },
                enterDoctor: EnterDoctor),
            onLeaveTab: () => ActivateTab(SkillViewTab.Discover),
            onStateChange: UpdateContextBar)
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };

        window.Add(_tabBar, _contextBar, _discoverTab, _installedTab, _updatesTab, _doctorTab,
                   _changesTab,
                   _spinner, _statusStrip);
        window.KeyDown += OnWindowKeyDown;
        AttachStartupPointerAndKeyTracking(
            window,
            _leftFrame,
            _rightFrame,
            _queryField,
            _ownerField,
            _agentField,
            _limitUpDown,
            _hiddenDirsBox,
            _resultsTable);
        AttachStartupFocusTracking(
            _queryField,
            _ownerField,
            _agentField,
            _limitUpDown,
            _hiddenDirsBox,
            _resultsTable);

        RefreshResultsTable();
        _services.Logger.Subscribe(OnLogEntry);
        UpdateContextBar();

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

    /// Centralised single-letter shortcut dispatcher for `window.KeyDown`.
    /// Returns true if the key was consumed.
    private bool OnWindowShortcut(Key key)
    {
        // Don't intercept plain-letter typing while a text input is focused.
        if (_queryField?.HasFocus == true || _ownerField?.HasFocus == true || _agentField?.HasFocus == true || _limitUpDown?.HasFocus == true)
        {
            return false;
        }

        var rune = key.AsRune;
        if (rune.Value == '/')
        {
            if (_inDoctor)
            {
                LeaveDoctor();
            }
            if (_activeTab != SkillViewTab.Discover)
            {
                ActivateTab(SkillViewTab.Discover);
            }
            _queryField?.SetFocus();
            if (_queryField is not null) _queryField.SelectAll();
            return true;
        }
        if (rune.Value == 'h' || rune.Value == 'H')
        {
            ToggleHiddenDirAccess();
            return true;
        }
        if (rune.Value == 'q' || rune.Value == 'Q') { _app?.RequestStop(); return true; }
        if (rune.Value == 'l' || rune.Value == 'L' || rune.Value == 'r' || rune.Value == 'R') { ToggleRightPane(); return true; }
        if (rune.Value == 'e' || rune.Value == 'E') { TogglePreviewMode(); return true; }
        if (rune.Value == 'd' || rune.Value == 'D') { EnterDoctor(); return true; }
        // winget-tui keybindings:
        //   i → compact install modal (one screen, sensible defaults)
        //   I → advanced install wizard (multi-step InstallScreen)
        // The Installed view is reached via `2` (jump-to-tab) or ←/→ cycling.
        if (rune.Value == 'I') { StageInstall(forceAdvanced: true); return true; }
        if (rune.Value == 'i') { StageInstall(forceAdvanced: false); return true; }
        if (rune.Value == 'o' || rune.Value == 'O') { OpenSelected(); return true; }
        // `u` jumps to the Changes tab (embedded). The actual single-row vs.
        // batch update keys live on the tab itself (u current row, U marked).
        if (rune.Value == 'u' || rune.Value == 'U') { ActivateTab(SkillViewTab.Changes); return true; }
        if (rune.Value == 'c' || rune.Value == 'C') { _workflows.ShowCleanupScreen(); return true; }
        if (key.KeyCode == KeyCode.F1 || rune.Value == '?') { ShowHelp(); return true; }

        // Search-tab sort cycle. Lower-case `s` is unused at this level so
        // accept both — matches winget-tui's `S` semantics while staying
        // permissive about case.
        if ((rune.Value == 'S' || rune.Value == 's') && _activeTab == SkillViewTab.Discover)
        {
            HandleSearchSortKey();
            return true;
        }

        // Tab navigation — direct (1/2/3) and cyclic (←/→).
        if (rune.Value == '1') { ActivateTab(SkillViewTab.Discover); return true; }
        if (rune.Value == '2') { ActivateTab(SkillViewTab.Installed); return true; }
        if (rune.Value == '3') { ActivateTab(SkillViewTab.Changes); return true; }
        if (key.KeyCode == KeyCode.CursorLeft)  { CycleTab(-1); return true; }
        if (key.KeyCode == KeyCode.CursorRight) { CycleTab(+1); return true; }
        return false;
    }

    /// Switch active tab. All three (Discover / Installed / Changes) are
    /// embedded views — flipping the Visible flags swaps them in-place
    /// without re-running the app loop.
    private void ActivateTab(SkillViewTab tab)
    {
        if (tab == _activeTab) return;
        _activeTab = tab;
        _tabBar?.SetActiveTab(tab);

        // Hide every non-Discover tab by default; the requested one is then
        // revealed below.
        if (_installedTab is not null) _installedTab.Visible = false;
        if (_updatesTab   is not null) _updatesTab.Visible   = false;
        if (_changesTab   is not null) _changesTab.Visible   = false;

        switch (tab)
        {
            case SkillViewTab.Discover:
                ShowSearchPanes(true);
                _queryField?.SetFocus();
                break;
            case SkillViewTab.Installed:
                ShowSearchPanes(false);
                if (_installedTab is not null)
                {
                    _installedTab.Visible = true;
                    _ = _installedTab.LoadAsync();
                }
                break;
            case SkillViewTab.Changes:
                ShowSearchPanes(false);
                if (_changesTab is not null)
                {
                    _changesTab.Visible = true;
                    _ = _changesTab.LoadAsync();
                }
                break;
        }
        UpdateContextBar();
    }

    private void ShowSearchPanes(bool visible)
    {
        if (_discoverTab is not null) _discoverTab.Visible = visible;
        // Also explicitly control left-frame visibility to replicate the old
        // per-frame assignment, which has the side-effect of resetting log-mode's
        // hidden left-frame whenever the Discover tab is re-activated.
        if (_leftFrame is not null) _leftFrame.Visible = visible;
    }

    /// Replace whatever tab is currently visible with the Doctor view. We
    /// remember the prior tab so LeaveDoctor can restore it; if Doctor is
    /// already on screen this is a no-op.
    private void EnterDoctor()
    {
        if (_inDoctor || _doctorTab is null) return;
        _tabBeforeDoctor = _activeTab;
        _inDoctor = true;
        ShowSearchPanes(false);
        if (_installedTab is not null) _installedTab.Visible = false;
        if (_updatesTab   is not null) _updatesTab.Visible   = false;
        if (_changesTab   is not null) _changesTab.Visible   = false;

        // Make sure the report is fresh — probe lazily if we never have.
        if (_lastReport is not null)
        {
            _doctorTab.SetReport(_lastReport);
            _doctorTab.Visible = true;
            _doctorTab.SetFocus();
            return;
        }

        // Probe in the background; reveal an empty pane in the meantime so
        // the user sees the screen flip even before the report lands.
        _doctorTab.Visible = true;
        SetBusy("probing environment for Doctor…");
        RunBackground(async cancellationToken =>
        {
            var probed = await _services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            _lastReport = probed;
            Invoke(() =>
            {
                ClearBusy();
                _doctorTab.SetReport(probed);
                _doctorTab.SetFocus();
            });
        }, "doctor");
    }

    private void LeaveDoctor()
    {
        if (!_inDoctor || _doctorTab is null) return;
        _inDoctor = false;
        _doctorTab.Visible = false;
        // Re-enter the previously-active primary tab. We force-set _activeTab
        // to something different first so ActivateTab's no-op guard doesn't
        // suppress the re-show.
        var restore = _tabBeforeDoctor;
        _activeTab = restore == SkillViewTab.Discover ? SkillViewTab.Installed : SkillViewTab.Discover;
        ActivateTab(restore);
    }

    /// Esc handler for the Updates view. When drilled in from Changes, returns
    /// to Changes; otherwise falls back to Discover. Mirrors LeaveDoctor's
    /// no-op-guard bypass.
    private void LeaveUpdates()
    {
        if (_updatesTab is not null) _updatesTab.Visible = false;
        if (_openedUpdatesFromChanges)
        {
            _openedUpdatesFromChanges = false;
            // _activeTab is still Changes since drill-in bypasses ActivateTab.
            // Force the no-op guard by temporarily setting to a different tab.
            _activeTab = SkillViewTab.Discover;
            ActivateTab(SkillViewTab.Changes);
        }
        else
        {
            ActivateTab(SkillViewTab.Discover);
        }
    }

    private void CycleTab(int delta)
    {
        var values = new[] { SkillViewTab.Discover, SkillViewTab.Installed, SkillViewTab.Changes };
        var idx = Array.IndexOf(values, _activeTab);
        if (idx < 0) idx = 0;
        idx = (idx + delta + values.Length) % values.Length;
        ActivateTab(values[idx]);
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
        var agent = _agentField?.Text.Trim();
        var limit = _limitUpDown?.Value ?? GhSkillSearchService.DefaultLimit;
        UpdateContextBar();
        _ = RunSearchAsync(
            query,
            string.IsNullOrEmpty(owner) ? null : owner,
            limit,
            string.IsNullOrEmpty(agent) ? null : agent);
    }

    private void ProbeGhAsync()
    {
        RunBackground(async cancellationToken =>
        {
            var report = await _services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            _lastReport = report;
            _ghPath = report.GhPath;

            var snapshot = await _services.InventoryService.CaptureAsync(
                report.GhPath,
                report.Capabilities,
                new LocalInventoryService.Options(
                    ScanRoots: _options.ScanRoots,
                    AllowHiddenDirs: false),
                cancellationToken
            ).ConfigureAwait(false);

            Invoke(() =>
            {
                if (_hiddenDirsBox is not null)
                {
                    _hiddenDirsBox.Enabled = SupportsHiddenDirToggle(report.Capabilities);
                    if (!_hiddenDirsBox.Enabled)
                    {
                        _hiddenDirsBox.Value = CheckState.UnChecked;
                    }
                    RefreshHiddenDirUi();
                }

                _updatesTab?.RefreshCapabilities();

                if (ShouldAutoOpenInstalledOnStartup(snapshot))
                {
                    _startupInstalledShown = true;
                    // Seed the embedded tab with the snapshot we already have,
                    // then activate it (skips the duplicate inventory scan
                    // LoadAsync would otherwise trigger).
                    if (_installedTab is not null)
                    {
                        ShowSearchPanes(false);
                        _activeTab = SkillViewTab.Installed;
                        _tabBar?.SetActiveTab(SkillViewTab.Installed);
                        _installedTab.LoadSeeded(snapshot);
                    }
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

    private async Task RunSearchAsync(string query, string? owner = null, int? limit = null, string? agent = null)
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
        var generation = System.Threading.Interlocked.Increment(ref _searchGeneration);
        SetBusy($"searching {query}…");
        var cancellationToken = GetRunLifetimeToken();
        try
        {
            var capabilities = _lastReport?.Capabilities ?? CapabilityProfile.Empty;
            var options = new GhSkillSearchService.Options(
                Owner: owner,
                Limit: limit ?? GhSkillSearchService.DefaultLimit);
            var response = await _services.SearchService
                .SearchAsync(_ghPath, query, capabilities, options, cancellationToken)
                .ConfigureAwait(false);
            var results = response.Results;
            var filteredResults = await FilterResultsByAgentAsync(results, agent, cancellationToken).ConfigureAwait(false);
            Invoke(() =>
            {
                if (System.Threading.Interlocked.Read(ref _searchGeneration) != generation)
                {
                    // A newer search has already taken effect — drop these
                    // results silently so we never paint stale data.
                    _services.Logger.Debug("search", $"dropping stale results for generation {generation}");
                    return;
                }
                _resultsNaturalOrder = filteredResults.ToList();
                _results = ApplySearchSort(_resultsNaturalOrder, _searchSort);
                RefreshResultsTable();
                UpdateMetadataPane();
                _resultsTable?.SetFocus();
                _services.Logger.Info("search", $"results loaded: count={_results.Count} rawCount={results.Count} tableFocus={_resultsTable?.HasFocus} queryFocus={_queryField?.HasFocus}");
                if (!_showingLogs)
                {
                    SetPreviewText(_results.Count == 0 ? TuiHelpers.WelcomeHint : TuiHelpers.PreviewHint);
                }
                if (_previewFrame is not null)
                {
                    _previewFrame.Title = "SKILL.md";
                }
                SetStatus(DescribeSearchResults(results.Count, _results.Count, agent));
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _services.Logger.Debug("search", "search canceled during shutdown");
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

    private async Task<IReadOnlyList<SearchResultSkill>> FilterResultsByAgentAsync(
        IReadOnlyList<SearchResultSkill> results,
        string? requestedAgent,
        CancellationToken cancellationToken)
    {
        var normalizedAgent = SearchAgentMetadataCache.NormalizeAgent(requestedAgent);
        if (normalizedAgent is null || _ghPath is null)
        {
            return results;
        }

        var capabilities = _lastReport?.Capabilities ?? CapabilityProfile.Empty;
        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_searchAgentMetadata.Has(result))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(result.Repo))
            {
                _searchAgentMetadata.Store(result, ImmutableArray<string>.Empty);
                continue;
            }

            try
            {
                var preview = await _services.PreviewService
                    .PreviewAsync(
                        _ghPath,
                        capabilities,
                        result.Repo,
                        result.SkillName,
                        allowHiddenDirs: ShouldAllowHiddenDirs(result, HiddenDirsEnabled),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var agents = preview.Succeeded
                    ? SearchAgentMetadataCache.ExtractAgentsFromMarkdown(preview.MarkdownBody ?? preview.Body ?? string.Empty)
                    : ImmutableArray<string>.Empty;
                _searchAgentMetadata.Store(result, agents);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _services.Logger.Warn("search.agent", $"{result.Repo}/{result.SkillName}: {ex.Message}");
                _searchAgentMetadata.Store(result, ImmutableArray<string>.Empty);
            }
        }

        return _searchAgentMetadata.Filter(results, normalizedAgent);
    }

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
        var runCancellationToken = GetRunLifetimeToken();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(runCancellationToken);
            cts.CancelAfter(PreviewTimeout);
            _services.Logger.Info("preview", $"loading {repo}/{pick.SkillName}…");
            var capabilities = _lastReport?.Capabilities ?? CapabilityProfile.Empty;
            var preview = await _services.PreviewService
                .PreviewAsync(
                    _ghPath,
                    capabilities,
                    repo,
                    pick.SkillName,
                    allowHiddenDirs: ShouldAllowHiddenDirs(pick, HiddenDirsEnabled),
                    cancellationToken: cts.Token)
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
        catch (OperationCanceledException) when (runCancellationToken.IsCancellationRequested)
        {
            _services.Logger.Debug("preview", "preview canceled during shutdown");
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

    /// Sort the natural-order results into a new list per the active sort
    /// mode. Pure — extracted so the cycle behavior can be unit-tested.
    internal static List<SearchResultSkill> ApplySearchSort(
        IReadOnlyList<SearchResultSkill> source,
        SearchSort sort) => sort switch
    {
        SearchSort.StarsDesc => source
            .OrderByDescending(s => s.Stars ?? -1)
            .ThenBy(s => s.SkillName, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        SearchSort.NameAsc => source
            .OrderBy(s => s.SkillName, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        SearchSort.NameDesc => source
            .OrderByDescending(s => s.SkillName, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        SearchSort.RepoAsc => source
            .OrderBy(s => s.Repo, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        _ => source.ToList(),
    };

    internal static SearchSort CycleSearchSort(SearchSort current) => current switch
    {
        SearchSort.Off       => SearchSort.StarsDesc,
        SearchSort.StarsDesc => SearchSort.NameAsc,
        SearchSort.NameAsc   => SearchSort.NameDesc,
        SearchSort.NameDesc  => SearchSort.RepoAsc,
        _                    => SearchSort.Off,
    };

    internal static string DescribeSearchSort(SearchSort sort) => sort switch
    {
        SearchSort.StarsDesc => "sort: stars ↓",
        SearchSort.NameAsc   => "sort: name ↑",
        SearchSort.NameDesc  => "sort: name ↓",
        SearchSort.RepoAsc   => "sort: repo ↑",
        _                    => "sort: off (gh order)",
    };

    private void HandleSearchSortKey()
    {
        _searchSort = CycleSearchSort(_searchSort);
        _results = ApplySearchSort(_resultsNaturalOrder, _searchSort);
        RefreshResultsTable();
        SetStatus(DescribeSearchSort(_searchSort));
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

        // Suffix the active sort column with a direction glyph so the user
        // can see at a glance what `S` last did. Inactive columns stay clean.
        var starsHeader = _searchSort == SearchSort.StarsDesc ? "★ ↓" : "★";
        var nameHeader  = _searchSort switch
        {
            SearchSort.NameAsc  => "Name ↑",
            SearchSort.NameDesc => "Name ↓",
            _                   => "Name",
        };
        var repoHeader  = _searchSort == SearchSort.RepoAsc ? "Repo ↑" : "Repo";
        var source = new EnumerableTableSource<SearchResultSkill>(
            _results,
            new Dictionary<string, Func<SearchResultSkill, object>>
            {
                [starsHeader] = s => s.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                [nameHeader]  = s => TuiHelpers.Truncate(s.SkillName, nameW),
                [repoHeader]  = s => TuiHelpers.Truncate(s.Repo, repoW),
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
        if (_discoverTab is null) return;
        var row = _resultsTable?.GetSelectedRow() ?? -1;
        var text = row >= 0 && row < _results.Count
            ? RenderSearchMetadata(_results[row], _lastReport?.Auth)
            : null;
        _discoverTab.DetailPane.SetMetadataContent(text);
    }

    internal static string BuildRepoUrl(GhAuthStatus? auth, string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return string.Empty;
        }

        var host = GetRepoLinkHost(auth) ?? "github.com";
        return $"https://{host}/{repo.Trim()}";
    }

    internal static string RenderSearchMetadata(SearchResultSkill s, GhAuthStatus? auth)
    {
        // One **label**: value pair per line. Labels are bold to anchor the
        // eye on the left edge; values are plain so URLs / paths read clean.
        // Keep the repo row clickable so the sidebar stays compact without a
        // redundant second URL line.
        // Avoid Markdown headings — TG2's renderer expands them into taller
        // blocks and consumes vertical space we'd rather give the preview.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**Skill** : {s.SkillName ?? "(unnamed)"}");
        var repoUrl = BuildRepoUrl(auth, s.Repo);
        sb.AppendLine($"**Repo**  : {FormatRepoValue(s.Repo, repoUrl)}");
        if (s.Stars is { } st)
            sb.AppendLine($"**Stars** : ★ {st.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(s.Path))
            sb.AppendLine($"**Path**  : {s.Path}");
        if (!string.IsNullOrWhiteSpace(s.Namespace))
            sb.AppendLine($"**Ns**    : {s.Namespace}");
        if (!string.IsNullOrWhiteSpace(s.Description))
            sb.AppendLine($"**About** : {s.Description}");
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    private static string FormatRepoValue(string? repo, string repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return "—";
        }

        var trimmedRepo = repo.Trim();
        if (string.IsNullOrEmpty(repoUrl))
        {
            return trimmedRepo;
        }

        return $"[{trimmedRepo}]({EscapeMarkdownLinkDestination(repoUrl)})";
    }

    private static string EscapeMarkdownLinkDestination(string value) =>
        value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal);

    internal static string DescribeSearchResults(int totalCount, int shownCount, string? requestedAgent)
    {
        var normalizedAgent = SearchAgentMetadataCache.NormalizeAgent(requestedAgent);
        if (normalizedAgent is null)
        {
            return shownCount == 0
                ? "no matches"
                : $"{shownCount} result(s) — Enter, p, or v to preview";
        }

        return shownCount == 0
            ? $"no matches for agent {normalizedAgent}"
            : $"{shownCount} of {totalCount} result(s) match {normalizedAgent}";
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

    internal static bool ShouldAllowHiddenDirPreview(SearchResultSkill skill)
    {
        if (string.IsNullOrWhiteSpace(skill.Path))
        {
            return false;
        }

        return skill.Path
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Length > 0 && segment[0] == '.');
    }

    internal static bool ShouldAllowHiddenDirs(SearchResultSkill skill, bool userEnabled) =>
        userEnabled || ShouldAllowHiddenDirPreview(skill);

    internal static bool SupportsHiddenDirToggle(CapabilityProfile capabilities) =>
        capabilities.SupportsAllowHiddenDirs || capabilities.SupportsPreviewAllowHiddenDirs;

    private static string? GetRepoLinkHost(GhAuthStatus? auth)
    {
        if (auth is not { LoggedIn: true })
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(auth.ActiveHost) ? null : auth.ActiveHost.Trim();
    }

    private bool HiddenDirsEnabled => _hiddenDirsBox?.Value == CheckState.Checked;

    private void ToggleHiddenDirAccess()
    {
        if (_hiddenDirsBox is null || !_hiddenDirsBox.Enabled)
        {
            return;
        }

        _hiddenDirsBox.Value = HiddenDirsEnabled ? CheckState.UnChecked : CheckState.Checked;
    }

    private void RefreshHiddenDirUi()
    {
        if (_detailPane is null)
        {
            return;
        }

        var state = HiddenDirsEnabled ? "on" : "off";
        _detailPane.SetActionsText($"[h] Hidden dirs: {state}    [i] Install    [o] Open in browser    [e] Raw / Rendered    [Enter] Preview");
        UpdateContextBar();
    }

    private void UpdateContextBar()
    {
        if (_contextBar is null) return;

        var workspaceName = _activeTab switch
        {
            SkillViewTab.Discover => "Discover",
            SkillViewTab.Installed => "Installed",
            SkillViewTab.Changes => "Changes",
            _ => null
        };

        string? agentLabel = null;
        string? filterLabel = null;

        // For Discover tab, show agent filter and hidden-dirs state.
        if (_activeTab == SkillViewTab.Discover)
        {
            var agent = _agentField?.Text?.Trim();
            if (!string.IsNullOrEmpty(agent))
            {
                agentLabel = $"agent: {agent}";
            }

            if (HiddenDirsEnabled)
            {
                filterLabel = "hidden dirs: on";
            }
        }

        // For Installed tab, show filter text and pin filter state.
        if (_activeTab == SkillViewTab.Installed && _installedTab is not null)
        {
            var filter = _installedTab.GetFilterText();
            if (!string.IsNullOrEmpty(filter))
            {
                filterLabel = $"filter: {filter}";
            }
            var pinFilter = _installedTab.GetPinFilterState();
            if (pinFilter is not null)
            {
                agentLabel = pinFilter;
            }
        }

        // For Changes tab, show count of pending items.
        if (_activeTab == SkillViewTab.Changes && _changesTab is not null)
        {
            var count = _changesTab.GetPendingCount();
            if (count > 0)
            {
                filterLabel = $"{count} pending";
            }
        }

        var state = new ContextBarState(
            Workspace: workspaceName,
            AgentLabel: agentLabel,
            LocationLabel: null,
            ProvenanceLabel: null,
            HealthLabel: null,
            FilterLabel: filterLabel);

        _contextBar.Update(state);
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
                _logPane.Text = log.Length > 0
                    ? TerminalEscapeSanitizer.Sanitize(log) ?? string.Empty
                    : "(no log entries yet)";
            }
        }
    }

    /// Mirror text into both the rendered Markdown pane and the raw
    /// editor pane so toggling between them via `e` keeps the same content.
    private void SetPreviewText(string text)
    {
        var sanitized = TerminalEscapeSanitizer.Sanitize(text) ?? string.Empty;
        if (_previewPane is not null) _previewPane.Text = NormalizeMarkdownLists(sanitized);
        if (_previewRawPane is not null) _previewRawPane.Text = sanitized;
    }

    private void TogglePreviewMode()
    {
        if (_previewPane is null || _previewRawPane is null || _showingLogs) return;
        _showingRawPreview = !_showingRawPreview;
        _previewPane.CanFocus = !_showingRawPreview;
        _previewRawPane.CanFocus = _showingRawPreview;
        _previewPane.Visible = !_showingRawPreview;
        _previewRawPane.Visible = _showingRawPreview;
        SetStatus(_showingRawPreview ? "preview: raw SKILL.md" : "preview: rendered");
    }

    private void ShowPreviewPane()
    {
        _showingLogs = false;
        if (_previewPane is not null) _previewPane.CanFocus = !_showingRawPreview;
        if (_previewRawPane is not null) _previewRawPane.CanFocus = _showingRawPreview;
        if (_previewPane is not null) _previewPane.Visible = !_showingRawPreview;
        if (_previewRawPane is not null) _previewRawPane.Visible = _showingRawPreview;
        if (_metadataFrame is not null) _metadataFrame.Visible = true;
        if (_previewFrame is not null) _previewFrame.Visible = true;
        if (_itemActionsLabel is not null) _itemActionsLabel.Visible = true;
        if (_logPane is not null) _logPane.CanFocus = false;
        if (_logPane is not null) _logPane.Visible = false;
        if (_leftFrame is not null) _leftFrame.Visible = true;
        if (_rightFrame is not null)
        {
            _rightFrame.X = _leftFrame is not null ? Pos.Right(_leftFrame) : 0;
            _rightFrame.Width = Dim.Fill();
        }
        UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
    }

    private void ShowLogPane()
    {
        _showingLogs = true;
        if (_previewPane is not null) _previewPane.CanFocus = false;
        if (_previewPane is not null) _previewPane.Visible = false;
        if (_previewRawPane is not null) _previewRawPane.CanFocus = false;
        if (_previewRawPane is not null) _previewRawPane.Visible = false;
        if (_metadataFrame is not null) _metadataFrame.Visible = false;
        if (_previewFrame is not null) _previewFrame.Visible = false;
        if (_itemActionsLabel is not null) _itemActionsLabel.Visible = false;
        if (_logPane is not null) _logPane.CanFocus = true;
        if (_logPane is not null) _logPane.Visible = true;
        if (_leftFrame is not null) _leftFrame.Visible = false;
        if (_rightFrame is not null)
        {
            _rightFrame.X = 0;
            _rightFrame.Width = Dim.Fill();
        }
        UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
    }

    private void ShowHelp()
    {
        if (_app is null) return;
        HelpOverlay.Show(_app);
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
        var url = BuildRepoUrl(_lastReport?.Auth, pick.Repo);
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
    private void StageInstall(bool forceAdvanced = false)
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
        _workflows.OpenInstallDialog(
            new InstallRequest(
                Repo: pick.Repo,
                SkillName: pick.SkillName,
                RepoPath: pick.Path,
                AllowHiddenDirs: ShouldAllowHiddenDirs(pick, HiddenDirsEnabled)),
            forceAdvanced: forceAdvanced);
    }

    private void FocusSearchFromInstalled()
    {
        _queryField?.SetFocus();
        _queryField?.SelectAll();
        RestoreDefaultStatus();
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
        UpdateStatusStrip(text, level);
        ScheduleStatusAutoClear();
    });

    /// Persistent contextual status (gh version, "gh not found", etc.).
    /// Replaces the auto-clear default and is what transient `SetStatus`
    /// messages fall back to after `StatusAutoClear`.
    private void SetDefaultStatus(string text) => Invoke(() =>
    {
        _defaultStatus = text;
        UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
        CancelStatusAutoClear();
    });

    private void RestoreDefaultStatus() => Invoke(() =>
    {
        UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
        CancelStatusAutoClear();
    });

    private void UpdateStatusStrip(string text, TuiHelpers.NotificationLevel level)
    {
        _currentStatus = text;
        if (_statusStrip is null) return;

        var hints = GetCurrentHints();
        var badges = GetCurrentBadges();
        _statusStrip.Update(text, hints, badges);
    }

    private List<StatusHint> GetCurrentHints()
    {
        if (_showingLogs)
        {
            return [
                new StatusHint("l", "Preview"),
                new StatusHint("?", "Help"),
                new StatusHint("q", "Quit"),
            ];
        }

        return _activeTab switch
        {
            SkillViewTab.Discover => [
                new StatusHint("/", "Search"),
                new StatusHint("1/2/3", "Tabs"),
                new StatusHint("i", "Install"),
                new StatusHint("I", "Install…"),
                new StatusHint("o", "Open"),
                new StatusHint("e", "Raw/Render"),
                new StatusHint("u", "Changes"),
                new StatusHint("c", "Cleanup"),
                new StatusHint("d", "Doctor"),
                new StatusHint("l", "Logs"),
                new StatusHint("?", "Help"),
                new StatusHint("q", "Quit"),
            ],
            SkillViewTab.Installed => [
                new StatusHint("f", "Filter"),
                new StatusHint("s", "Sort"),
                new StatusHint("P", "Pin"),
                new StatusHint("x", "Remove"),
                new StatusHint("o", "Open"),
                new StatusHint("/", "Discover"),
                new StatusHint("?", "Help"),
                new StatusHint("q", "Quit"),
            ],
            SkillViewTab.Changes => [
                new StatusHint("1/2/3", "Tabs"),
                new StatusHint("u", "Updates"),
                new StatusHint("c", "Cleanup"),
                new StatusHint("d", "Doctor"),
                new StatusHint("?", "Help"),
                new StatusHint("q", "Quit"),
            ],
            _ => [
                new StatusHint("?", "Help"),
                new StatusHint("q", "Quit"),
            ],
        };
    }

    private string GetCurrentBadges()
    {
        // Future: surface facets like agent, location, health here
        return string.Empty;
    }

    private void ScheduleStatusAutoClear()
    {
        if (_app is null) return;
        CancelStatusAutoClear();
        _statusToken = _app.AddTimeout(StatusAutoClear, () =>
        {
            _statusToken = null;
            UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
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
        UpdateStatusStrip(text, TuiHelpers.NotificationLevel.Info);
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
        var lifetime = _runLifetime;
        var app = _app;

        if (app is not null)
        {
            if (lifetime?.IsCancellationRequested == true)
            {
                return;
            }

            app.Invoke(() =>
            {
                if (lifetime?.IsCancellationRequested == true)
                {
                    return;
                }

                action();
            });
            return;
        }

        if (!_hasRunLifetime)
        {
            action();
        }
    }

    private bool ShouldAutoOpenInstalledOnStartup(InventorySnapshot snapshot) =>
        ShouldAutoOpenInstalledOnStartup(
            snapshot,
            _startupInstalledShown,
            _userInteractedSinceLaunch);

    private void NoteUserInteraction()
    {
        _userInteractedSinceLaunch = true;
    }

    private void AttachStartupPointerAndKeyTracking(params View?[] views)
    {
        foreach (var view in views)
        {
            if (view is null)
            {
                continue;
            }

            view.MouseEvent += (_, _) => NoteUserInteraction();
            view.KeyDown += (_, _) => NoteUserInteraction();
        }
    }

    private void AttachStartupFocusTracking(params View?[] views)
    {
        foreach (var view in views)
        {
            if (view is null)
            {
                continue;
            }

            view.HasFocusChanged += (_, _) =>
            {
                if (!view.HasFocus)
                {
                    return;
                }

                if (!_startupFocusPrimed)
                {
                    _startupFocusPrimed = true;
                    return;
                }

                NoteUserInteraction();
            };
        }
    }

    internal Window BuildUiForTests() => BuildUi();

    internal TextField? QueryFieldForTests => _queryField;

    internal TextField? OwnerFieldForTests => _ownerField;

    internal TextField? AgentFieldForTests => _agentField;

    internal NumericUpDown<int>? LimitUpDownForTests => _limitUpDown;

    internal CheckBox? HiddenDirsBoxForTests => _hiddenDirsBox;

    internal TableView? ResultsTableForTests => _resultsTable;

    internal bool UserInteractedSinceLaunchForTests => _userInteractedSinceLaunch;

    internal string StatusTextForTests => _currentStatus;

    internal string DefaultStatusForTests => _defaultStatus;

    internal void SetDefaultStatusForTests(string text) => SetDefaultStatus(text);

    internal void FocusSearchFromInstalledForTests() => FocusSearchFromInstalled();

    internal bool ShouldAutoOpenInstalledOnStartupForTests(InventorySnapshot snapshot) =>
        ShouldAutoOpenInstalledOnStartup(snapshot);

    internal SkillViewTab ActiveTabForTests => _activeTab;

    internal SkillView.Ui.Tabs.ChangesTabView? ChangesTabForTests => _changesTab;

    internal ContextBarView? ContextBarForTests => _contextBar;

    internal TabBarView? TabBarForTests => _tabBar;

    internal void ForceActiveTabForTests(SkillViewTab tab)
    {
        _activeTab = tab;
        _tabBar?.SetActiveTab(tab);
        ShowSearchPanes(tab == SkillViewTab.Discover);
        UpdateContextBar();
    }

    /// Fire-and-forget background work with exception guard. Catches any
    /// unhandled exception, logs it, and shows a status bar message so
    /// failures are never silently swallowed.
    private void RunBackground(Func<CancellationToken, Task> work, string operation)
    {
        var cancellationToken = GetRunLifetimeToken();
        _ = Task.Run(async () =>
        {
            try
            {
                await work(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _services.Logger.Debug(operation, $"{operation} canceled during shutdown");
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
        }, cancellationToken);
    }

    private CancellationToken GetRunLifetimeToken() => _runLifetime?.Token ?? CancellationToken.None;
}
