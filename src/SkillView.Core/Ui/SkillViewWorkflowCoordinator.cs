using SkillView.Maintenance;
using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Threading;
using Terminal.Gui.App;

namespace SkillView.Ui;

internal sealed partial class SkillViewWorkflowCoordinator
{
    private readonly DisplayInventoryCache _displayInventory;
    private long _installHandoffGeneration;
    private readonly TuiServices _services;
    private readonly AppOptions _options;
    private readonly Func<IApplication?> _getApp;
    private readonly Func<string?> _getGhPath;
    private readonly Func<EnvironmentReport?> _getLastReport;
    private readonly Func<CancellationToken, Task<EnvironmentReport>> _getOrProbeReport;
    private readonly Action<string> _setBusy;
    private readonly Action _clearBusy;
    private readonly Action<string> _setStatus;
    private readonly Action<string, TuiHelpers.NotificationLevel> _setStatusWithLevel;
    private readonly Action<Action> _invoke;
    private readonly Func<Action, CancellationToken, Task<bool>> _invokeAsync;
    private readonly Func<Func<CancellationToken, Task>, string, bool> _runBackground;
    private readonly Action _focusSearchFromInstalled;
    private readonly Action _refreshActiveTab;

    public SkillViewWorkflowCoordinator(
        TuiServices services,
        AppOptions options,
        Func<IApplication?> getApp,
        Func<string?> getGhPath,
        Func<EnvironmentReport?> getLastReport,
        Func<CancellationToken, Task<EnvironmentReport>> getOrProbeReport,
        Action<string> setBusy,
        Action clearBusy,
        Action<string> setStatus,
        Action<string, TuiHelpers.NotificationLevel> setStatusWithLevel,
        Action<Action> invoke,
        Func<Action, CancellationToken, Task<bool>> invokeAsync,
        Func<Func<CancellationToken, Task>, string, bool> runBackground,
        Action focusSearchFromInstalled,
        Action refreshActiveTab)
    {
        _services = services;
        _options = options;
        _getApp = getApp;
        _getGhPath = getGhPath;
        _getLastReport = getLastReport;
        _getOrProbeReport = getOrProbeReport;
        _setBusy = setBusy;
        _clearBusy = clearBusy;
        _setStatus = setStatus;
        _setStatusWithLevel = setStatusWithLevel;
        _invoke = invoke;
        _invokeAsync = invokeAsync;
        _runBackground = runBackground;
        _focusSearchFromInstalled = focusSearchFromInstalled;
        _refreshActiveTab = refreshActiveTab;
        _displayInventory = new(() => _services.ListAdapter.Generation, async token =>
        {
            var report = await GetOrProbeReportAsync(token).ConfigureAwait(false);
            return await CaptureInventoryCoreAsync(report, token).ConfigureAwait(false);
        }, _runBackground, onCancellationCallbackException:
            CancellationCallbackReporter.For(_services.Logger, "display inventory capture"));
    }

    /// Open the install flow. By default takes the compact one-screen modal
    /// matching winget-tui's `i` shortcut — Scope radio + agents row + Install.
    /// If the user picks "Advanced…" (or `forceAdvanced: true` is requested by
    /// the caller for the `I` shortcut), escalates to the full multi-step
    /// <see cref="InstallScreen"/> wizard.
    public void OpenInstallDialog(InstallRequest request, bool forceAdvanced = false,
        Action<InstalledInstallHandoff>? revealInstalled = null, Func<bool>? isOwnerActive = null)
    {
        var app = _getApp();
        var ghPath = _getGhPath();
        var report = _getLastReport();
        if (app is null || ghPath is null || report is null)
        {
            return;
        }

        // A mutating dialog may fail after starting; retire earlier observations
        // before handing over control, even when its eventual result is partial.
        _services.ListAdapter.Invalidate();

        if (!forceAdvanced)
        {
            var compact = new InstallConfirmModal(
                app,
                _services.InstallService,
                _services.Logger,
                ghPath,
                request,
                findInstalled: (evidence, token) => CaptureInstallHandoffAsync(evidence, report, token),
                canPublish: found => IsInstallHandoffCurrent(found) && isOwnerActive?.Invoke() == true);
            var compactResult = compact.Show();
            switch (compactResult.Outcome)
            {
                case InstallConfirmModal.Outcome.Installed when compactResult.Install is { Succeeded: true }:
                    if (compactResult.Handoff is { } handoff && IsInstallHandoffCurrent(handoff) && isOwnerActive?.Invoke() == true)
                    {
                        revealInstalled?.Invoke(handoff);
                        return;
                    }
                    _services.ListAdapter.Invalidate();
                    _setStatusWithLevel("Install completed", TuiHelpers.NotificationLevel.Success);
                    return;
                case InstallConfirmModal.Outcome.Failed when compactResult.Install is { } f:
                    _setStatusWithLevel(
                        $"install failed (exit {f.ExitCode}) — see logs (l)",
                        TuiHelpers.NotificationLevel.Error);
                    return;
                case InstallConfirmModal.Outcome.Cancelled:
                    return;
                case InstallConfirmModal.Outcome.EscalateToAdvanced:
                    // Fall through to the advanced wizard below.
                    break;
            }
        }

        var installScreen = new InstallScreen(
            app,
            _services.InstallService,
            _services.Logger,
            ghPath,
            request);
        installScreen.Show();
        if (installScreen.LastResult is { Succeeded: true } result)
        {
            _services.ListAdapter.Invalidate();
            _setStatusWithLevel(
                $"installed {result.Repo}{(result.SkillName is null ? "" : "/" + result.SkillName)} — rescanning…",
                TuiHelpers.NotificationLevel.Success);
            QueueInventoryRescan(
                report,
                successStatus: $"installed — inventory now {{0}} skill(s)");
        }
        else if (installScreen.LastResult is { } failed)
        {
            _setStatusWithLevel($"install failed (exit {failed.ExitCode}) — see logs (l)", TuiHelpers.NotificationLevel.Error);
        }
    }


