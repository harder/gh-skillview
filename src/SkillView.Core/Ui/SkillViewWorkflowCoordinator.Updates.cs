using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Threading;

namespace SkillView.Ui;

internal sealed partial class SkillViewWorkflowCoordinator
{
    internal UpdateAvailabilitySession Availability { get; } = new();
    private CancellationOperationSlot? _availabilityCancellation;
    private long _availabilityGeneration;
    private bool _checkingAvailability;

    internal void CancelAvailability(Action changed)
    {
        _availabilityCancellation?.Cancel();
        Availability.Cancel();
        Availability.SetStopping(_checkingAvailability);
        changed();
    }

    // May be called by an invalidating mutation on a worker. Cancellation is
    // immediate; queued presentation still checks the captured generation.
    internal void InvalidateAvailability(Action changed)
    {
        var generation = Interlocked.Increment(ref _availabilityGeneration);
        _availabilityCancellation?.Cancel();
        _invoke(() =>
        {
            if (generation != Interlocked.Read(ref _availabilityGeneration)) return;
            Availability.Invalidate();
            Availability.SetStopping(_checkingAvailability);
            changed();
        });
    }

    internal async Task CheckUpdatesAsync(Action changed, CancellationToken lifetime)
    {
        if (_checkingAvailability) { if (!Availability.Stopping) CancelAvailability(changed); return; }
        _availabilityCancellation ??= new(CancellationCallbackReporter.For(_services.Logger, "update availability"));
        using var operation = _availabilityCancellation.TryBegin(lifetime);
        if (operation is null) return;
        _checkingAvailability = true;
        Availability.Start();
        changed();
        var token = operation.Token;
        var generation = Interlocked.Increment(ref _availabilityGeneration);
        var catalogGeneration = _services.ListAdapter.Generation;
        var request = 0L;
        string? contextKey = null;
        string? checkedGhPath = null;
        string? checkedGhVersion = null;
        bool Current() => !token.IsCancellationRequested && generation == Interlocked.Read(ref _availabilityGeneration)
            && catalogGeneration == _services.ListAdapter.Generation
            && (contextKey is null || contextKey == UpdateAvailability.Context(_services.Runner, checkedGhPath, checkedGhVersion));
        try
        {
            var report = await GetOrProbeReportAsync(token).ConfigureAwait(false);
            var snapshot = await CaptureInventorySnapshotAsync(token).ConfigureAwait(false);
            var context = _services.Runner.WithContext(_services.Runner.EffectiveWorkingDirectory,
                new Dictionary<string, string>());
            checkedGhPath = report.GhPath;
            checkedGhVersion = report.GhVersionRaw;
            contextKey = UpdateAvailability.Context(context, checkedGhPath, checkedGhVersion);
            await _invokeAsync(() =>
            {
                if (!Current()) return;
                request = Availability.Begin(snapshot, catalogGeneration, contextKey);
                changed();
            }, token).ConfigureAwait(false);
            if (!Current() || request == 0) return;
            await CheckCopiesAsync(snapshot.Skills, async (skill, ct) =>
                {
                    var observation = !report.BaselineOk
                        ? new UpdateAvailability(UpdateAvailabilityStatus.Unsupported,
                            "A supported gh (2.97.0 or newer) with gh skill is required. Open Doctor for setup.", DateTimeOffset.Now)
                        : snapshot.IsPartial || !snapshot.ConfiguredRootsComplete
                            ? new UpdateAvailability(UpdateAvailabilityStatus.Unknown,
                                "Inventory coverage is incomplete. Refresh and resolve discovery failures before checking upstream.", DateTimeOffset.Now)
                            : await _services.UpdateAvailability.CheckAvailabilityAsync(report.GhPath!, skill, context, ct).ConfigureAwait(false);
                    await _invokeAsync(() =>
                    {
                        if (!Current()) return;
                        if (Availability.Publish(request, catalogGeneration, contextKey, skill, observation)) changed();
                    }, token).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _services.Logger.Error("updates.check", ex.ToString());
            await _invokeAsync(() =>
            {
                if (!Current()) return;
                Availability.Fail(TuiHelpers.ErrorSnippet(ex.Message));
                changed();
            }, lifetime).ConfigureAwait(false);
        }
        finally
        {
            // Await publication while the operation lease is still alive.
            await _invokeAsync(() =>
            {
                _checkingAvailability = false;
                Availability.SetStopping(false);
                if (Current() && request != 0) Availability.Complete(request);
                else if (!lifetime.IsCancellationRequested && !token.IsCancellationRequested) Availability.Invalidate();
                changed();
            }, lifetime).ConfigureAwait(false);
        }
    }

    // Availability has no 128-target mutation limit. Enumerate the entire
    // frozen snapshot, with only four children/metadata reads admitted at once.
    internal static Task CheckCopiesAsync(IEnumerable<InstalledSkill> skills,
        Func<InstalledSkill, CancellationToken, ValueTask> check, CancellationToken token) =>
        Parallel.ForEachAsync(skills.DistinctBy(skill => skill.InstallationId),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, check);
}
