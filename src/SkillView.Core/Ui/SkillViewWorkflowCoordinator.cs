using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using Terminal.Gui.App;

namespace SkillView.Ui;

internal sealed class SkillViewWorkflowCoordinator
{
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
    private readonly Action<Func<CancellationToken, Task>, string> _runBackground;
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
        Action<Func<CancellationToken, Task>, string> runBackground,
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
    }

    /// Open the install flow. By default takes the compact one-screen modal
    /// matching winget-tui's `i` shortcut — Scope radio + agents row + Install.
    /// If the user picks "Advanced…" (or `forceAdvanced: true` is requested by
    /// the caller for the `I` shortcut), escalates to the full multi-step
    /// <see cref="InstallScreen"/> wizard.
    public void OpenInstallDialog(InstallRequest request, bool forceAdvanced = false)
    {
        var app = _getApp();
        var ghPath = _getGhPath();
        var report = _getLastReport();
        if (app is null || ghPath is null || report is null)
        {
            return;
        }

        if (!forceAdvanced)
        {
            var compact = new InstallConfirmModal(
                app,
                _services.InstallService,
                _services.Logger,
                ghPath,
                request);
            var compactResult = compact.Show();
            switch (compactResult.Outcome)
            {
                case InstallConfirmModal.Outcome.Installed when compactResult.Install is { Succeeded: true } r:
                    _services.ListAdapter.Invalidate();
                    _setStatusWithLevel(
                        $"installed {r.Repo}{(r.SkillName is null ? "" : "/" + r.SkillName)} — rescanning…",
                        TuiHelpers.NotificationLevel.Success);
                    QueueInventoryRescan(report, successStatus: "installed — inventory now {0} skill(s)");
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
            { _setStatus("Selected installation is missing or ambiguous — refresh Changes."); return; }
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
            () => _setStatus("selected cleanup issue is missing or ambiguous — refresh Changes")).ConfigureAwait(false);
    }

    private void ShowCleanupReview(IApplication app, EnvironmentReport report, InventorySnapshot snapshot,
        ImmutableArray<CleanupClassifier.Candidate> candidates)
    {
        var screen = new CleanupScreen(app, _services.RemoveService, _services.Logger, candidates,
            snapshot.ProtectedRoots.ToArray(), snapshot.Skills, ownershipRunner: _services.Runner,
            captureInventory: token => CaptureInventoryAsync(report, token));
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



    public Task<InventorySnapshot> CaptureInventorySnapshotAsync(CancellationToken cancellationToken = default)
    {
        // Used by the embedded InstalledTabView / UpdatesTabView for their
        // on-activate snapshot loads. Probes the environment lazily so the
        // first tab activation doesn't hard-fail when gh hasn't been probed
        // yet (mirrors the lazy-probe in ShowDoctor).
        return CaptureForTabAsync(null, cancellationToken);
    }

    internal void ShowEffectiveLoading(InstalledSkill selected)
    {
        if (_getApp() is not { } app) return;
        EffectiveLoadingScreen.Show(app, _services.Logger, selected, _services.Runner.EffectiveWorkingDirectory,
            (request, token) => new SkillView.Loading.EffectiveSkillLoader(_services.Runner).CaptureInventoryAsync(
                _services.InventoryService, _getGhPath(), request, _options.ScanRoots,
                _options.WorkspaceDirectories, _options.Profiles, token));
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

    /// Scope-filtered capture used by the Installed tab's scope cycle. When
    /// `scopeFilter` is "user"/"project", `gh skill list --scope` does the
    /// filtering server-side; null captures everything (the tab narrows
    /// "custom" in-process, since gh's `--scope` only accepts project|user).
    public Task<InventorySnapshot> CaptureInventorySnapshotAsync(string? scopeFilter, CancellationToken cancellationToken = default)
    {
        return CaptureForTabAsync(scopeFilter, cancellationToken);
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

        var plan = await BuildRemoveDialogPlanAsync(target, snapshot, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _invokeAsync(
                () =>
                {
                    var app = _getApp();
                    if (app is not null)
                    {
                        OpenRemoveDialogInternal(app, target, snapshot, plan);
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
            var targets = RemoveTargetResolver.BuildTargetsWithCancellation(
                target,
                snapshot,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var primaryEvaluation = targets.IsEmpty
                ? null
                : (evaluator ?? RemoveTargetResolver.EvaluateWithCancellation)(targets[0], snapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new RemoveDialogPlan(targets, primaryEvaluation);
        }, cancellationToken);

    private async Task<InventorySnapshot> CaptureForTabAsync(string? scopeFilter, CancellationToken cancellationToken)
    {
        var report = await GetOrProbeReportAsync(cancellationToken).ConfigureAwait(false);
        return await CaptureInventoryAsync(report, cancellationToken, scopeFilter).ConfigureAwait(false);
    }

    private void OpenRemoveDialogInternal(
        IApplication app,
        InstalledSkill target,
        InventorySnapshot snapshot,
        RemoveDialogPlan plan)
    {
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
            var snapshot = await CaptureInventoryAsync(report, cancellationToken).ConfigureAwait(false);
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

    internal Task<InventorySnapshot> CaptureInventoryAsync(
        EnvironmentReport report, CancellationToken cancellationToken, string? scopeFilter = null) =>
        _services.InventoryService.CaptureAsync(
            report.GhPath,
            new LocalInventoryService.Options(
                ScanRoots: _options.ScanRoots, WorkspaceDirectories: _options.WorkspaceDirectories, Profiles: _options.Profiles,
                AllowHiddenDirs: false,
                FilterScope: scopeFilter),
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

    internal async Task<LocalUpdateResponse> ReviewLocalUpdateAsync(LocalUpdateRequest request, bool allowHidden, CancellationToken token)
    {
        var inventory = await CaptureUpdateInventoryAsync(_getGhPath(), allowHidden, token).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var plans = _services.SavedUpdatePlans;
            var plan = request.Plan;
            return request.Action switch
            {
                LocalUpdateAction.Preserve => new LocalUpdateResponse(Plan: plans.PreserveInstalled(plan.Id, plan.PlanFingerprint, inventory, token)),
                LocalUpdateAction.Review => new LocalUpdateResponse(Review: plans.ReviewLocalChanges(plan.Id, plan.PlanFingerprint, inventory, token)),
                LocalUpdateAction.Choose when request.Review is { } review => new LocalUpdateResponse(Review: plans.ChooseLocalChanges(
                    plan.Id, review.ReviewFingerprint, request.Choices, inventory, token,
                    request.Resolutions.Select(input => input with
                    { SourceFile = Path.GetFullPath(input.SourceFile, _services.Runner.EffectiveWorkingDirectory) }).ToArray())),
                LocalUpdateAction.Build when request.Review is { } review => new LocalUpdateResponse(Plan: plans.FinalizeLocalChanges(
                    plan.Id, review.ReviewFingerprint, inventory, token)),
                _ => throw new ArgumentException("The local update action needs its exact captured review.")
            };
        }, token).ConfigureAwait(false);
    }

    /// Drill into the Updates workspace from the Changes queue.
    /// Calls <paramref name="activateUpdates"/> with <paramref name="hideChanges"/> so the caller
    /// controls the concrete tab-view types; the coordinator stays decoupled from UI types.
    internal void OpenUpdatesFromChanges(Action hideChanges, Action<Action> activateUpdates) =>
        activateUpdates(hideChanges);

}

internal sealed record RemoveDialogPlan(
    ImmutableArray<RemoveTarget> Targets,
    RemoveTargetEvaluation? PrimaryEvaluation);