    internal async Task<InstalledInstallHandoff> CaptureInstallHandoffAsync(
        SkillView.Gh.InstallNavigationEvidence evidence, EnvironmentReport report, CancellationToken token)
    {
        var request = Interlocked.Increment(ref _installHandoffGeneration);
        _services.ListAdapter.Invalidate();
        var generation = _services.ListAdapter.Generation;
        async Task<IReadOnlyList<IReadOnlyList<string>>> CheckContextAsync()
        {
            token.ThrowIfCancellationRequested();
            var context = _services.Runner.WithContext(_services.Runner.EffectiveWorkingDirectory, new Dictionary<string, string>());
            if (evidence.ContextKey != SkillView.Gh.InstallNavigationEvidence.Context(context))
                throw new IOException("The installation context changed; refresh Installed before choosing a copy.");
            var roots = await SkillView.Gh.GhPlacementCatalog.DestinationsAsync(
                new(Agents: evidence.Agents, Scope: evidence.Scope, WorkingDirectory: context.EffectiveWorkingDirectory),
                context, token).ConfigureAwait(false);
            if (!roots.SequenceEqual(evidence.Roots, StringComparer.Ordinal))
                throw new IOException("The project or installation folders changed; refresh Installed before choosing a copy.");
            var folders = await CaptureInstallAgentFoldersAsync(evidence, context, token).ConfigureAwait(false);
            if (evidence.ContextKey != SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner))
                throw new IOException("The installation context changed; refresh Installed before choosing a copy.");
            return folders;
        }
        var agentFolders = await CheckContextAsync().ConfigureAwait(false);
        var snapshot = await CaptureInventoryAsync(report, token, bypassGhCache: true).ConfigureAwait(false);
        var overview = await Task.Run(() => InstalledOverview.Build(snapshot, token), token).ConfigureAwait(false);
        var latestFolders = await CheckContextAsync().ConfigureAwait(false);
        if (agentFolders.Count != latestFolders.Count || agentFolders.Zip(latestFolders).Any(pair =>
            !pair.First.Select(PathIdentity.NormalizeKey).ToHashSet(StringComparer.Ordinal).SetEquals(pair.Second.Select(PathIdentity.NormalizeKey))))
            throw new IOException("The requested installation folders changed; refresh Installed before choosing a copy.");
        var handoff = await Task.Run(() => InstalledInstallHandoffResolver.Resolve(evidence, snapshot, overview, token, agentFolders), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return handoff with { CatalogGeneration = generation, RequestGeneration = request };
    }

