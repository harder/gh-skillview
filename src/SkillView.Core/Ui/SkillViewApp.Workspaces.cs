using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Inventory.Models;
using SkillView.Threading;
using Terminal.Gui.ViewBase;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{

    internal static bool ShouldAutoOpenInstalledOnStartup(
        InventorySnapshot snapshot,
        bool startupInstalledShown,
        bool userInteractedSinceLaunch) =>
        !startupInstalledShown
        && !userInteractedSinceLaunch
        && ShouldOpenInstalledOnStartup(snapshot);

    private async Task ProbeDoctorAsync(long doctorGeneration, CancellationToken cancellationToken)
    {
        try
        {
            var probed = await GetOrProbeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            Invoke(() =>
            {
                if (!IsDoctorWorkspaceActive(doctorGeneration)) return;
                ClearBusy();
                _doctorTab?.SetReport(probed, _installedTab?.CurrentSnapshot);
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
        _discoverLifetime = new CancellationSource(
            GetRunLifetimeToken(),
            CancellationCallbackReporter.For(_services.Logger, "Discover"));
        Interlocked.Increment(ref _discoverGeneration);
    }

    private void DeactivateDiscoverWorkspace(bool clearBusy)
    {
        _discoverTab?.SetLoading(false);
        Interlocked.Increment(ref _discoverGeneration);
        _searchRequests.Cancel();
        CancelCurrentPreview();
        var lifetime = Interlocked.Exchange(ref _discoverLifetime, null);
        if (lifetime is not null)
        {
            lifetime.TryCancel();
            lifetime.Dispose();
        }
        if (clearBusy)
        {
            ClearAllBusyOperations();
        }
    }

    internal static bool TryCaptureActiveLifetimeToken(
        CancellationSource? lifetime,
        out CancellationToken cancellationToken)
    {
        if (lifetime is null)
        {
            cancellationToken = new CancellationToken(canceled: true);
            return false;
        }

        if (lifetime.TryGetActiveToken(out cancellationToken))
        {
            return true;
        }

        cancellationToken = new CancellationToken(canceled: true);
        return false;
    }

    private void ActivateDoctorWorkspace()
    {
        DeactivateDoctorWorkspace(clearBusy: false);
        _doctorLifetime = new CancellationSource(
            GetRunLifetimeToken(),
            CancellationCallbackReporter.For(_services.Logger, "Doctor"));
        Interlocked.Increment(ref _doctorGeneration);
    }

    private void DeactivateDoctorWorkspace(bool clearBusy)
    {
        Interlocked.Increment(ref _doctorGeneration);
        var lifetime = Interlocked.Exchange(ref _doctorLifetime, null);
        if (lifetime is not null)
        {
            lifetime.TryCancel();
            lifetime.Dispose();
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

    private void ProbeGhAsync()
    {
        RunBackground(async cancellationToken =>
        {
            var report = await GetOrProbeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            _ghPath = report.GhPath;

            var snapshot = await CaptureStartupInventoryAsync(report, cancellationToken).ConfigureAwait(false);

            var overview = InstalledOverview.Build(snapshot, cancellationToken);

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
                        _installedTab.LoadSeeded(snapshot, overview);
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

    internal Task<InventorySnapshot> CaptureStartupInventoryAsync(EnvironmentReport report, CancellationToken cancellationToken) =>
        _workflows.CaptureDisplayInventoryAsync(report, cancellationToken);

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
}
