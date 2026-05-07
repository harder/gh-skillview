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
    private readonly Action<EnvironmentReport> _rememberReport;
    private readonly Action<string> _setBusy;
    private readonly Action _clearBusy;
    private readonly Action<string> _setStatus;
    private readonly Action<string, TuiHelpers.NotificationLevel> _setStatusWithLevel;
    private readonly Action<Action> _invoke;
    private readonly Action<Func<CancellationToken, Task>, string> _runBackground;
    private readonly Action _focusSearchFromInstalled;

    public SkillViewWorkflowCoordinator(
        TuiServices services,
        AppOptions options,
        Func<IApplication?> getApp,
        Func<string?> getGhPath,
        Func<EnvironmentReport?> getLastReport,
        Action<EnvironmentReport> rememberReport,
        Action<string> setBusy,
        Action clearBusy,
        Action<string> setStatus,
        Action<string, TuiHelpers.NotificationLevel> setStatusWithLevel,
        Action<Action> invoke,
        Action<Func<CancellationToken, Task>, string> runBackground,
        Action focusSearchFromInstalled)
    {
        _services = services;
        _options = options;
        _getApp = getApp;
        _getGhPath = getGhPath;
        _getLastReport = getLastReport;
        _rememberReport = rememberReport;
        _setBusy = setBusy;
        _clearBusy = clearBusy;
        _setStatus = setStatus;
        _setStatusWithLevel = setStatusWithLevel;
        _invoke = invoke;
        _runBackground = runBackground;
        _focusSearchFromInstalled = focusSearchFromInstalled;
    }

    public void OpenInstallDialog(InstallRequest request)
    {
        var app = _getApp();
        var ghPath = _getGhPath();
        var report = _getLastReport();
        if (app is null || ghPath is null || report is null)
        {
            return;
        }

        var installScreen = new InstallScreen(
            app,
            _services.InstallService,
            _services.Logger,
            ghPath,
            report.Capabilities,
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

    public void ShowUpdateScreen()
    {
        var app = _getApp();
        var ghPath = _getGhPath();
        var report = _getLastReport();
        if (app is null)
        {
            return;
        }

        if (ghPath is null || report is null)
        {
            _setStatus("gh not ready — press 'd' for Doctor");
            return;
        }

        _setBusy("scanning inventory for update picker…");
        _runBackground(async cancellationToken =>
        {
            var snapshot = await CaptureInventoryAsync(report, cancellationToken).ConfigureAwait(false);
            _invoke(() =>
            {
                _clearBusy();
                var screen = new UpdateScreen(
                    app,
                    _services.UpdateService,
                    _services.Logger,
                    ghPath,
                    report.Capabilities,
                    snapshot.Skills);
                screen.Show();
                if (screen.LastResult is { DryRun: false, Succeeded: true })
                {
                    _services.ListAdapter.Invalidate();
                    _setStatusWithLevel("update succeeded — rescanning…", TuiHelpers.NotificationLevel.Success);
                    QueueInventoryRescan(report, successStatus: "updated — inventory now {0} skill(s)");
                }
                else if (screen.LastResult is { Succeeded: false } failed)
                {
                    _setStatusWithLevel($"update failed (exit {failed.ExitCode}) — see logs (l)", TuiHelpers.NotificationLevel.Error);
                }
            });
        }, "update");
    }

    public void ShowInstalled()
    {
        if (_getApp() is null)
        {
            return;
        }

        _setBusy("scanning inventory…");
        _runBackground(async cancellationToken =>
        {
            var report = await GetOrProbeReportAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await CaptureInventoryAsync(report, cancellationToken).ConfigureAwait(false);
            _invoke(() =>
            {
                _clearBusy();
                _setStatus($"{snapshot.Skills.Length} installed skill(s)");
                OpenInstalledSnapshot(snapshot);
            });
        }, "installed");
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
            var candidates = CleanupClassifier.Classify(snapshot, snapshot.ScannedRoots);
            _invoke(() =>
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
                if (screen.RemovedCount > 0)
                {
                    _services.ListAdapter.Invalidate();
                }

                _setStatus($"cleanup: removed {screen.RemovedCount}, ignored {screen.IgnoredCount}");
            });
        }, "cleanup");
    }

    public void ShowDoctor()
    {
        var app = _getApp();
        if (app is null)
        {
            return;
        }

        var report = _getLastReport();
        if (report is not null)
        {
            DoctorScreen.Show(app, report);
            return;
        }

        _setBusy("probing environment…");
        _runBackground(async cancellationToken =>
        {
            var probed = await _services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            _rememberReport(probed);
            _invoke(() =>
            {
                _clearBusy();
                DoctorScreen.Show(app, probed);
            });
        }, "doctor");
    }

    public void OpenInstalledSnapshot(InventorySnapshot snapshot)
    {
        var app = _getApp();
        if (app is null)
        {
            return;
        }

        InstalledScreen.Show(
            app,
            snapshot,
            target => OpenRemoveDialog(target, snapshot),
            _focusSearchFromInstalled);
    }

    private void OpenRemoveDialog(InstalledSkill target, InventorySnapshot snapshot)
    {
        var app = _getApp();
        if (app is null)
        {
            return;
        }

        var validation = RemoveValidator.Validate(target, snapshot.ScannedRoots, snapshot.Skills);
        var screen = new RemoveScreen(app, _services.RemoveService, _services.Logger, target, validation);
        screen.Show();
        if (screen.LastReport is { Succeeded: true } report)
        {
            _services.ListAdapter.Invalidate();
            _setStatusWithLevel(
                $"removed {target.Name} ({report.FilesDeleted} file(s)) — rescanning…",
                TuiHelpers.NotificationLevel.Success);
            var envReport = _getLastReport();
            if (envReport is not null)
            {
                QueueInventoryRescan(envReport, successStatus: "removed — inventory now {0} skill(s)");
            }
        }
    }

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
        var report = _getLastReport();
        if (report is not null)
        {
            return report;
        }

        report = await _services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        _rememberReport(report);
        return report;
    }

    private Task<InventorySnapshot> CaptureInventoryAsync(EnvironmentReport report, CancellationToken cancellationToken) =>
        _services.InventoryService.CaptureAsync(
            report.GhPath,
            report.Capabilities,
            new LocalInventoryService.Options(
                ScanRoots: _options.ScanRoots,
                AllowHiddenDirs: false),
            cancellationToken);

}