    // Post-install navigation only. Keep these catalog lookups out of the
    // install service's final-admission-to-launch interval.
    internal static async Task<IReadOnlyList<IReadOnlyList<string>>> CaptureInstallAgentFoldersAsync(
        SkillView.Gh.InstallNavigationEvidence evidence, SkillView.Subprocess.ProcessRunner context, CancellationToken token)
    {
        var folders = new List<IReadOnlyList<string>>();
        IEnumerable<IReadOnlyList<string>> selections = evidence.Agents.Count == 0 ? [evidence.Agents]
            : evidence.Agents.Select(agent => (IReadOnlyList<string>)[agent]);
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested();
            folders.Add(await SkillView.Gh.GhPlacementCatalog.DestinationsAsync(
                new(Agents: selection, Scope: evidence.Scope, WorkingDirectory: context.EffectiveWorkingDirectory), context, token).ConfigureAwait(false));
        }
        var union = folders.SelectMany(set => set).Select(PathIdentity.NormalizeKey).ToHashSet(StringComparer.Ordinal);
        if (folders.Count == 0 || folders.Any(set => set.Count == 0)
            || !union.SetEquals(evidence.Roots.Select(PathIdentity.NormalizeKey)))
            throw new IOException("The requested installation folders do not match the captured placement; refresh Installed.");
        return folders;
    }

    internal bool IsInstallHandoffCurrent(InstalledInstallHandoff handoff) =>
        handoff.CatalogGeneration == _services.ListAdapter.Generation
        && handoff.RequestGeneration == Interlocked.Read(ref _installHandoffGeneration)
        && handoff.Evidence?.ContextKey == SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner);

    /// Open the "install every skill in a repo" flow (gh 2.94 `--all`,
    /// cli/cli#13471). Reuses the compact modal's scope/agent pickers in
    /// install-all mode; there is no advanced-wizard escalation because the
    /// multi-step <see cref="InstallScreen"/> is single-skill.
    public void OpenInstallAllDialog(InstallRequest request)
    {
        var app = _getApp();
        var ghPath = _getGhPath();
        var report = _getLastReport();
        if (app is null || ghPath is null || report is null)
        {
            return;
        }

        // A mutating dialog may fail after starting; retire earlier observations
        // before handing over control, even when its eventual result is partial.
        _services.ListAdapter.Invalidate();

        var modal = new InstallConfirmModal(
            app,
            _services.InstallService,
            _services.Logger,
            ghPath,
            request,
            installAll: true);
        var result = modal.Show();
        switch (result.Outcome)
        {
            case InstallConfirmModal.Outcome.Installed when result.Install is { Succeeded: true } r:
                _services.ListAdapter.Invalidate();
                _setStatusWithLevel(
                    $"installed all skills from {r.Repo} — rescanning…",
                    TuiHelpers.NotificationLevel.Success);
                QueueInventoryRescan(report, successStatus: "installed — inventory now {0} skill(s)");
                return;
            case InstallConfirmModal.Outcome.Failed when result.Install is { } f:
                _setStatusWithLevel(
                    $"install-all failed (exit {f.ExitCode}) — see logs (l)",
                    TuiHelpers.NotificationLevel.Error);
                return;
            default:
                return;
        }
    }

    /// Discover the skills a repo offers (gh ≥ 2.97.0, cli/cli#13548) and let
    /// the user pick a subset to install via <see cref="RepoSkillPickerModal"/>.
    /// Discovery is a read-only `gh skill install <repo>` (no skill, no --all)
    /// run off the UI thread; on success the populated picker opens. If
    /// discovery fails or the repo lists no skills, falls back to the blunt
    /// install-all modal so the `A` shortcut never dead-ends.
    public void OpenRepoDiscoveryDialog(InstallRequest request)
    {
        var app = _getApp();
        var ghPath = _getGhPath();
        var report = _getLastReport();
        if (app is null || ghPath is null || report is null)
        {
            return;
        }

        _setBusy($"discovering skills in {request.Repo}…");
        _runBackground(async cancellationToken =>
        {
            var listing = await _services.InstallService
                .ListRepoSkillsAsync(ghPath, request.Repo, version: null, request.AllowHiddenDirs, cancellationToken)
                .ConfigureAwait(false);
            await _invokeAsync(
                () =>
                {
                    _clearBusy();
                    if (!listing.Succeeded)
                    {
                        _setStatusWithLevel(
                            $"could not list skills in {request.Repo} (exit {listing.ExitCode}) — opening install-all",
                            TuiHelpers.NotificationLevel.Warn);
                        OpenInstallAllDialog(request);
                        return;
                    }
                    if (listing.Skills.IsDefaultOrEmpty)
                    {
                        _setStatusWithLevel(
                            $"no skills discovered in {request.Repo}",
                            TuiHelpers.NotificationLevel.Warn);
                        return;
                    }

                    var picker = new RepoSkillPickerModal(
                        app,
                        _services.InstallService,
                        _services.Logger,
                        ghPath,
                        request,
                        listing.Skills);
                    _services.ListAdapter.Invalidate();
                    var result = picker.Show();
                    if (result.Outcome == RepoSkillPickerModal.Outcome.Installed && result.InstalledCount > 0)
                    {
                        _services.ListAdapter.Invalidate();
                        var suffix = result.FailedCount > 0 ? $" ({result.FailedCount} failed)" : string.Empty;
                        _setStatusWithLevel(
                            $"installed {result.InstalledCount} skill(s) from {request.Repo}{suffix} — rescanning…",
                            result.FailedCount > 0 ? TuiHelpers.NotificationLevel.Warn : TuiHelpers.NotificationLevel.Success);
                        QueueInventoryRescan(report, successStatus: "installed — inventory now {0} skill(s)");
                    }
                    else if (result.Outcome == RepoSkillPickerModal.Outcome.Failed)
                    {
                        _setStatusWithLevel(
                            $"install failed — {TuiHelpers.ErrorSnippet(result.FirstError)}".TrimEnd(),
                            TuiHelpers.NotificationLevel.Error);
                    }
                    else
                    {
                        // Cancelled (or nothing selected): clear the lingering
                        // "discovering skills in …" busy status the spinner left.
                        _setStatus($"{request.Repo}: no skills installed");
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }, "discover");
    }

    public void ShowCleanupScreen()
    {
        var app = _getApp();
        if (app is null) return;
        _setBusy("scanning for cleanup candidates…");
        _runBackground(async cancellationToken =>
        {
            var report = await GetOrProbeReportAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await CaptureInventoryAsync(report, cancellationToken).ConfigureAwait(false);
            var candidates = CleanupClassifier.ClassifyWithCancellation(snapshot, snapshot.ScannedRoots, null, cancellationToken);
            await _invokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                _clearBusy();
                ShowCleanupReview(app, report, snapshot, candidates);
            }, cancellationToken).ConfigureAwait(false);
        }, "cleanup");
    }

    internal static ImmutableArray<CleanupClassifier.Candidate> FilterCleanupForInstallation(
        ImmutableArray<CleanupClassifier.Candidate> candidates, OperationSubject selected, string issue)
    {
        var matches = candidates.Where(candidate => candidate.Kind.ToString() == issue
            && PathIdentity.Equals(candidate.Path, selected.Path)
            && candidate.Impact?.Subject.InstallationId == selected.InstallationId
            && PathIdentity.Equals(candidate.Impact.Subject.Path, selected.Path)).Take(2).ToImmutableArray();
        return matches.Length == 1 ? matches : [];
    }

    internal async Task ShowCopiesAsync(SkillView.Maintenance.MaintenanceQueueEntry selected,
        ChangesSelectionGate.Lease request, Action<OperationSubject> activateUpdates, CancellationToken lifetimeToken)
    {
        if (_getApp() is not { } app || !request.IsCurrent) return;
        var report = await GetOrProbeReportAsync(request.Token).ConfigureAwait(false);
        var snapshot = await CaptureInventoryAsync(report, request.Token).ConfigureAwait(false);
        var target = SkillView.Maintenance.MaintenanceQueueBuilder.ResolveInstallation(snapshot, selected.Subject);
        var group = target is null ? null : CopiesPresentation.Build(snapshot, target, request.Token);
        if (group is { Copies.Length: > 0 })
        {
            var candidates = CleanupClassifier.ClassifyWithCancellation(snapshot, snapshot.ScannedRoots, null, request.Token);
            group = group with
            {
                CleanupCandidates = group.Copies.Select(skill => (Id: skill.InstallationId,
                        Matches: FilterCleanupForInstallation(candidates, OperationSubject.From(skill), selected.Issue)))
                    .Where(row => row.Matches.Length == 1).ToImmutableDictionary(row => row.Id, row => row.Matches[0])
            };
        }
        await _invokeAsync(() =>
        {
            if (!request.TryEnterDispatch() || lifetimeToken.IsCancellationRequested) return;
            if (group is null || group.Copies.Length == 0)
            { _setStatus("Selected installation is missing or ambiguous — refresh Issues."); return; }
            var choice = CopiesScreen.Show(app, _services.Logger, group,
                (left, right, token) => new SkillView.Maintenance.SkillComparisonService().CompareAsync(left, right, token,
                    inventoryComplete: !group.Partial), lifetimeToken);
            if (choice is null || lifetimeToken.IsCancellationRequested) return;
            if (choice.Action == CopiesAction.Update) activateUpdates(choice.Subject);
            else if (SkillView.Maintenance.MaintenanceQueueBuilder.ResolveInstallation(snapshot, choice.Subject) is not null
                && group.CleanupCandidates.TryGetValue(choice.Subject.InstallationId, out var candidate))
                ShowCleanupReview(app, report, snapshot, [candidate]);
        }, request.Token).ConfigureAwait(false);
    }

    internal async Task ShowSelectedCleanupAsync(SkillView.Maintenance.MaintenanceQueueEntry selected,
        ChangesSelectionGate.Lease request)
    {
        var app = _getApp();
        if (app is null || !request.IsCurrent) return;
        var report = await GetOrProbeReportAsync(request.Token).ConfigureAwait(false);
        await SelectedCleanupHandoff.RunAsync(selected, request,
            token => CaptureInventoryAsync(report, token), _invokeAsync,
            (snapshot, candidates) => ShowCleanupReview(app, report, snapshot, candidates),
            () => _setStatus("selected cleanup issue is missing or ambiguous — refresh Issues")).ConfigureAwait(false);
    }

    private void ShowCleanupReview(IApplication app, EnvironmentReport report, InventorySnapshot snapshot,
        ImmutableArray<CleanupClassifier.Candidate> candidates)
    {
        var screen = new CleanupScreen(app, _services.RemoveService, _services.Logger, candidates,
            snapshot.ProtectedRoots.ToArray(), snapshot.Skills, ownershipRunner: _services.Runner,
            captureInventory: token => CaptureInventoryAsync(report, token));
        _services.ListAdapter.Invalidate();
        screen.Show();
        var changed = screen.RemovedCount > 0 || screen.RemovedFileCount > 0 || screen.RemovedDirectoryCount > 0
            || screen.IgnoredCount > 0 || screen.PreferenceMutationObserved;
        var status = $"Cleanup: removed {screen.RemovedCount} items, {screen.RemovedFileCount} files, {screen.RemovedDirectoryCount} folders; ignored {screen.IgnoredCount}."
            + (screen.CancellationObserved ? " Some operations were canceled." : "");
        if (changed)
        {
            try
            {
                _services.ListAdapter.Invalidate();
                // This callback requests a refresh; it cannot prove completion.
                _refreshActiveTab();
                status += " Inventory refresh requested.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _services.Logger.Error("cleanup.refresh", ex.ToString());
                status += " Inventory refresh failed; refresh manually.";
            }
        }
        _setStatus(status);
    }

    internal static bool MatchesCleanupSelection(CleanupClassifier.Candidate candidate,
        SkillView.Maintenance.MaintenanceQueueEntry selected) =>
        candidate.Kind.ToString() == selected.Issue && PathIdentity.Equals(candidate.Path, selected.Cleanup?.Path ?? selected.Subject.Path)
        && candidate.Impact?.Subject.InstallationId == selected.Subject.InstallationId;



    internal Task<InventorySnapshot> CaptureDisplayInventoryAsync(EnvironmentReport report, CancellationToken token) =>
        _displayInventory.GetAsync(token, ct => CaptureInventoryCoreAsync(report, ct));

    public Task<InventorySnapshot> CaptureInventorySnapshotAsync(CancellationToken cancellationToken = default)
    {
        // Used by the embedded InstalledTabView / UpdatesTabView for their
        // on-activate snapshot loads. Probes the environment lazily so the
        // first tab activation doesn't hard-fail when gh hasn't been probed
        // yet (mirrors the lazy-probe in ShowDoctor).
        return _displayInventory.GetAsync(cancellationToken);
    }

    internal void ShowPackageCheck(InstalledSkill selected, CancellationToken lifetimeToken)
    {
        if (_getApp() is not { } app) return;
        PackageCheckScreen.Show(app, _services.Logger, selected, async token =>
        {
            var inventory = await _services.InventoryService.CaptureAsync(null,
                new(_options.ScanRoots, true, WorkspaceDirectories: _options.WorkspaceDirectories,
                    Profiles: _options.Profiles), token).ConfigureAwait(false);
            var matches = inventory.Skills.Where(skill => skill.InstallationId == selected.InstallationId
                && PathIdentity.Equals(skill.ResolvedPath, selected.ResolvedPath)).Take(2).ToArray();
            if (matches.Length != 1) throw new IOException("The selected installation changed; refresh Installed before checking it.");
            return await new SkillView.PackageChecking.StaticPackageChecker().CheckAsync(matches[0], token).ConfigureAwait(false);
        }, lifetimeToken);
    }

    internal void ShowLocalReimport(InstalledSkill selected, CancellationToken lifetimeToken)
    {
        if (_getApp() is not { } app) return;
        LocalReimportScreen.Show(app, _services.Logger, selected,
            (request, token) => RunLocalReimportAsync(selected, request, token), lifetimeToken);
        if (!lifetimeToken.IsCancellationRequested) _refreshActiveTab();
    }

    internal void ShowLocalSourceSwitch(InstalledSkill selected, CancellationToken lifetimeToken)
    {
        if (_getApp() is not { } app) return;
        LocalReimportScreen.Show(app, _services.Logger, selected,
            (request, token) => RunLocalSourceSwitchAsync(selected, request, token), lifetimeToken, sourceSwitch: true);
        if (!lifetimeToken.IsCancellationRequested) _refreshActiveTab();
    }

    internal Task<LocalReimportResponse> RunLocalReimportAsync(InstalledSkill selected,
        LocalReimportRequest request, CancellationToken token) => RunLocalImportAsync(selected, request, false, token);

    internal Task<LocalReimportResponse> RunLocalSourceSwitchAsync(InstalledSkill selected,
        LocalReimportRequest request, CancellationToken token) => RunLocalImportAsync(selected, request, true, token);

    private async Task<LocalReimportResponse> RunLocalImportAsync(InstalledSkill selected,
        LocalReimportRequest request, bool sourceSwitch, CancellationToken token)
    {
        var purpose = sourceSwitch ? SkillView.Maintenance.ReplacementPlanStore.LocalSourceSwitchPurpose : SkillView.Maintenance.ReplacementPlanStore.LocalReimportPurpose;
        var disposition = sourceSwitch ? SkillView.Maintenance.ReplacementPlanStore.LocalSourceSwitchDisposition : SkillView.Maintenance.ReplacementPlanStore.LocalReimportDisposition;
        var ghPath = _getGhPath();
        var inventory = await CaptureUpdateInventoryAsync(ghPath, request.AllowHidden, token).ConfigureAwait(false);
        if (request.Action == LocalReimportAction.Prepare)
        {
            if (ghPath is null) throw new IOException("Local reimport preparation requires GitHub CLI.");
            var target = inventory.Skills.SingleOrDefault(skill => skill.InstallationId == selected.InstallationId
                && PathIdentity.Equals(skill.ResolvedPath, selected.ResolvedPath))
                ?? throw new IOException("The selected physical installation changed; refresh before preparing local reimport.");
            return new(Preparation: await Task.Run(() => sourceSwitch
                ? _services.SavedUpdates.PrepareLocalSourceSwitchAsync(ghPath, target, request.NewSourcePath ?? throw new ArgumentException("Choose the new source."), inventory, token)
                : _services.SavedUpdates.PrepareLocalReimportAsync(ghPath, target, inventory, token), token).ConfigureAwait(false));
        }
        if (request.Action == LocalReimportAction.Inspect)
        {
            var inspected = await Task.Run(() => _services.SavedUpdatePlans.Inspect(request.Id!, inventory, token, purpose), token).ConfigureAwait(false);
            RequireSelectedLocalReimport(selected, inspected, sourceSwitch);
            return new(Plan: inspected);
        }
        var review = request.Review ?? throw new ArgumentException("Inspect the saved local reimport before continuing.");
        RequireSelectedLocalReimport(selected, review, sourceSwitch);
        if (request.Id != review.Id) throw new ArgumentException("The saved ID differs from the reviewed local reimport.");
        _services.ListAdapter.Invalidate();
        if (request.Action == LocalReimportAction.Apply)
            return new(Preparation: await Task.Run(() => _services.SavedUpdates.ApplyAsync(review,
                disposition, inventory,
                reconciliation => CaptureUpdateInventoryAsync(ghPath, request.AllowHidden, reconciliation), token), token).ConfigureAwait(false));
        if (request.Action is not (LocalReimportAction.Recover or LocalReimportAction.Undo))
            throw new ArgumentException("Unsupported local reimport action.");
        if (request.Action == LocalReimportAction.Recover && review.AcknowledgeSourceReview && review.SourceReview is { } source)
            await Task.Run(() => _services.SavedUpdatePlans.AcknowledgeSourceReview(review.Id, review.PlanFingerprint,
                source.Fingerprint, inventory, token, purpose), token).ConfigureAwait(false);
        // Reconciliation survives canceled UI delivery and uses its own deadline.
        SkillView.Maintenance.ReplacementResult result;
        string? reconciliationWarning = null;
        try
        {
            result = await Task.Run(() => _services.SavedUpdatePlans.Recover(review.Id,
                review.ReviewFingerprint, inventory, request.Action == LocalReimportAction.Undo, token, purpose), token).ConfigureAwait(false);
        }
        finally
        {
            _services.ListAdapter.Invalidate();
            using var deadline = new SkillView.Threading.CancellationSource(TimeSpan.FromSeconds(10),
                SkillView.Threading.CancellationCallbackReporter.For(_services.Logger, "local reimport reconciliation"));
            try
            {
                var reconciled = await CaptureUpdateInventoryAsync(ghPath, request.AllowHidden, deadline.Token).ConfigureAwait(false);
                if (reconciled.IsPartial || !reconciled.ConfiguredRootsComplete)
                    throw new IOException("Post-attempt inventory is partial; saved evidence remains authoritative.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
            { reconciliationWarning = exception.Message; _services.Logger.Warn("reimport.reconcile", exception.ToString()); }
        }
        return new(Plan: result, ReconciliationWarning: reconciliationWarning);
    }

    private static void RequireSelectedLocalReimport(InstalledSkill selected, SkillView.Maintenance.ReplacementResult review, bool sourceSwitch = false)
    {
        if (!(sourceSwitch ? LocalReimportScreen.IsLocalSourceSwitch(review) : LocalReimportScreen.IsLocalReimport(review)) || !PathIdentity.Equals(selected.ResolvedPath, review.OriginalPath))
            throw new IOException("This saved local reimport belongs to another purpose or physical installation address.");
    }

    public async Task OpenRemoveDialogAsync(
        InstalledSkill target,
        InventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (_getApp() is null)
        {
            return;
        }

        // Display snapshots can be reused across tabs; removal preflight needs
        // current consumers/owners in addition to native execution validation.
        var report = await GetOrProbeReportAsync(cancellationToken).ConfigureAwait(false);
        snapshot = await CaptureInventoryAsync(report, cancellationToken, bypassGhCache: true).ConfigureAwait(false);
        var plan = await BuildRemoveDialogPlanAsync(target, snapshot, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _invokeAsync(
                () =>
                {
                    var app = _getApp();
                    if (app is not null)
                    {
                        OpenRemoveDialogInternal(app, plan.Target, snapshot, plan);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static Task<RemoveDialogPlan> BuildRemoveDialogPlanAsync(
        InstalledSkill target,
        InventorySnapshot snapshot,
        CancellationToken cancellationToken,
        Func<RemoveTarget, InventorySnapshot, CancellationToken, RemoveTargetEvaluation>? evaluator = null) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var freshTarget = SkillView.Maintenance.MaintenanceQueueBuilder.ResolveInstallation(snapshot, OperationSubject.From(target))
                ?? throw new IOException("The selected installation is missing or ambiguous in the fresh inventory; refresh Installed and select it again.");
            var targets = RemoveTargetResolver.BuildTargetsWithCancellation(
                freshTarget,
                snapshot,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var primaryEvaluation = targets.IsEmpty
                ? null
                : (evaluator ?? RemoveTargetResolver.EvaluateWithCancellation)(targets[0], snapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new RemoveDialogPlan(freshTarget, targets, primaryEvaluation);
        }, cancellationToken);

    private void OpenRemoveDialogInternal(
        IApplication app,
        InstalledSkill target,
        InventorySnapshot snapshot,
        RemoveDialogPlan plan)
    {
        _services.ListAdapter.Invalidate();
        RemoveService.RemoveReport? compactAttemptReport = null;

        // Compact path: a single skill with no second-confirm warnings and no
        // validation errors gets the winget-tui `[y] yes  [n] no` confirm.
        // Anything more involved (package/repo group, incoming symlinks,
        // errors) escalates to the full RemoveScreen wizard.
        var targets = plan.Targets;
        if (plan.PrimaryEvaluation is { } evaluation)
        {
            if (RemoveConfirmModal.CanRunCompact(evaluation))
            {
                var modal = new RemoveConfirmModal(app, _services.RemoveService, _services.Logger, target, evaluation);
                var compactResult = modal.Show();
                if (compactResult.Outcome == RemoveConfirmModal.Outcome.Removed
                    && compactResult.Report is { Succeeded: true })
                {
                    _services.ListAdapter.Invalidate();
                    _setStatusWithLevel(
                        $"removed {target.Name} — rescanning…",
                        TuiHelpers.NotificationLevel.Success);
                    var envReportCompact = _getLastReport();
                    if (envReportCompact is not null)
                    {
                        QueueInventoryRescan(envReportCompact, successStatus: "removed — inventory now {0} skill(s)");
                    }
                    return;
                }
                if (compactResult.Outcome == RemoveConfirmModal.Outcome.Failed)
                {
                    if (compactResult.Report is { } failedCompactReport
                        && HasFilesystemChanges(failedCompactReport))
                    {
                        _services.ListAdapter.Invalidate();
                        _setStatusWithLevel(
                            $"partially removed {failedCompactReport.FilesDeleted} file(s); "
                            + $"{failedCompactReport.ErrorCount} error(s) — rescanning…",
                            TuiHelpers.NotificationLevel.Warn);
                        var envReportFailed = _getLastReport();
                        if (envReportFailed is not null)
                        {
                            QueueInventoryRescan(
                                envReportFailed,
                                successStatus: "partial remove — inventory now {0} skill(s)");
                        }
                    }
                    else
                    {
                        _setStatusWithLevel(
                            $"remove failed — {compactResult.Report?.Errors.FirstOrDefault() ?? "see logs (l)"}",
                            TuiHelpers.NotificationLevel.Error);
                    }
                    return;
                }
                if (compactResult.Outcome == RemoveConfirmModal.Outcome.Cancelled)
                {
                    if (compactResult.Report is { } canceledReport
                        && HasFilesystemChanges(canceledReport))
                    {
                        _services.ListAdapter.Invalidate();
                        _setStatusWithLevel(
                            $"remove canceled after {canceledReport.FilesDeleted} file(s) — rescanning…",
                            TuiHelpers.NotificationLevel.Warn);
                        var envReportCanceled = _getLastReport();
                        if (envReportCanceled is not null)
                        {
                            QueueInventoryRescan(
                                envReportCanceled,
                                successStatus: "partial remove — inventory now {0} skill(s)");
                        }
                    }
                    return;
                }
                // Outcome.EscalateToWizard → fall through to the wizard below.
                // Preserve any mutations made by failed compact attempts so
                // closing or failing the wizard cannot suppress the rescan.
                compactAttemptReport = compactResult.Report;
            }
        }

        var screen = new RemoveScreen(
            app,
            _services.RemoveService,
            _services.Logger,
            target,
            snapshot,
            targets,
            plan.PrimaryEvaluation);
        screen.Show();
        var report = screen.LastReport;
        if (compactAttemptReport is { } compactReport)
        {
            var compactBatch = RemoveService.BatchRemoveReport.FromSingle(
                compactReport,
                targetsDeleted: compactReport.Succeeded ? 1 : 0);
            report = report is null
                ? compactBatch
                : RemovalReportState.Accumulate(compactBatch, report);
        }

        if (report is { } completedReport && HasFilesystemChanges(completedReport))
        {
            _services.ListAdapter.Invalidate();
            _setStatusWithLevel(
                completedReport.IsCanceled
                    ? $"remove canceled after {completedReport.FilesDeleted} file(s); "
                        + $"{completedReport.ErrorCount} error(s) — rescanning…"
                    : completedReport.Succeeded
                    ? $"removed {completedReport.TargetsDeleted} skill(s) ({completedReport.FilesDeleted} file(s)) — rescanning…"
                    : $"partially removed {completedReport.TargetsDeleted} skill(s), {completedReport.FilesDeleted} file(s); {completedReport.ErrorCount} error(s) — rescanning…",
                completedReport.Succeeded && !completedReport.IsCanceled
                    ? TuiHelpers.NotificationLevel.Success
                    : TuiHelpers.NotificationLevel.Warn);
            var envReport = _getLastReport();
            if (envReport is not null)
            {
                QueueInventoryRescan(
                    envReport,
                    successStatus: completedReport.Succeeded && !completedReport.IsCanceled
                        ? "removed — inventory now {0} skill(s)"
                        : "partial remove — inventory now {0} skill(s)");
            }
        }
        else if (report is { IsCanceled: true })
        {
            _setStatusWithLevel("remove canceled", TuiHelpers.NotificationLevel.Warn);
        }
        else if (report is { Errors.Length: > 0 } failedReport)
        {
            _setStatusWithLevel(
                $"remove failed — {failedReport.ErrorCount} error(s); no files removed",
                TuiHelpers.NotificationLevel.Error);
        }
    }

    private static bool HasFilesystemChanges(RemoveService.RemoveReport report) =>
        report.FilesDeleted > 0 || report.DirectoriesDeleted > 0;

    private static bool HasFilesystemChanges(RemoveService.BatchRemoveReport report) =>
        report.TargetsDeleted > 0 || report.FilesDeleted > 0 || report.DirectoriesDeleted > 0;

    private void QueueInventoryRescan(EnvironmentReport report, string successStatus)
    {
        _runBackground(async cancellationToken =>
        {
            var snapshot = await CaptureDisplayInventoryAsync(report, cancellationToken).ConfigureAwait(false);
            _invoke(() =>
                _setStatusWithLevel(
                    string.Format(successStatus, snapshot.Skills.Length),
                    TuiHelpers.NotificationLevel.Success));
        }, "rescan");
    }

    private async Task<EnvironmentReport> GetOrProbeReportAsync(CancellationToken cancellationToken)
    {
        return await _getOrProbeReport(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<InventorySnapshot> CaptureInventoryAsync(
        EnvironmentReport report, CancellationToken cancellationToken, bool bypassGhCache = false)
    {
        var capturedGeneration = _services.ListAdapter.Generation;
        var snapshot = await CaptureInventoryCoreAsync(report, cancellationToken, bypassGhCache).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Operation captures may seed browsing; display workers publish through
        // DisplayInventoryCache.LoadAsync alone.
        _displayInventory.PublishFresh(snapshot, capturedGeneration);
        return snapshot;
    }

    private Task<InventorySnapshot> CaptureInventoryCoreAsync(
        EnvironmentReport report, CancellationToken cancellationToken, bool bypassGhCache = false) =>
        _services.InventoryService.CaptureAsync(
            report.GhPath,
            new LocalInventoryService.Options(
                ScanRoots: _options.ScanRoots, WorkspaceDirectories: _options.WorkspaceDirectories, Profiles: _options.Profiles,
                AllowHiddenDirs: false)
            { BypassGhCache = bypassGhCache },
            cancellationToken);

    internal async Task<SkillView.Gh.Models.UpdateResult> PrepareUpdatesAsync(string ghPath,
        SkillView.Gh.GhSkillUpdateService.Options options, CancellationToken token)
    {
        var inventory = await CaptureUpdateInventoryAsync(ghPath, options.AllowHiddenDirectories, token).ConfigureAwait(false);
        return await Task.Run(() => _services.SavedUpdates.PrepareAsync(ghPath, options, inventory, token), token).ConfigureAwait(false);
    }

    internal async Task<SkillView.Gh.Models.UpdateResult> ApplyReviewedUpdateAsync(
        SkillView.Maintenance.ReplacementResult review, bool allowHidden, CancellationToken token)
    {
        _services.ListAdapter.Invalidate();
        var ghPath = _getGhPath();
        var inventory = await CaptureUpdateInventoryAsync(ghPath, allowHidden, token).ConfigureAwait(false);
        return await Task.Run(() => _services.SavedUpdates.ApplyAsync(review, review.PinPolicy is not null ? "unpin-only" : review.LocalDisposition is null ? "retain-original" : "merge-reviewed", inventory,
            reconciliation => CaptureUpdateInventoryAsync(ghPath, allowHidden, reconciliation), token), token).ConfigureAwait(false);
    }

    internal async Task<SkillView.Maintenance.ReplacementResult> InspectUpdateUndoAsync(
        SkillView.Maintenance.ReplacementResult applied, bool allowHidden, CancellationToken token)
    {
        var inventory = await CaptureUpdateInventoryAsync(_getGhPath(), allowHidden, token).ConfigureAwait(false);
        return await Task.Run(() => _services.SavedUpdates.InspectUndo(applied, inventory, token), token).ConfigureAwait(false);
    }

    internal async Task<SkillView.Gh.Models.UpdateResult> UndoReviewedUpdateAsync(
        SkillView.Maintenance.ReplacementResult review, bool allowHidden, CancellationToken token)
    {
        _services.ListAdapter.Invalidate();
        var ghPath = _getGhPath();
        var inventory = await CaptureUpdateInventoryAsync(ghPath, allowHidden, token).ConfigureAwait(false);
        return await Task.Run(() => _services.SavedUpdates.UndoAsync(review, inventory,
            reconciliation => CaptureUpdateInventoryAsync(ghPath, allowHidden, reconciliation), token), token).ConfigureAwait(false);
    }

    internal async Task<SkillView.Gh.Models.UpdateResult> PrepareUnpinAsync(InstalledSkill selected, bool allowHidden, CancellationToken token)
    {
        var inventory = await CaptureUpdateInventoryAsync(_getGhPath(), allowHidden, token).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var target = inventory.Skills.SingleOrDefault(skill => skill.InstallationId == selected.InstallationId
                && PathIdentity.Equals(skill.ResolvedPath, selected.ResolvedPath))
                ?? throw new IOException("The selected physical installation changed; refresh before planning unpin.");
            return _services.SavedUpdates.PrepareUnpin(target, inventory, token);
        }, token).ConfigureAwait(false);
    }

    private Task<InventorySnapshot> CaptureUpdateInventoryAsync(string? ghPath, bool allowHidden, CancellationToken token) =>
        _services.InventoryService.CaptureAsync(ghPath,
            new LocalInventoryService.Options(_options.ScanRoots, allowHidden,
                WorkspaceDirectories: _options.WorkspaceDirectories, Profiles: _options.Profiles), token);

    internal async Task<ReplacementResult> PreserveUpdateAsync(ReplacementResult plan, bool allowHidden, CancellationToken token)
    {
        var inventory = await CaptureUpdateInventoryAsync(_getGhPath(), allowHidden, token).ConfigureAwait(false);
        return await Task.Run(() => _services.SavedUpdatePlans.PreserveInstalled(plan.Id, plan.PlanFingerprint, inventory, token), token).ConfigureAwait(false);
    }

}

internal sealed record RemoveDialogPlan(
    InstalledSkill Target,
    ImmutableArray<RemoveTarget> Targets,
    RemoveTargetEvaluation? PrimaryEvaluation);
