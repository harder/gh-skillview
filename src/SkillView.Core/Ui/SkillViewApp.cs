using System.Collections.Immutable;
using System.Globalization;
using System.Text;
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
    // Shared MEC app-config name across both host entrypoints (`skillview`
    // and `gh-skillview`) so they resolve the same app-specific config file
    // location rather than one keyed off each binary's own assembly name.
    private const string SkillViewAppName = "SkillView";

    private readonly TuiServices _services;
    private readonly AppOptions _options;
    private readonly Func<IApplication> _applicationFactory;
    private readonly bool _probeOnRun;
    private readonly SearchAgentMetadataCache _searchAgentMetadata = new();
    private readonly SearchAgentMetadataLoader _searchAgentMetadataLoader;
    private readonly SkillViewWorkflowCoordinator _workflows;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private const int MaxVisibleLogLines = 512;
    private const int MaxVisibleLogCharacters = 256 * 1024;

    private IApplication? _app;
    private Window? _mainWindow;
    private IDisposable? _logSubscription;
    private CancellationTokenSource? _runLifetime;
    private readonly LatestRequestGate _previewRequests = new();
    private readonly LatestRequestGate _searchRequests = new();
    private readonly SharedAsyncOperation<EnvironmentReport> _environmentProbe = new();
    private CancellationTokenSource? _discoverLifetime;
    private CancellationTokenSource? _doctorLifetime;
    private long _discoverGeneration;
    private long _doctorGeneration;
    private bool _hasEnteredRunLifecycle;
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
    private TabBarView? _tabBar;
    private TerminalSizeGuardView? _sizeGuard;
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

    private const string ItemActionsText = "[Enter] Preview    [i] Install    [o] Open    [?] More";

    private List<SearchResultSkill> _results = new();
    // Original gh-skill-search ordering for the current query — preserved so
    // the `S` sort cycle's "Off" mode can restore it. Re-set by RunSearchAsync
    // on each fresh fetch.
    private List<SearchResultSkill> _resultsNaturalOrder = new();
    private SearchSort _searchSort = SearchSort.Off;
    private volatile string? _ghPath;
    private volatile bool _showingLogs;
    private bool _showingRawPreview;
    private EnvironmentReport? _lastReport;
    // Monotonic generation counter — bumped on each RunSearchAsync invocation
    // and captured at submit time. Result painting checks for stale generation
    // and silently drops out-of-band completions. Mirrors winget-tui's
    // app.view_generation pattern in src/app.rs.
    private long _searchGeneration;
    private volatile bool _userInteractedSinceLaunch;
    private volatile bool _startupInstalledShown;
    private volatile bool _startupFocusPrimed;
    private bool _contextBarShown = true;
    private View? _lastDiscoverFocus;
    private string? _loadedPreviewKey;
    private readonly object _busyGate = new();
    private readonly Dictionary<long, string> _busyOperations = new();
    private long _nextBusyOperationId;
    private long? _legacyBusyOperationId;
    private long _activePreviewBusyOperationId;
    private readonly object _visibleLogGate = new();
    private readonly Queue<string> _visibleLogLines = new(MaxVisibleLogLines);
    private int _visibleLogCharacters;
    private int _logRefreshQueued;

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
        _backgroundTasks = new BackgroundTaskTracker(LogUnhandledException);
        _searchAgentMetadataLoader = new SearchAgentMetadataLoader(_searchAgentMetadata, services.Logger);
        _workflows = new SkillViewWorkflowCoordinator(
            services,
            options,
            () => _app,
            () => _ghPath,
            () => Volatile.Read(ref _lastReport),
            GetOrProbeEnvironmentAsync,
            SetBusy,
            ClearBusy,
            SetStatus,
            SetStatus,
            Invoke,
            InvokeOwnedAsync,
            RunBackground,
            FocusSearchFromInstalled,
            RefreshActiveTab);
    }

    internal static bool ShouldOpenInstalledOnStartup(InventorySnapshot snapshot) => snapshot.Skills.Length > 0;

    internal static bool ShouldAutoOpenInstalledOnStartup(
        InventorySnapshot snapshot,
        bool startupInstalledShown,
        bool userInteractedSinceLaunch) =>
        !startupInstalledShown
        && !userInteractedSinceLaunch
        && ShouldOpenInstalledOnStartup(snapshot);

    // This startup path stays aligned with TG2 AOT guidance and the modern
    // lifecycle (Terminal.Gui 2.4.17+).
    public int Run() => RunAsync().GetAwaiter().GetResult();

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }

        TuiHelpers.SetTheme(_options.Theme);
        // MEC-based replacement for the legacy `ConfigurationManager.Enable
        // (ConfigLocations.All)`: loads library defaults → user files →
        // environment variables → runtime config (same precedence as
        // ConfigLocations.All) and pushes them onto the static scheme/theme
        // facades WingetTuiTheme.Register below still targets.
        new TuiConfigurationBuilder(SkillViewAppName).ApplyToStaticFacades();
        SkillView.Ui.Theming.WingetTuiTheme.Register(_options.Theme);
        if (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }

        IApplication? app = null;
        Window? window = null;
        using var runLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _hasEnteredRunLifecycle = true;
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
                return ExitCodes.Cancelled;
            }

            _app = app;
            ActivateDiscoverWorkspace();
            window = BuildUi();

            // TableView routes Enter (View base default), p/v/CursorRight (rebound in
            // ConfigureTableKeyBindings), and Warp's Ctrl+J directly through
            // Command.Accept → the Accepted event on the table. Query field Enter
            // is handled by OnQueryFieldKey. No global key intercept needed.

            if (_probeOnRun)
            {
                ProbeGhAsync();
            }

            try
            {
                await app.RunAsync(window, runLifetime.Token, HandleRunLoopException).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runLifetime.IsCancellationRequested)
            {
                // Cancellation is the normal external shutdown contract for
                // the awaitable host. Teardown still runs below, and the
                // method reports the stable CLI-compatible exit code.
            }
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= onUnhandledException;
            CancelStatusAutoClear();
            // Close task admission before cancellation so no event racing the
            // run-loop exit can add unowned work after the drain snapshot.
            _backgroundTasks.StopAccepting();
            DeactivateDoctorWorkspace(clearBusy: false);
            DeactivateDiscoverWorkspace(clearBusy: false);
            runLifetime.Cancel();
            CancelCurrentPreview();
            await _backgroundTasks.DrainAsync().ConfigureAwait(false);
            DisposeLogSubscription();
            DetachApplicationKeyHandler();
            _runLifetime = null;
            _app = null;
            window?.Dispose();
            app?.Dispose();
        }
        return cancellationToken.IsCancellationRequested
            ? ExitCodes.Cancelled
            : ExitCodes.Success;
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
        if (_activeTab == SkillViewTab.Discover && !_inDoctor)
        {
            ActivateDiscoverWorkspace();
        }

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
        _mainWindow = window;
        if (_app is not null)
        {
            // Keyboard.KeyDown is raised before focused views. That makes it
            // the reliable place for app-wide quit keys even when TableView or
            // a text editor would otherwise consume the key first.
            _app.Keyboard.KeyDown += OnApplicationKeyDown;
        }

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
        _leftFrame = _discoverTab.LeftFrame;
        _queryField = _discoverTab.QueryField;
        _ownerField = _discoverTab.OwnerField;
        _limitUpDown = _discoverTab.LimitUpDown;
        _agentField = _discoverTab.AgentField;
        _hiddenDirsBox = _discoverTab.HiddenDirsBox;
        _resultsTable = _discoverTab.ResultsTable;
        _detailPane = _discoverTab.DetailPane;
        _rightFrame = _discoverTab.DetailPane;
        _itemActionsLabel = _discoverTab.DetailPane.ItemActionsLabel;
        _metadataFrame = _discoverTab.DetailPane.MetadataFrame;
        _previewFrame = _discoverTab.DetailPane.PreviewFrame;
        _previewPane = _discoverTab.DetailPane.PreviewPane;
        _previewRawPane = _discoverTab.DetailPane.PreviewRawPane;
        _logPane = _discoverTab.DetailPane.LogPane;

        // Wire event handlers that require SkillViewApp state.
        _queryField.KeyDown += OnQueryFieldKey;
        _ownerField.KeyDown += OnFilterFieldKey;
        _agentField.KeyDown += OnFilterFieldKey;
        _limitUpDown.KeyDown += OnFilterFieldKey;
        _limitUpDown.ValueChanging += (_, e) =>
        {
            NoteUserInteraction();
            if (e.NewValue < 1 || e.NewValue > 200) e.Handled = true;
        };
        _hiddenDirsBox.ValueChanged += (_, _) =>
        {
            NoteUserInteraction();
            RefreshHiddenDirUi();
            UpdateDiscoverActions();
        };

        // Accepted fires on Enter, double-click, p, v, CursorRight, and
        // Ctrl+J (Warp) — all routed through Command.Accept by the View
        // base or our keybindings.
        _resultsTable.Accepted += (_, _) =>
        {
            _services.Logger.Info("preview", "Accepted → calling PreviewSelectedAsync");
            RunOwnedTask(PreviewSelectedAsync, "preview");
        };
        _resultsTable.ValueChanged += (_, _) =>
        {
            CancelCurrentPreview();
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
        _sizeGuard = new TerminalSizeGuardView();
        RefreshHiddenDirUi();

        Func<Action, Task> runOnUi = async action =>
        {
            var cancellationToken = GetRunLifetimeToken();
            if (cancellationToken.IsCancellationRequested) return;
            var app = _app;
            if (app is null) return;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            try
            {
                app.Invoke(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }
                    try { action(); tcs.TrySetResult(); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                });
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            await tcs.Task.ConfigureAwait(false);
        };

        _installedTab = new SkillView.Ui.Tabs.InstalledTabView(
            runOnUi: runOnUi,
            runTask: RunOwnedTask,
            snapshotLoader: token => _workflows.CaptureInventorySnapshotAsync(token),
            onRemove: (skill, snap, token) =>
                _workflows.OpenRemoveDialogAsync(skill, snap, token),
            onLeaveTab: () => ActivateTab(SkillViewTab.Discover),
            onGoToSearch: () => { ActivateTab(SkillViewTab.Discover); FocusSearchFromInstalled(); },
            onStateChange: RefreshShellChrome,
            // Scope cycle (`G`) pushes `--scope` down to `gh skill list`.
            scopedSnapshotLoader: (scope, token) => _workflows.CaptureInventorySnapshotAsync(scope, token),
            lifetimeToken: GetRunLifetimeToken())
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Visible = false,
        };

        _updatesTab = new SkillView.Ui.Tabs.UpdatesTabView(
            runOnUi: runOnUi,
            runTask: RunOwnedTask,
            snapshotLoader: token => _workflows.CaptureInventorySnapshotAsync(token),
            updateRunner: (ghPath, options, token) => _services.UpdateService.UpdateAsync(ghPath, options, token),
            ghPathProvider: () => _ghPath,
            logger: _services.Logger,
            onLeaveTab: LeaveUpdates,
            onUpdateApplied: () => _services.ListAdapter.Invalidate(),
            lifetimeToken: GetRunLifetimeToken())
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
            snapshotLoader: token => _workflows.CaptureInventorySnapshotAsync(token),
            onActivateUpdates: () =>
            {
                _openedUpdatesFromChanges = true;
                _changesTab?.CancelPendingLoad();
                _workflows.OpenUpdatesFromChanges(
                    hideChanges: () => _changesTab!.Visible = false,
                    activateUpdates: hideChanges => _updatesTab!.ActivateFromChanges(hideChanges));
            },
            onActivateCleanup: () => _workflows.ShowCleanupScreen(),
            onActivateDoctor: () => _doctorTab?.ActivateFromChanges(
                hideChanges: () => { if (_changesTab is not null) _changesTab.Visible = false; },
                enterDoctor: EnterDoctor),
            onLeaveTab: () => ActivateTab(SkillViewTab.Discover),
            onStateChange: UpdateContextBar,
            lifetimeToken: GetRunLifetimeToken())
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };

        window.Add(_tabBar, _contextBar, _discoverTab, _installedTab, _updatesTab, _doctorTab,
                   _changesTab,
                   _statusStrip, _sizeGuard);
        window.FrameChanged += (_, _) =>
            _sizeGuard.UpdateForSize(window.Frame.Width, window.Frame.Height);
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
        DisposeLogSubscription();
        lock (_visibleLogGate)
        {
            _visibleLogLines.Clear();
            _visibleLogCharacters = 0;
        }
        _logSubscription = _services.Logger.SubscribeWithReplay(OnLogEntry);
        window.Disposing += (_, _) =>
        {
            DeactivateDoctorWorkspace(clearBusy: false);
            DeactivateDiscoverWorkspace(clearBusy: false);
            DisposeLogSubscription();
            DetachApplicationKeyHandler();
        };
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

    private void OnApplicationKeyDown(object? sender, Key key)
    {
        if (key.Handled) return;
        var app = _app;
        var mainWindow = _mainWindow;
        if (app is null || mainWindow is null) return;

        if (IsUnconditionalQuitKey(key))
        {
            var top = app.TopRunnable;
            if (top is not null && !ReferenceEquals(top, mainWindow))
            {
                app.RequestStop(top);
            }
            app.RequestStop(mainWindow);
            key.Handled = true;
            return;
        }

        if (_sizeGuard?.Visible == true)
        {
            // Do not let hidden controls mutate state while the resize guard
            // is covering them. Ctrl+Q above remains available.
            key.Handled = true;
            return;
        }

        if (ReferenceEquals(app.TopRunnableView, mainWindow)
            && !TextInputHasFocus()
            && key.AsRune.Value is 'q' or 'Q')
        {
            app.RequestStop(mainWindow);
            key.Handled = true;
        }
    }

    private void RememberStickyFocus(View view)
    {
        if (ReferenceEquals(view, _queryField)
            || ReferenceEquals(view, _ownerField)
            || ReferenceEquals(view, _agentField)
            || ReferenceEquals(view, _limitUpDown)
            || ReferenceEquals(view, _hiddenDirsBox)
            || ReferenceEquals(view, _resultsTable))
        {
            _lastDiscoverFocus = view;
        }
    }

    private void RestoreDiscoverFocus()
    {
        if (_lastDiscoverFocus is not null)
        {
            _lastDiscoverFocus.SetFocus();
            return;
        }

        _queryField?.SetFocus();
    }

    /// Centralised single-letter shortcut dispatcher for `window.KeyDown`.
    /// Returns true if the key was consumed.
    private bool OnWindowShortcut(Key key)
    {
        // Ctrl+Q is the unconditional escape hatch. Handle it before the
        // printable-input guard so quitting still works while typing.
        if (IsUnconditionalQuitKey(key))
        {
            _app?.RequestStop();
            return true;
        }

        // Let printable text keep going to the focused input, but keep global
        // navigation (tab arrows, help, slash, etc.) available everywhere.
        if (TextInputHasFocus() && IsPrintableTextInputKey(key))
        {
            return false;
        }

        // Esc at the root of a primary tab would otherwise fall through to
        // Terminal.Gui's default quit-on-Esc and exit the app with no
        // confirmation. Swallow it and hint how to quit instead. Modals,
        // Doctor, Updates, and the search field all handle Esc themselves
        // (setting Handled before this runs), so this only fires at the
        // top-level list where Esc previously meant "lose your session".
        if (key.KeyCode == KeyCode.Esc)
        {
            var leftInput = LeaveTextInput();
            SetStatus(leftInput
                ? "Left the field · q or Ctrl+Q quits"
                : "Press q or Ctrl+Q to quit");
            return true;
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
        if ((rune.Value == 'f' || rune.Value == 'F') && _activeTab == SkillViewTab.Discover)
        {
            OpenDiscoverFilters();
            return true;
        }
        if (rune.Value == 'q' || rune.Value == 'Q') { _app?.RequestStop(); return true; }
        if (rune.Value == 'l' || rune.Value == 'L') { ToggleRightPane(); return true; }
        if (rune.Value == 'r' || rune.Value == 'R') { RefreshActiveTab(); return true; }
        if (rune.Value == 'e' || rune.Value == 'E') { TogglePreviewMode(); return true; }
        if (rune.Value == 'd' || rune.Value == 'D') { EnterDoctor(); return true; }
        // winget-tui keybindings:
        //   i → compact install modal (one screen, sensible defaults)
        //   I → advanced install wizard (multi-step InstallScreen)
        // The Installed view is reached via `2` (jump-to-tab) or ←/→ cycling.
        if (rune.Value == 'I') { StageInstall(forceAdvanced: true); return true; }
        if (rune.Value == 'i') { StageInstall(forceAdvanced: false); return true; }
        // A → discover the skills in the selected result's repo and pick which
        // to install (gh ≥ 2.95 non-interactive listing, cli/cli#13548).
        if (rune.Value == 'A' && _activeTab == SkillViewTab.Discover) { StageInstallAll(); return true; }
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
        if (key.KeyCode == KeyCode.CursorLeft) { CycleTab(-1); return true; }
        if (key.KeyCode == KeyCode.CursorRight) { CycleTab(+1); return true; }
        return false;
    }

    private bool TextInputHasFocus()
    {
        if (_inDoctor) return false;
        if (_activeTab == SkillViewTab.Installed)
        {
            return _installedTab?.FilterHasFocus == true;
        }
        return _activeTab == SkillViewTab.Discover
            && _discoverTab?.Visible == true
            && (_queryField?.HasFocus == true
                || _ownerField?.HasFocus == true
                || _agentField?.HasFocus == true
                || _limitUpDown?.HasFocus == true);
    }

    private bool LeaveTextInput()
    {
        if (!TextInputHasFocus())
        {
            return false;
        }

        if (_activeTab == SkillViewTab.Installed)
        {
            _installedTab?.FocusList();
            return true;
        }

        _resultsTable?.SetFocus();
        return true;
    }

    private static bool IsPrintableTextInputKey(Key key)
    {
        var rune = key.AsRune;
        if (rune.Value == 0)
        {
            return false;
        }

        return !Rune.IsControl(rune)
            && key.KeyCode != KeyCode.CursorLeft
            && key.KeyCode != KeyCode.CursorRight;
    }

    internal static bool IsUnconditionalQuitKey(Key key) =>
        key.KeyCode == (KeyCode.Q | KeyCode.CtrlMask);

    /// Switch active tab. All three (Discover / Installed / Changes) are
    /// embedded views — flipping the Visible flags swaps them in-place
    /// without re-running the app loop.
    private void ActivateTab(SkillViewTab tab)
    {
        var leftDoctor = false;
        if (_inDoctor)
        {
            _inDoctor = false;
            if (_doctorTab is not null) _doctorTab.Visible = false;
            DeactivateDoctorWorkspace(clearBusy: true);
            leftDoctor = true;
        }
        if (leftDoctor && tab == _activeTab)
        {
            _activeTab = tab == SkillViewTab.Discover
                ? SkillViewTab.Installed
                : SkillViewTab.Discover;
        }
        if (tab == _activeTab) return;

        if (_activeTab == SkillViewTab.Discover)
        {
            DeactivateDiscoverWorkspace(clearBusy: true);
        }
        CancelPendingTabWork();
        _activeTab = tab;
        _tabBar?.SetActiveTab(tab);

        // Hide every non-Discover tab by default; the requested one is then
        // revealed below.
        if (_installedTab is not null) _installedTab.Visible = false;
        if (_updatesTab is not null) _updatesTab.Visible = false;
        if (_changesTab is not null) _changesTab.Visible = false;

        switch (tab)
        {
            case SkillViewTab.Discover:
                ActivateDiscoverWorkspace();
                ShowSearchPanes(true);
                RestoreDiscoverFocus();
                break;
            case SkillViewTab.Installed:
                ShowSearchPanes(false);
                if (_installedTab is not null)
                {
                    _installedTab.Visible = true;
                    RunOwnedTask(_installedTab.LoadAsync, "installed.load");
                    var installed = _installedTab;
                    Invoke(() => installed.FocusList());
                }
                break;
            case SkillViewTab.Changes:
                ShowSearchPanes(false);
                if (_changesTab is not null)
                {
                    _changesTab.Visible = true;
                    RunOwnedTask(_changesTab.LoadAsync, "changes.load");
                }
                break;
        }
        UpdateContextBar();
    }

    internal void LoadSearchResultsForTests(IReadOnlyList<SearchResultSkill> results)
    {
        ReplaceSearchResults(results);
    }

    internal LatestRequestGate.Lease BeginPreviewRequestForTests()
    {
        var request = _previewRequests.Begin(DiscoverLifetimeForTests, PreviewTimeout);
        _ = BeginPreviewBusyOperation(request, "preview test…");
        return request;
    }

    internal long BeginBusyOperationForTests(string text) => BeginBusyOperation(text);

    internal void EndBusyOperationForTests(long operation) => EndBusyOperation(operation);

    internal void ShowLogPaneForTests() => ShowLogPane();

    internal void SetPreviewTextForTests(string text) => SetPreviewText(text);

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
        if (_activeTab == SkillViewTab.Discover)
        {
            DeactivateDiscoverWorkspace(clearBusy: true);
        }
        ActivateDoctorWorkspace();
        CancelPendingTabWork();
        ShowSearchPanes(false);
        if (_installedTab is not null) _installedTab.Visible = false;
        if (_updatesTab is not null) _updatesTab.Visible = false;
        if (_changesTab is not null) _changesTab.Visible = false;
        RefreshShellChrome();

        // Make sure the report is fresh — probe lazily if we never have.
        if (Volatile.Read(ref _lastReport) is { } cachedReport)
        {
            _doctorTab.SetReport(cachedReport);
            _doctorTab.Visible = true;
            _doctorTab.SetFocus();
            return;
        }

        // Probe in the background; reveal an empty pane in the meantime so
        // the user sees the screen flip even before the report lands.
        _doctorTab.Visible = true;
        SetBusy("probing environment for Doctor…");
        var doctorGeneration = Interlocked.Read(ref _doctorGeneration);
        var doctorToken = _doctorLifetime?.Token ?? new CancellationToken(canceled: true);
        RunOwnedTask(
            () => ProbeDoctorAsync(doctorGeneration, doctorToken),
            "doctor");
    }

    private void CancelPendingTabWork()
    {
        _installedTab?.CancelPendingWork();
        _changesTab?.CancelPendingLoad();
        _updatesTab?.CancelPendingWork();
    }

    private void LeaveDoctor()
    {
        if (!_inDoctor || _doctorTab is null) return;
        _inDoctor = false;
        _doctorTab.Visible = false;
        DeactivateDoctorWorkspace(clearBusy: true);
        // Re-enter the previously-active primary tab. We force-set _activeTab
        // to something different first so ActivateTab's no-op guard doesn't
        // suppress the re-show.
        var restore = _tabBeforeDoctor;
        RefreshShellChrome();
        _activeTab = restore == SkillViewTab.Discover ? SkillViewTab.Installed : SkillViewTab.Discover;
        ActivateTab(restore);
    }

    private async Task ProbeDoctorAsync(long doctorGeneration, CancellationToken cancellationToken)
    {
        try
        {
            var probed = await GetOrProbeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            Invoke(() =>
            {
                if (!IsDoctorWorkspaceActive(doctorGeneration)) return;
                ClearBusy();
                _doctorTab?.SetReport(probed);
                _doctorTab?.SetFocus();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _services.Logger.Debug("doctor", "Doctor probe canceled during deactivation");
        }
        catch (Exception ex)
        {
            _services.Logger.Error("doctor", ex.Message);
            Invoke(() =>
            {
                if (!IsDoctorWorkspaceActive(doctorGeneration)) return;
                ClearBusy();
                SetStatus($"doctor failed: {TuiHelpers.ErrorSnippet(ex.Message)}",
                    TuiHelpers.NotificationLevel.Error);
            });
        }
    }

    private void ActivateDiscoverWorkspace()
    {
        if (_discoverLifetime is { IsCancellationRequested: false }) return;
        _discoverLifetime?.Dispose();
        _discoverLifetime = CancellationTokenSource.CreateLinkedTokenSource(GetRunLifetimeToken());
        Interlocked.Increment(ref _discoverGeneration);
    }

    private void DeactivateDiscoverWorkspace(bool clearBusy)
    {
        Interlocked.Increment(ref _discoverGeneration);
        _searchRequests.Cancel();
        CancelCurrentPreview();
        var lifetime = Interlocked.Exchange(ref _discoverLifetime, null);
        if (lifetime is not null)
        {
            try { lifetime.Cancel(); }
            finally { lifetime.Dispose(); }
        }
        if (clearBusy)
        {
            ClearAllBusyOperations();
        }
    }

    internal static bool TryCaptureActiveLifetimeToken(
        CancellationTokenSource? lifetime,
        out CancellationToken cancellationToken)
    {
        if (lifetime is null)
        {
            cancellationToken = new CancellationToken(canceled: true);
            return false;
        }

        try
        {
            cancellationToken = lifetime.Token;
        }
        catch (ObjectDisposedException)
        {
            cancellationToken = new CancellationToken(canceled: true);
            return false;
        }

        return !cancellationToken.IsCancellationRequested;
    }

    private void ActivateDoctorWorkspace()
    {
        DeactivateDoctorWorkspace(clearBusy: false);
        _doctorLifetime = CancellationTokenSource.CreateLinkedTokenSource(GetRunLifetimeToken());
        Interlocked.Increment(ref _doctorGeneration);
    }

    private void DeactivateDoctorWorkspace(bool clearBusy)
    {
        Interlocked.Increment(ref _doctorGeneration);
        var lifetime = Interlocked.Exchange(ref _doctorLifetime, null);
        if (lifetime is not null)
        {
            try { lifetime.Cancel(); }
            finally { lifetime.Dispose(); }
        }
        if (clearBusy)
        {
            ClearAllBusyOperations();
        }
    }

    private bool IsDiscoverWorkspaceActive(long generation) =>
        !_inDoctor
        && _activeTab == SkillViewTab.Discover
        && Interlocked.Read(ref _discoverGeneration) == generation
        && _discoverLifetime is { IsCancellationRequested: false };

    private bool IsDoctorWorkspaceActive(long generation) =>
        _inDoctor
        && Interlocked.Read(ref _doctorGeneration) == generation
        && _doctorLifetime is { IsCancellationRequested: false };

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
            LeaveTextInput();
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
        else if (key.KeyCode == KeyCode.Esc)
        {
            key.Handled = true;
            LeaveTextInput();
        }
    }

    private void SubmitSearch()
    {
        var query = _queryField?.Text.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query)) return;
        var owner = _ownerField?.Text.Trim();
        var agent = _agentField?.Text.Trim();
        var limit = _limitUpDown?.Value ?? GhSkillSearchService.DefaultLimit;
        var allowHiddenDirs = HiddenDirsEnabled;
        UpdateContextBar();
        RunOwnedTask(
            () => RunSearchAsync(
                query,
                string.IsNullOrEmpty(owner) ? null : owner,
                limit,
                string.IsNullOrEmpty(agent) ? null : agent,
                allowHiddenDirs),
            "search");
    }

    private void ProbeGhAsync()
    {
        RunBackground(async cancellationToken =>
        {
            var report = await GetOrProbeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            _ghPath = report.GhPath;

            var snapshot = await _services.InventoryService.CaptureAsync(
                report.GhPath,
                new LocalInventoryService.Options(
                    ScanRoots: _options.ScanRoots,
                    AllowHiddenDirs: false),
                cancellationToken
            ).ConfigureAwait(false);

            Invoke(() =>
            {
                if (_hiddenDirsBox is not null)
                {
                    // gh ≥ 2.94 always supports --allow-hidden-dirs.
                    _hiddenDirsBox.Enabled = true;
                    RefreshHiddenDirUi();
                }

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
                        // Defer to the next loop tick so the list focus wins
                        // over Terminal.Gui's post-reflow default that would
                        // otherwise land on the filter field and swallow the
                        // advertised global hotkeys (?, d, x).
                        var tab = _installedTab;
                        Invoke(() => tab.FocusList());
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
            if (!report.GhSkillAvailable)
            {
                SetDefaultStatus("`gh skill` not detected — press 'd' for Doctor");
                return;
            }
            SetDefaultStatus($"gh {report.GhVersion} — press '/' to search, 'd' for Doctor");
        }, "probe");
    }

    private async Task<EnvironmentReport> GetOrProbeEnvironmentAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _lastReport);
        if (cached is not null)
        {
            return cached;
        }

        return await _environmentProbe.GetAsync(async sharedCancellationToken =>
        {
            cached = Volatile.Read(ref _lastReport);
            if (cached is not null)
            {
                return cached;
            }

            var report = await _services.EnvironmentProbe
                .ProbeAsync(sharedCancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _lastReport, report);
            return report;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunSearchAsync(
        string query,
        string? owner = null,
        int? limit = null,
        string? agent = null,
        bool allowHiddenDirs = false)
    {
        if (_ghPath is null)
        {
            SetStatus("cannot search — gh not found", TuiHelpers.NotificationLevel.Error);
            return;
        }
        if (!TryCaptureActiveLifetimeToken(_discoverLifetime, out var discoverToken))
        {
            return;
        }

        CancelCurrentPreview();
        using var request = _searchRequests.Begin(discoverToken, TimeSpan.FromMinutes(2));
        var discoverGeneration = Interlocked.Read(ref _discoverGeneration);
        var generation = System.Threading.Interlocked.Increment(ref _searchGeneration);
        var busyOperation = BeginBusyOperation($"searching {query}…");
        var cancellationToken = request.Token;
        try
        {
            var options = new GhSkillSearchService.Options(
                Owner: owner,
                Limit: limit ?? GhSkillSearchService.DefaultLimit);
            var response = await _services.SearchService
                .SearchAsync(_ghPath, query, options, cancellationToken)
                .ConfigureAwait(false);
            var results = response.Results;
            var filteredResults = await FilterResultsByAgentAsync(
                results,
                agent,
                allowHiddenDirs,
                cancellationToken).ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent
                    || !IsDiscoverWorkspaceActive(discoverGeneration)
                    || System.Threading.Interlocked.Read(ref _searchGeneration) != generation)
                {
                    // A newer search has already taken effect — drop these
                    // results silently so we never paint stale data.
                    _services.Logger.Debug("search", $"dropping stale results for generation {generation}");
                    return;
                }
                ReplaceSearchResults(filteredResults);
                UpdateDiscoverActions();
                _resultsTable?.SetFocus();
                _services.Logger.Info("search", $"results loaded: count={_results.Count} rawCount={results.Count} tableFocus={_resultsTable?.HasFocus} queryFocus={_queryField?.HasFocus}");
                if (!_showingLogs)
                {
                    SetPreviewText(_results.Count == 0 ? TuiHelpers.NoResultsHint : TuiHelpers.PreviewHint);
                }
                if (_previewFrame is not null)
                {
                    _previewFrame.Title = "Preview";
                }
                SetStatus(DescribeSearchResults(results.Count, _results.Count, agent));
            }, discoverToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _services.Logger.Debug("search", "search canceled or superseded");
            if (request.IsCurrent && IsDiscoverWorkspaceActive(discoverGeneration))
            {
                await InvokeAsync(() =>
                {
                    if (request.IsCurrent && IsDiscoverWorkspaceActive(discoverGeneration))
                    {
                        SetStatus("search timed out", TuiHelpers.NotificationLevel.Error);
                    }
                }, discoverToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("search", ex.Message);
            var snippet = TuiHelpers.ErrorSnippet(ex.Message);
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent || !IsDiscoverWorkspaceActive(discoverGeneration)) return;
                SetStatus(snippet.Length > 0
                    ? $"search failed: {snippet}"
                    : "search failed — see logs (l)",
                    TuiHelpers.NotificationLevel.Error);
            }, discoverToken).ConfigureAwait(false);
        }
        finally
        {
            EndBusyOperation(busyOperation);
        }
    }

    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(30);

    private void ReplaceSearchResults(IReadOnlyList<SearchResultSkill> results)
    {
        // A preview may have started against the old table after this search
        // began. Invalidate again at the commit boundary so it cannot paint
        // after the table has been replaced with a different result set.
        CancelCurrentPreview();
        _resultsNaturalOrder = results.ToList();
        _results = ApplySearchSort(_resultsNaturalOrder, _searchSort);
        _loadedPreviewKey = null;
        RefreshResultsTable();
        RefreshDiscoverResultsTitle();
        UpdateMetadataPane();
        UpdatePreviewPlaceholder();
    }

    private async Task<IReadOnlyList<SearchResultSkill>> FilterResultsByAgentAsync(
        IReadOnlyList<SearchResultSkill> results,
        string? requestedAgent,
        bool allowHiddenDirs,
        CancellationToken cancellationToken)
    {
        if (SearchAgentMetadataCache.NormalizeAgent(requestedAgent) is null || _ghPath is null)
        {
            return results;
        }

        var ghPath = _ghPath;
        return await _searchAgentMetadataLoader.FilterAsync(
            results,
            requestedAgent,
            (result, token) => _services.PreviewService.PreviewAsync(
                ghPath,
                result.Repo!,
                result.SkillName,
                allowHiddenDirs: ShouldAllowHiddenDirs(result, allowHiddenDirs),
                cancellationToken: token),
            cancellationToken).ConfigureAwait(false);
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

        if (!TryCaptureActiveLifetimeToken(_discoverLifetime, out var discoverToken))
        {
            return;
        }
        var discoverGeneration = Interlocked.Read(ref _discoverGeneration);
        using var request = _previewRequests.Begin(discoverToken, PreviewTimeout);
        var busyOperation = BeginPreviewBusyOperation(
            request,
            $"preview {repo}/{pick.SkillName}…");
        try
        {
            _services.Logger.Info("preview", $"loading {repo}/{pick.SkillName}…");
            var preview = await _services.PreviewService
                .PreviewAsync(
                    _ghPath,
                    repo,
                    pick.SkillName,
                    allowHiddenDirs: ShouldAllowHiddenDirs(pick, HiddenDirsEnabled),
                    cancellationToken: request.Token)
                .ConfigureAwait(false);
            _services.Logger.Debug("preview", $"PreviewAsync returned: succeeded={preview.Succeeded} exit={preview.ExitCode} bodyLen={preview.Body?.Length ?? 0}");
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent || !IsDiscoverWorkspaceActive(discoverGeneration)) return;
                _loadedPreviewKey = preview.Succeeded ? BuildPreviewSelectionKey(pick) : null;
                SetPreviewText(preview.Succeeded
                    ? preview.MarkdownBody ?? preview.Body ?? "(empty preview)"
                    : $"(preview failed: exit {preview.ExitCode})\n\n{preview.ErrorMessage}");
                if (_previewFrame is not null)
                {
                    _previewFrame.Title = $"Preview — {repo}/{pick.SkillName}";
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
            }, discoverToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (discoverToken.IsCancellationRequested)
        {
            _services.Logger.Debug("preview", "preview canceled during workspace deactivation");
        }
        catch (OperationCanceledException)
        {
            if (!request.IsCurrent)
            {
                _services.Logger.Debug("preview", "preview superseded by a newer selection");
                return;
            }
            _services.Logger.Warn("preview", "preview timed out");
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent || !IsDiscoverWorkspaceActive(discoverGeneration)) return;
                _loadedPreviewKey = null;
                SetPreviewText("(preview timed out)\n\nThe gh subprocess did not respond within 30 seconds.");
                SetStatus("preview timed out", TuiHelpers.NotificationLevel.Error);
            }, discoverToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Logger.Error("preview", ex.Message);
            var snippet = TuiHelpers.ErrorSnippet(ex.Message);
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent || !IsDiscoverWorkspaceActive(discoverGeneration)) return;
                _loadedPreviewKey = null;
                SetPreviewText(snippet.Length > 0
                    ? $"(preview failed)\n\n{snippet}"
                    : "(preview failed)\n\nSee logs for details.");

                SetStatus(snippet.Length > 0
                    ? $"preview failed: {snippet}"
                    : "preview failed — see logs (l)",
                    TuiHelpers.NotificationLevel.Error);
            }, discoverToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _activePreviewBusyOperationId,
                value: 0,
                comparand: busyOperation);
            EndBusyOperation(busyOperation);
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
        SearchSort.Off => SearchSort.StarsDesc,
        SearchSort.StarsDesc => SearchSort.NameAsc,
        SearchSort.NameAsc => SearchSort.NameDesc,
        SearchSort.NameDesc => SearchSort.RepoAsc,
        _ => SearchSort.Off,
    };

    internal static string DescribeSearchSort(SearchSort sort) => sort switch
    {
        SearchSort.StarsDesc => "sort: stars ↓",
        SearchSort.NameAsc => "sort: name ↑",
        SearchSort.NameDesc => "sort: name ↓",
        SearchSort.RepoAsc => "sort: repo ↑",
        _ => "sort: off (gh order)",
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
        var nameHeader = _searchSort switch
        {
            SearchSort.NameAsc => "Name ↑",
            SearchSort.NameDesc => "Name ↓",
            _ => "Name",
        };
        var repoHeader = _searchSort == SearchSort.RepoAsc ? "Repo ↑" : "Repo";
        var source = new EnumerableTableSource<SearchResultSkill>(
            _results,
            new Dictionary<string, Func<SearchResultSkill, object>>
            {
                [starsHeader] = s => s.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                [nameHeader] = s => TuiHelpers.Truncate(s.SkillName, nameW),
                [repoHeader] = s => TuiHelpers.Truncate(s.Repo, repoW),
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

        var row = _resultsTable?.GetSelectedRow() ?? -1;
        if (row < 0 || row >= _results.Count)
        {
            return;
        }

        var pick = _results[row];
        _loadedPreviewKey = null;
        SetPreviewText($"Selected: {pick.Repo}/{pick.SkillName}\n\n{TuiHelpers.PreviewHint}");
    }

    internal static string BuildDiscoverPreviewBodyForTests(
        string? description,
        string previewText,
        bool includeDescription) => BuildDiscoverPreviewBody(description, previewText, includeDescription);

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
        var sb = new System.Text.StringBuilder();
        var repoUrl = BuildRepoUrl(auth, s.Repo);
        AppendSearchMetadataItem(sb, "Name", s.SkillName ?? "(unnamed)");
        AppendSearchMetadataItem(sb, "Repo", FormatRepoValue(s.Repo, repoUrl));
        if (s.Stars is { } st)
            AppendSearchMetadataItem(sb, "Stars", $"★ {st.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(s.Path))
            AppendSearchMetadataItem(sb, "Path", s.Path);
        if (!string.IsNullOrWhiteSpace(s.Namespace))
            AppendSearchMetadataItem(sb, "Namespace", s.Namespace);
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    private static void AppendSearchMetadataItem(System.Text.StringBuilder sb, string label, string value)
    {
        sb.Append("- **");
        sb.Append(label);
        sb.Append(":** ");
        sb.AppendLine(value);
    }

    private static string BuildDiscoverPreviewBody(string? description, string previewText, bool includeDescription)
    {
        var trimmedPreview = previewText.Trim();
        var sanitizedDescription = TerminalEscapeSanitizer.Sanitize(description ?? string.Empty) ?? string.Empty;
        if (!includeDescription || string.IsNullOrWhiteSpace(sanitizedDescription))
        {
            return trimmedPreview;
        }

        var trimmedDescription = sanitizedDescription.Trim();
        if (trimmedPreview.Length == 0)
        {
            return trimmedDescription;
        }

        var sb = new StringBuilder(trimmedDescription.Length + trimmedPreview.Length + 8);
        sb.AppendLine(trimmedDescription);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(trimmedPreview);
        return sb.ToString();
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
                ? "no results"
                : $"{shownCount} result(s)";
        }

        return shownCount == 0
            ? $"no results for {normalizedAgent}"
            : $"{shownCount} of {totalCount} result(s) for {normalizedAgent}";
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

    private static string? GetRepoLinkHost(GhAuthStatus? auth)
    {
        if (auth is not { LoggedIn: true })
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(auth.ActiveHost) ? null : auth.ActiveHost.Trim();
    }

    private bool HiddenDirsEnabled => _hiddenDirsBox?.Value == CheckState.Checked;

    private void RefreshHiddenDirUi()
    {
        RefreshDiscoverFilterSummary();
        UpdateDiscoverActions();
        UpdateContextBar();
    }

    private void RefreshDiscoverFilterSummary()
    {
        if (_discoverTab is null)
        {
            return;
        }

        var summary = DescribeDiscoverFilters(includePrefix: true);
        var showSummary = summary.Length > 0;
        _discoverTab.FilterSummaryLabel.Text = summary;
        _discoverTab.FilterSummaryLabel.Visible = showSummary;
        _discoverTab.ResultsTable.Y = showSummary ? 4 : 3;
    }

    private string DescribeDiscoverFilters(bool includePrefix)
    {
        var summary = SkillView.Ui.Tabs.DiscoverTabView.BuildFilterSummaryForTests(
            owner: _ownerField?.Text.Trim() ?? string.Empty,
            agent: _agentField?.Text.Trim() ?? string.Empty,
            limit: _limitUpDown?.Value ?? GhSkillSearchService.DefaultLimit,
            hiddenDirs: HiddenDirsEnabled);

        return includePrefix
            ? summary
            : summary["Filters: ".Length..];
    }

    private void RefreshDiscoverResultsTitle()
    {
        if (_discoverTab is null)
        {
            return;
        }

        _discoverTab.LeftFrame.Title = _results.Count == 0
            ? "Search Results"
            : $"Search Results ({_results.Count})";
    }

    private void UpdateDiscoverActions()
    {
        if (_detailPane is null)
        {
            return;
        }

        _detailPane.SetActionsText(_results.Count == 0
            ? "[f] Filters    [?] Help"
            : ItemActionsText);
    }

    private void OpenDiscoverFilters()
    {
        if (_app is null || _ownerField is null || _agentField is null || _limitUpDown is null || _hiddenDirsBox is null)
        {
            return;
        }

        var dialog = new DiscoverFiltersDialog(
            _app,
            owner: _ownerField.Text.Trim(),
            agent: _agentField.Text.Trim(),
            limit: _limitUpDown.Value,
            hiddenDirs: HiddenDirsEnabled,
            supportsHiddenDirs: _hiddenDirsBox.Enabled);
        var result = dialog.Show();
        if (!result.Accepted)
        {
            return;
        }

        NoteUserInteraction();
        _ownerField.Text = result.Owner;
        _agentField.Text = result.Agent;
        _limitUpDown.Value = result.Limit;
        _hiddenDirsBox.Value = result.HiddenDirs ? CheckState.Checked : CheckState.UnChecked;
        RefreshHiddenDirUi();

        if (!string.IsNullOrWhiteSpace(_queryField?.Text))
        {
            SubmitSearch();
        }
        else
        {
            SetStatus("discover filters updated");
        }
    }

    private void UpdateContextBar()
    {
        if (_contextBar is null) return;

        var workspaceName = _activeTab switch
        {
            _ when _inDoctor => "Doctor — Environment diagnostics",
            SkillViewTab.Discover => "Discover skills",
            SkillViewTab.Installed => "Installed skills",
            SkillViewTab.Changes => "Review changes",
            _ => null
        };

        string? agentLabel = null;
        string? filterLabel = null;
        string? locationLabel = null;
        string? provenanceLabel = null;
        string? healthLabel = null;

        if (_inDoctor)
        {
            var doctorState = new ContextBarState(
                Workspace: workspaceName,
                AgentLabel: null,
                LocationLabel: null,
                ProvenanceLabel: null,
                HealthLabel: null,
                FilterLabel: null);
            _contextBar.Update(doctorState);
            ApplyContextBarVisibility(ContextBarView.ShouldShowForTests(doctorState));
            return;
        }

        // For Installed, keep the shell chrome focused on active filters/pin
        // state; the selected row's metadata already lives in the detail pane.
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
            var label = _changesTab.GetQueueLabel();
            var count = _changesTab.GetPendingCount();
            if (!string.IsNullOrWhiteSpace(label) && count > 0)
            {
                filterLabel = $"{label} · {count}";
            }
        }

        var state = new ContextBarState(
            Workspace: workspaceName,
            AgentLabel: agentLabel,
            LocationLabel: locationLabel,
            ProvenanceLabel: provenanceLabel,
            HealthLabel: healthLabel,
            FilterLabel: filterLabel);

        _contextBar.Update(state);
        ApplyContextBarVisibility(ContextBarView.ShouldShowForTests(state));
    }

    private void ApplyContextBarVisibility(bool visible)
    {
        if (_contextBar is null)
        {
            return;
        }

        if (_contextBarShown == visible)
        {
            _contextBar.Visible = visible;
            return;
        }

        _contextBarShown = visible;
        _contextBar.Visible = visible;
        var contentY = visible ? 2 : 1;
        var contentFill = visible ? 2 : 1;
        ApplyTopOffset(_discoverTab, contentY, contentFill);
        ApplyTopOffset(_installedTab, contentY, contentFill);
        ApplyTopOffset(_updatesTab, contentY, contentFill);
        ApplyTopOffset(_doctorTab, contentY, contentFill);
        ApplyTopOffset(_changesTab, contentY, contentFill);
        _contextBar.SuperView?.SetNeedsLayout();
        _contextBar.SuperView?.SetNeedsDraw();
    }

    private static void ApplyTopOffset(View? view, int y, int fillBottom)
    {
        if (view is null)
        {
            return;
        }

        view.Y = y;
        view.Height = Dim.Fill(fillBottom);
        view.SetNeedsLayout();
        view.SetNeedsDraw();
    }

    private void RefreshShellChrome()
    {
        UpdateContextBar();
        UpdateStatusStrip(string.IsNullOrEmpty(_currentStatus) ? _defaultStatus : _currentStatus, TuiHelpers.NotificationLevel.Info);
    }

    /// Reload the active tab's data. Invalidates the shared `gh skill list`
    /// cache first so the reload reflects on-disk changes (e.g. a removal made
    /// outside this process). Discover re-runs the current query if one is set.
    private void RefreshActiveTab()
    {
        _services.ListAdapter.Invalidate();
        switch (_activeTab)
        {
            case SkillViewTab.Installed when _installedTab is not null:
                SetStatus("refreshing inventory…");
                RunOwnedTask(_installedTab.LoadAsync, "installed.refresh");
                break;
            case SkillViewTab.Changes when _changesTab is not null:
                SetStatus("refreshing updates…");
                RunOwnedTask(_changesTab.LoadAsync, "changes.refresh");
                break;
            case SkillViewTab.Discover:
                if (!string.IsNullOrEmpty(_queryField?.Text.Trim()))
                {
                    SetStatus("refreshing results…");
                    SubmitSearch();
                }
                break;
        }
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
            UpdatePreviewPlaceholder();
            if (_previewFrame is not null) _previewFrame.Title = "Preview";
        }
        else
        {
            ShowLogPane();
            FlushVisibleLogLines();
        }
    }

    /// Mirror text into both the rendered Markdown pane and the raw
    /// editor pane so toggling between them via `e` keeps the same content.
    private void SetPreviewText(string text)
    {
        var sanitized = TerminalEscapeSanitizer.Sanitize(text) ?? string.Empty;
        var selected = GetSelectedResult();
        var includeDescription = selected is not null
            && !string.Equals(_loadedPreviewKey, BuildPreviewSelectionKey(selected), StringComparison.Ordinal);
        var body = BuildDiscoverPreviewBody(selected?.Description, sanitized, includeDescription);
        if (_previewPane is not null) _previewPane.Text = body;
        if (_previewRawPane is not null) _previewRawPane.Text = body;
    }

    private SearchResultSkill? GetSelectedResult()
    {
        var row = _resultsTable?.GetSelectedRow() ?? -1;
        return row >= 0 && row < _results.Count ? _results[row] : null;
    }

    private static string BuildPreviewSelectionKey(SearchResultSkill skill) =>
        $"{skill.Repo ?? string.Empty}/{skill.SkillName ?? string.Empty}";

    private void TogglePreviewMode()
    {
        if (_previewPane is null || _previewRawPane is null || _showingLogs) return;
        _showingRawPreview = !_showingRawPreview;
        _previewPane.CanFocus = !_showingRawPreview;
        _previewRawPane.CanFocus = _showingRawPreview;
        _previewPane.Visible = !_showingRawPreview;
        _previewRawPane.Visible = _showingRawPreview;
        SetStatus(_showingRawPreview ? "preview: source" : "preview: rendered");
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
        // Logs are an explicit user choice. A preview already in flight must
        // not be allowed to close the log pane when it completes.
        CancelCurrentPreview();
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
        if (!ApplyBusyUi())
        {
            UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
        }
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
                AllowHiddenDirs: ShouldAllowHiddenDirs(pick, HiddenDirsEnabled)),
            forceAdvanced: forceAdvanced);
    }

    /// Discover the skills in the selected result's repo and open the picker
    /// so the user installs a chosen subset (gh ≥ 2.95 non-interactive
    /// listing, cli/cli#13548). Falls back to install-all if discovery fails.
    private void StageInstallAll()
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
        _workflows.OpenRepoDiscoveryDialog(
            new InstallRequest(
                Repo: pick.Repo,
                SkillName: null,
                AllowHiddenDirs: ShouldAllowHiddenDirs(pick, HiddenDirsEnabled)));
    }

    private void FocusSearchFromInstalled()
    {
        _queryField?.SetFocus();
        _queryField?.SelectAll();
        RestoreDefaultStatus();
    }

    private void OnLogEntry(LogEntry entry)
    {
        var line = TerminalEscapeSanitizer.Sanitize(Logger.Format(entry)) ?? string.Empty;
        lock (_visibleLogGate)
        {
            _visibleLogLines.Enqueue(line);
            _visibleLogCharacters += line.Length + 1;
            while (_visibleLogLines.Count > MaxVisibleLogLines
                   || _visibleLogCharacters > MaxVisibleLogCharacters)
            {
                _visibleLogCharacters -= _visibleLogLines.Dequeue().Length + 1;
            }
        }

        // Retain the bounded entry stream even while hidden. Visibility only
        // controls drawing, so opening the pane never needs a racy snapshot
        // replacement.
        if (!_showingLogs) return;

        // Coalesce bursts into one UI refresh. This avoids rebuilding and
        // redrawing the whole log pane once per individual entry.
        if (Interlocked.Exchange(ref _logRefreshQueued, 1) == 0)
        {
            Invoke(FlushVisibleLogLines);
        }
    }

    private void FlushVisibleLogLines()
    {
        string text;
        lock (_visibleLogGate)
        {
            text = _visibleLogLines.Count == 0
                ? "(no log entries yet)"
                : string.Join('\n', _visibleLogLines);

            // Reset while the queue snapshot is protected. An entry enqueued
            // after this point must observe zero and schedule another flush.
            Interlocked.Exchange(ref _logRefreshQueued, 0);
        }
        if (_showingLogs && _logPane is not null)
        {
            _logPane.Text = text;
        }
    }

    private void CancelCurrentPreview()
    {
        _previewRequests.Cancel();
        var busyOperation = Interlocked.Exchange(
            ref _activePreviewBusyOperationId,
            value: 0);
        if (busyOperation != 0)
        {
            EndBusyOperation(busyOperation);
        }
    }

    private void DisposeLogSubscription()
    {
        Interlocked.Exchange(ref _logSubscription, null)?.Dispose();
    }

    private void DetachApplicationKeyHandler()
    {
        if (_app is not null)
        {
            _app.Keyboard.KeyDown -= OnApplicationKeyDown;
        }
        _mainWindow = null;
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
                new StatusHint("Ctrl+Q", "Quit"),
            ];
        }

        if (_inDoctor)
        {
            return [
                new StatusHint("Esc", "Back"),
                new StatusHint("?", "Help"),
                new StatusHint("Ctrl+Q", "Quit"),
            ];
        }

        return _activeTab switch
        {
            SkillViewTab.Discover => [
                new StatusHint("f", "Filters"),
                new StatusHint("1/2/3", "Tabs"),
                new StatusHint("?", "Help"),
                new StatusHint("Ctrl+Q", "Quit"),
            ],
            SkillViewTab.Installed => [
                new StatusHint("f", "Filter"),
                new StatusHint("s", "Sort"),
                new StatusHint("P", "Pins"),
                new StatusHint("G", "Scope"),
                new StatusHint("x", "Remove"),
                new StatusHint("?", "Help"),
                new StatusHint("Ctrl+Q", "Quit"),
            ],
            SkillViewTab.Changes => [
                new StatusHint("Enter", "Open"),
                new StatusHint("c", "Cleanup"),
                new StatusHint("d", "Doctor"),
                new StatusHint("?", "Help"),
                new StatusHint("Ctrl+Q", "Quit"),
            ],
            _ => [
                new StatusHint("?", "Help"),
                new StatusHint("Ctrl+Q", "Quit"),
            ],
        };
    }

    private string GetCurrentBadges()
    {
        var parts = new List<string>();

        // Show active facets from Installed tab selection if applicable.
        if (_activeTab == SkillViewTab.Installed && _installedTab is not null)
        {
            var selected = _installedTab.GetSelectedSkill();
            if (selected is not null)
            {
                var agents = InstalledInventoryFormatter.DescribeAgents(selected);
                if (!string.IsNullOrEmpty(agents))
                {
                    parts.Add($"Agents {agents}");
                }
            }
        }

        return parts.Count > 0 ? string.Join(" ", parts) : string.Empty;
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

    private long BeginBusyOperation(string text)
    {
        long operation;
        lock (_busyGate)
        {
            operation = ++_nextBusyOperationId;
            _busyOperations.Add(operation, text);
        }
        Invoke(RefreshBusyUi);
        return operation;
    }

    private long BeginPreviewBusyOperation(
        LatestRequestGate.Lease request,
        string text)
    {
        var operation = BeginBusyOperation(text);
        var previous = Interlocked.Exchange(
            ref _activePreviewBusyOperationId,
            operation);
        if (previous != 0)
        {
            EndBusyOperation(previous);
        }

        // Cancellation can race the small interval between beginning the
        // request and publishing its busy owner. Remove the just-published
        // owner if the request already lost ownership.
        if (!request.IsCurrent
            && Interlocked.CompareExchange(
                ref _activePreviewBusyOperationId,
                value: 0,
                comparand: operation) == operation)
        {
            EndBusyOperation(operation);
        }
        return operation;
    }

    private void EndBusyOperation(long operation)
    {
        var changed = false;
        lock (_busyGate)
        {
            changed = _busyOperations.Remove(operation);
            if (_legacyBusyOperationId == operation)
            {
                _legacyBusyOperationId = null;
            }
        }
        if (changed)
        {
            Invoke(RefreshBusyUi);
        }
    }

    private void SetBusy(string text)
    {
        lock (_busyGate)
        {
            if (_legacyBusyOperationId is { } previous)
            {
                _busyOperations.Remove(previous);
            }

            var operation = ++_nextBusyOperationId;
            _busyOperations.Add(operation, text);
            _legacyBusyOperationId = operation;
        }
        Invoke(RefreshBusyUi);
    }

    private void ClearBusy()
    {
        long? operation;
        lock (_busyGate)
        {
            operation = _legacyBusyOperationId;
        }
        if (operation is { } active)
        {
            EndBusyOperation(active);
        }
    }

    private void ClearAllBusyOperations()
    {
        lock (_busyGate)
        {
            _busyOperations.Clear();
            _legacyBusyOperationId = null;
        }
        Interlocked.Exchange(ref _activePreviewBusyOperationId, value: 0);
        Invoke(RefreshBusyUi);
    }

    private void RefreshBusyUi() => _ = ApplyBusyUi();

    private bool ApplyBusyUi()
    {
        string? text = null;
        long newest = 0;
        lock (_busyGate)
        {
            foreach (var operation in _busyOperations)
            {
                if (operation.Key <= newest) continue;
                newest = operation.Key;
                text = operation.Value;
            }
        }

        var busy = text is not null;
        _statusStrip?.SetBusy(busy);
        if (busy)
        {
            UpdateStatusStrip(text!, TuiHelpers.NotificationLevel.Info);
        }
        return busy;
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

        // Unit helpers build the view before a real run and intentionally use
        // direct dispatch. Once lifecycle entry has happened, a missing app
        // always means teardown or post-teardown and must be a no-op.
        if (!_hasEnteredRunLifecycle)
        {
            action();
        }
    }

    private async Task<bool> InvokeAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        var lifetime = _runLifetime;
        var app = _app;

        if (app is not null)
        {
            using var dispatchLifetime = lifetime is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.Token);
            return await AwaitDispatchAsync(
                    app.Invoke,
                    action,
                    dispatchLifetime.Token)
                .ConfigureAwait(false);
        }

        if (!_hasEnteredRunLifecycle && !cancellationToken.IsCancellationRequested)
        {
            action();
            return true;
        }
        return false;
    }

    private async Task<bool> InvokeOwnedAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        var lifetime = _runLifetime;
        var app = _app;

        if (app is not null)
        {
            using var dispatchLifetime = lifetime is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.Token);
            return await AwaitOwnedDispatchAsync(
                    app.Invoke,
                    action,
                    dispatchLifetime.Token)
                .ConfigureAwait(false);
        }

        if (!_hasEnteredRunLifecycle && !cancellationToken.IsCancellationRequested)
        {
            action();
            return true;
        }
        return false;
    }

    internal static async Task<bool> AwaitDispatchAsync(
        Action<Action> dispatch,
        Action action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatch(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetResult(false);
                return;
            }

            try
            {
                action();
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        try
        {
            return await completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    internal static async Task<bool> AwaitOwnedDispatchAsync(
        Action<Action> dispatch,
        Action action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // 0 = queued, 1 = callback started, 2 = canceled before start.
        // Cancellation may reject a queued callback, but once the callback has
        // begun the owner must wait for it to return before releasing state the
        // callback still uses (notably a nested modal run loop).
        var dispatchState = 0;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref dispatchState, 2, 0) == 0)
            {
                completion.TrySetResult(false);
            }
        });

        dispatch(() =>
        {
            if (Interlocked.CompareExchange(ref dispatchState, 1, 0) != 0)
            {
                completion.TrySetResult(false);
                return;
            }

            try
            {
                action();
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return await completion.Task.ConfigureAwait(false);
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

                RememberStickyFocus(view);

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

    internal CancellationToken DiscoverLifetimeForTests =>
        TryCaptureActiveLifetimeToken(_discoverLifetime, out var token)
            ? token
            : new CancellationToken(canceled: true);

    internal CancellationToken DoctorLifetimeForTests =>
        _doctorLifetime?.Token ?? new CancellationToken(canceled: true);

    internal SkillView.Ui.Tabs.ChangesTabView? ChangesTabForTests => _changesTab;

    internal ContextBarView? ContextBarForTests => _contextBar;

    internal StatusStripView? StatusStripForTests => _statusStrip;

    internal IReadOnlyList<StatusHint> CurrentHintsForTests => GetCurrentHints();

    internal string PreviewTextForTests => _previewPane?.Text.ToString() ?? string.Empty;

    internal int VisibleLogCharacterCountForTests
    {
        get
        {
            lock (_visibleLogGate)
            {
                return _visibleLogCharacters;
            }
        }
    }

    internal TabBarView? TabBarForTests => _tabBar;

    internal SkillView.Ui.Tabs.InstalledTabView? InstalledTabForTests => _installedTab;

    internal void ForceActiveTabForTests(SkillViewTab tab)
    {
        _activeTab = tab;
        _tabBar?.SetActiveTab(tab);
        ShowSearchPanes(tab == SkillViewTab.Discover);
        if (tab == SkillViewTab.Discover)
        {
            RestoreDiscoverFocus();
        }
        UpdateContextBar();
    }

    internal void ActivateTabForTests(SkillViewTab tab) => ActivateTab(tab);

    internal void EnterDoctorForTests(EnvironmentReport report)
    {
        Volatile.Write(ref _lastReport, report);
        EnterDoctor();
    }

    internal void LeaveDoctorForTests() => LeaveDoctor();

    /// Fire-and-forget background work with exception guard. Catches any
    /// unhandled exception, logs it, and shows a status bar message so
    /// failures are never silently swallowed.
    private void RunBackground(Func<CancellationToken, Task> work, string operation)
    {
        var cancellationToken = GetRunLifetimeToken();
        RunOwnedTask(async () =>
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
        }, operation, runOnThreadPool: true);
    }

    private void RunOwnedTask(Func<Task> work, string operation) =>
        RunOwnedTask(work, operation, runOnThreadPool: false);

    private void RunOwnedTask(Func<Task> work, string operation, bool runOnThreadPool)
    {
        if (!_backgroundTasks.TryRun(work, runOnThreadPool))
        {
            _services.Logger.Debug(operation, $"{operation} skipped during shutdown");
        }
    }

    internal void RunBackgroundForTests(Func<CancellationToken, Task> work, string operation) =>
        RunBackground(work, operation);

    private CancellationToken GetRunLifetimeToken() => _runLifetime?.Token ?? CancellationToken.None;
}
