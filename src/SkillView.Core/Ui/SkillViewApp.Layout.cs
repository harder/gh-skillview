using SkillView.Inventory.Models;
using SkillView.Bootstrapping;
using SkillView.Threading;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{

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
        UpdateDiscoverActions();
        _metadataFrame = _discoverTab.DetailPane.MetadataFrame;
        _previewFrame = _discoverTab.DetailPane.PreviewFrame;
        _previewPane = _discoverTab.DetailPane.PreviewPane;
        _previewRawPane = _discoverTab.DetailPane.PreviewRawPane;
        _logPane = _discoverTab.DetailPane.LogPane;

        // Wire event handlers that require SkillViewApp state.
        _resultsTable.KeyDown += (sender, key) =>
        {
            // Terminal.Gui consumes printable keys on an empty table before
            // they reach the window. Keep retry, help and tab shortcuts usable.
            if (_results.Count == 0) OnWindowKeyDown(sender, key);
        };
        _queryField.KeyDown += OnSearchFieldKey;
        _ownerField.KeyDown += OnSearchFieldKey;
        _agentField.KeyDown += OnSearchFieldKey;
        _limitUpDown.KeyDown += OnSearchFieldKey;
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
            onStateChange: RefreshShellChrome,
            lifetimeToken: GetRunLifetimeToken(),
            onCancellationCallbackException:
                CancellationCallbackReporter.For(_services.Logger, "installed tab"),
            onPackageCheck: (snapshot, id, skill) =>
            {
                _workflows.ShowPackageCheck(snapshot, id, skill, () => _installedTab?.CurrentSnapshot, GetRunLifetimeToken());
                _installedTab?.RefreshAvailability();
            },
            onLocalReimport: skill => _workflows.ShowLocalReimport(skill, GetRunLifetimeToken()),
            onLocalSourceSwitch: skill => _workflows.ShowLocalSourceSwitch(skill, GetRunLifetimeToken()),
            availability: _workflows.Availability,
            onCheckUpdates: CheckUpdates,
            onReviewUpdate: skill => OpenUpdatesWorkspace(OperationSubject.From(skill)),
            capturedIssues: () => _changesTab?.CapturedIssues,
            packageCheck: () => _workflows.CurrentPackageCheck,
            onResourceReplacement: (fact, target) =>
            {
                if (!ReferenceEquals(fact, _workflows.CurrentPackageCheck) || !_workflows.PackageRequestIsCurrent(fact.Request)
                    || _installedTab?.CurrentSnapshot is not { } snapshot || !fact.Matches(snapshot, _services.ListAdapter.Generation, fact.Request.Target)
                    || PackageResourceReplacement.Target(fact) != target || _updatesTab is null || _updatesTab.Visible) return;
                OpenUpdatesWorkspace(OperationSubject.From(fact.Request.Target), prepareImmediately: false, fact, target);
            },
            catalogGeneration: () => _services.ListAdapter.Generation,
            onRemovalError: error =>
            {
                _services.Logger.Error("installed.remove", error.ToString());
                SetStatus("removal failed: " + TuiHelpers.ErrorSnippet(error.Message), TuiHelpers.NotificationLevel.Error);
            })
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
            updateRunner: (ghPath, options, token) => _workflows.PrepareUpdatesAsync(ghPath, options, token),
            ghPathProvider: () => _ghPath,
            logger: _services.Logger,
            onLeaveTab: LeaveUpdates,
            onUpdateApplied: () => _services.ListAdapter.Invalidate(),
            applyRunner: (review, hidden, token) => _workflows.ApplyReviewedUpdateAsync(review, hidden, token),
            preserveRunner: (plan, hidden, token) => _workflows.PreserveUpdateAsync(plan, hidden, token),
            unpinRunner: (target, hidden, token) => _workflows.PrepareUnpinAsync(target, hidden, token),
            inspectUndoRunner: (applied, hidden, token) => _workflows.InspectUpdateUndoAsync(applied, hidden, token),
            undoRunner: (review, hidden, token) => _workflows.UndoReviewedUpdateAsync(review, hidden, token),
            resourcePrepareRunner: _workflows.PrepareResourceReplacementAsync,
            resourceCheckRunner: _workflows.CheckResourceReplacementAsync,
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
            onRefresh: () => _services.ListAdapter.Invalidate(),
            availability: _workflows.Availability,
            onCheckUpdates: CheckUpdates,
            homeDirectory: DisplayPath.CaptureHome(_services.Runner),
            packageCheck: () => _workflows.CurrentPackageCheck,
            onPackageCheck: fact => _workflows.ShowRetainedPackageCheck(fact, GetRunLifetimeToken()),
            catalogGeneration: () => _services.ListAdapter.Generation,
            onLeaveTab: () => ActivateTab(SkillViewTab.Discover),
            onStateChange: UpdateContextBar,
            lifetimeToken: GetRunLifetimeToken(),
            onCancellationCallbackException:
                CancellationCallbackReporter.For(_services.Logger, "changes tab"),
            activateEntryAsync: (entry, request) =>
            {
                if (entry.IsCopyReview)
                    return _workflows.ShowCopiesAsync(entry, request,
                        subject => OpenUpdatesWorkspace(subject, prepareImmediately: false), GetRunLifetimeToken());
                if (entry.Cleanup is not null) return _workflows.ShowSelectedCleanupAsync(entry, request);
                if (request.IsCurrent) OpenUpdatesWorkspace(entry.Subject);
                return Task.CompletedTask;
            },
            runTask: RunOwnedTask)
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };

        void InvalidateUpdateObservations() => _workflows.InvalidateAvailability(RefreshUpdateAvailability);
        _services.ListAdapter.Invalidated += InvalidateUpdateObservations;
        window.Disposing += (_, _) => _services.ListAdapter.Invalidated -= InvalidateUpdateObservations;
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
        RefreshShellChrome();

        if (TuiHelpers.IsWarpTerminal)
        {
            SetDefaultStatus("Warp detected — use Ctrl+J instead of Enter (p/v also work for preview)");
        }

        return window;
    }

    private void UpdateContextBar()
    {
        if (_contextBar is null) return;

        var workspaceName = _activeTab switch
        {
            _ when _inDoctor => "Doctor — Environment diagnostics",
            SkillViewTab.Discover => "Discover skills",
            SkillViewTab.Installed => "Installed skills",
            SkillViewTab.Changes => "Issues",
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

        // Installed owns its filter/coverage chrome and needs the row budget
        // for its full-width list at 80x24.
        if (_activeTab == SkillViewTab.Installed && _installedTab is not null)
        {
            ApplyContextBarVisibility(false);
            return;
        }

        // For Changes tab, show count of pending items.
        if (_activeTab == SkillViewTab.Changes && _changesTab is not null)
        {
            var label = _changesTab.GetQueueLabel();
            if (!string.IsNullOrWhiteSpace(label))
            {
                filterLabel = label;
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
        UpdateStatusStrip(string.IsNullOrEmpty(_currentStatus) ? _defaultStatus : _currentStatus, TuiHelpers.NotificationLevel.Info, _currentStatusDiscoverOwned);
    }
}
