using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Threading;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Hosts the TUI and owns its application lifetime. Layout, workspace navigation,
/// discovery, presentation and UI dispatch are grouped in focused partial files.
public sealed partial class SkillViewApp
{
    // Shared MEC app-config name across both host entrypoints (`skillview`
    // and `gh-skillview`) so they resolve the same app-specific config file
    // location rather than one keyed off each binary's own assembly name.
    private const string SkillViewAppName = "SkillView";

    // Keep field initializers together so splitting handlers cannot reorder construction.
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
    private CancellationSource? _runLifetime;
    private readonly LatestRequestGate _previewRequests;
    private readonly LatestRequestGate _searchRequests;
    private readonly SharedAsyncOperation<EnvironmentReport> _environmentProbe;
    private CancellationSource? _discoverLifetime;
    private CancellationSource? _doctorLifetime;
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
    private SkillViewTab _tabBeforeUpdateReview = SkillViewTab.Discover;

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
    private readonly Dictionary<long, (string Text, bool DiscoverOwned)> _busyOperations = new();
    private long _nextBusyOperationId;
    private long? _legacyBusyOperationId;
    private long _activePreviewBusyOperationId;
    private readonly object _visibleLogGate = new();
    private readonly Queue<string> _visibleLogLines = new(MaxVisibleLogLines);
    private int _visibleLogCharacters;
    private int _logRefreshQueued;

    private string _defaultStatus = "ready — press / to search or F1 for help";
    private string _currentStatus = string.Empty;
    private bool _currentStatusDiscoverOwned;
    private object? _statusToken;
    private long _statusVersion;
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
        _previewRequests = new LatestRequestGate(
            CancellationCallbackReporter.For(services.Logger, "preview request"));
        _searchRequests = new LatestRequestGate(
            CancellationCallbackReporter.For(services.Logger, "search request"));
        _environmentProbe = new SharedAsyncOperation<EnvironmentReport>(
            CancellationCallbackReporter.For(services.Logger, "environment probe"));
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
            TryRunBackground,
            FocusSearchFromInstalled,
            RefreshActiveTab);
    }

    internal static bool ShouldOpenInstalledOnStartup(InventorySnapshot snapshot) => snapshot.Skills.Length > 0;

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
        using var runLifetime = new CancellationSource(
            cancellationToken,
            CancellationCallbackReporter.For(_services.Logger, "application"));
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
            var historyStarted = false;
            window.DrawComplete += (_, _) =>
            {
                if (historyStarted) return;
                historyStarted = true;
                StartHistoryHint();
            };

            // TableView routes Enter (View base default), p/v/CursorRight (rebound in
            // ConfigureTableKeyBindings), and Warp's Ctrl+J directly through
            // Command.Accept → the Accepted event on the table. Query field Enter
            // is handled by OnSearchFieldKey. No global key intercept needed.

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
            try
            {
                CancelStatusAutoClear();
                // Close task admission before cancellation so no event racing the
                // run-loop exit can add unowned work after the drain snapshot.
                _backgroundTasks.StopAccepting();
                DeactivateDoctorWorkspace(clearBusy: false);
                DeactivateDiscoverWorkspace(clearBusy: false);
                runLifetime.TryCancel();
                CancelCurrentPreview();
                await _backgroundTasks.DrainAsync().ConfigureAwait(false);
                DisposeLogSubscription();
            }
            finally
            {
                DetachApplicationKeyHandler();
                _runLifetime = null;
                _app = null;
                try
                {
                    window?.Dispose();
                }
                catch (Exception ex)
                {
                    LogUnhandledException(ex);
                    throw;
                }
                finally
                {
                    // The driver owns terminal mouse/paste modes. Always release
                    // it, even if a view or earlier shutdown step throws.
                    app?.Dispose();
                }
            }
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

    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(30);

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
}
