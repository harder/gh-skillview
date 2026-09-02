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

    /// Discover the skills a repo offers (gh ≥ 2.99.0, cli/cli#13548) and let
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
        if (app is null)
        {
            return;
        }

        _setBusy("scanning for cleanup candidates…");
        _runBackground(async cancellationToken =>
        {
            var report = await GetOrProbeReportAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await CaptureInventoryAsync(report, cancellationToken).ConfigureAwait(false);
            var candidates = CleanupClassifier.ClassifyWithCancellation(
                snapshot,
                snapshot.ScannedRoots,
                options: null,
                cancellationToken);
            await _invokeAsync(
                () =>
                {
                    _clearBusy();
                    var screen = new CleanupScreen(
                        app,
                        _services.RemoveService,
                        _services.Logger,
                        candidates,
                        snapshot.ScannedRoots,
                        snapshot.Skills);
                    screen.Show();
                    if (screen.RemovedCount > 0
                        || screen.RemovedFileCount > 0
                        || screen.RemovedDirectoryCount > 0
                        || screen.IgnoredCount > 0)
                    {
                        _services.ListAdapter.Invalidate();
                        // Cleanup can remove or ignore skills while the user is
                        // sitting on the Installed/Updates list. The screen runs as
                        // a modal overlay (not a tab switch), so nothing reloads the
                        // underlying tab on its own — refresh it here so the list
                        // reflects what cleanup just changed.
                        _refreshActiveTab();
                    }

                    _setStatus($"cleanup: removed {screen.RemovedCount}, ignored {screen.IgnoredCount}");
                },
                cancellationToken).ConfigureAwait(false);
        }, "cleanup");
    }



    public Task<InventorySnapshot> CaptureInventorySnapshotAsync(CancellationToken cancellationToken = default)
    {
        // Used by the embedded InstalledTabView / UpdatesTabView for their
        // on-activate snapshot loads. Probes the environment lazily so the
        // first tab activation doesn't hard-fail when gh hasn't been probed
        // yet (mirrors the lazy-probe in ShowDoctor).
        return CaptureForTabAsync(null, cancellationToken);
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

    private Task<InventorySnapshot> CaptureInventoryAsync(
        EnvironmentReport report, CancellationToken cancellationToken, string? scopeFilter = null) =>
        _services.InventoryService.CaptureAsync(
            report.GhPath,
            new LocalInventoryService.Options(
                ScanRoots: _options.ScanRoots,
                AllowHiddenDirs: false,
                FilterScope: scopeFilter),
            cancellationToken);

    /// Drill into the Updates workspace from the Changes queue.
    /// Calls <paramref name="activateUpdates"/> with <paramref name="hideChanges"/> so the caller
    /// controls the concrete tab-view types; the coordinator stays decoupled from UI types.
    internal void OpenUpdatesFromChanges(Action hideChanges, Action<Action> activateUpdates) =>
        activateUpdates(hideChanges);

}

internal sealed record RemoveDialogPlan(
    ImmutableArray<RemoveTarget> Targets,
    RemoveTargetEvaluation? PrimaryEvaluation);
