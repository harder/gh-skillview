using SkillView.Inventory.Models;
using SkillView.Threading;

namespace SkillView.Ui;

/// One bounded, app-owned display capture shared by tabs in a fixed app context.
/// Mutation/review capture paths bypass this cache.
internal sealed class DisplayInventoryCache(
    Func<long> generation,
    Func<CancellationToken, Task<InventorySnapshot>> capture,
    Func<Func<CancellationToken, Task>, string, bool> runBackground,
    Func<DateTimeOffset>? clock = null,
    Action<AggregateException>? onCancellationCallbackException = null)
{
    internal static readonly TimeSpan ReuseWindow = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private InventorySnapshot? _snapshot;
    private long _snapshotGeneration;
    private DateTimeOffset _completedAt;
    private Flight? _flight;
    private DiscoverInstalledObservation? _observation;
    internal event Action? ObservationChanged;
    internal DiscoverInstalledObservation? Observation
    {
        get { lock (_gate) return _snapshotGeneration == generation() ? _observation : null; }
    }

    internal bool CacheIsCurrent(InventorySnapshot snapshot, long capturedGeneration)
    {
        lock (_gate)
            return ReferenceEquals(_snapshot, snapshot) && _snapshotGeneration == capturedGeneration
                && generation() == capturedGeneration;
    }

    internal async Task<InventorySnapshot> GetAsync(CancellationToken token, Func<CancellationToken, Task<InventorySnapshot>>? captureOverride = null)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Flight? flight = null;
            Flight? obsolete;
            InventorySnapshot? cached = null;
            var start = false;
            lock (_gate)
            {
                var current = generation();
                obsolete = DetachObsoleteFlightUnderLock(current);
                // Keep the latest observation for ordering, but let a later
                // visit retry incomplete coverage immediately.
                if (_snapshot is not null && _snapshotGeneration == current
                    && !_snapshot.IsPartial && _snapshot.ConfiguredRootsComplete
                    && _now() - _completedAt < ReuseWindow) cached = _snapshot;
                else
                {
                    start = _flight is null;
                    flight = _flight ??= new Flight(current, onCancellationCallbackException);
                }
            }
            // Publish and schedule the replacement before cancellation callbacks
            // can re-enter. Cancellation never runs under the ownership gate.
            if (start) StartFlight(flight!, captureOverride ?? capture);
            obsolete?.Cancel();
            if (cached is not null) return cached;
            var active = flight!;
            InventorySnapshot result;
            try { result = await active.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
            catch when (!token.IsCancellationRequested && active.Generation != generation()) { continue; }
            if (active.Generation == generation()) return result;
        }
    }

    private void StartFlight(Flight flight, Func<CancellationToken, Task<InventorySnapshot>> loader)
    {
        try
        {
            if (!runBackground(ct => LoadAsync(flight, ct, loader), "inventory.display-capture"))
                throw new OperationCanceledException("Inventory capture was refused during shutdown.");
        }
        catch (Exception ex)
        {
            lock (_gate) ClearFlightUnderLock(flight);
            flight.Completion.TrySetException(ex);
            _ = flight.Completion.Task.Exception;
            flight.Dispose();
        }
    }

    internal void PublishFresh(InventorySnapshot snapshot, long capturedGeneration)
    {
        Flight? obsolete;
        lock (_gate)
        {
            _ = StoreUnderLock(snapshot, capturedGeneration);
            obsolete = DetachObsoleteFlightUnderLock(generation());
        }
        obsolete?.Cancel();
        ObservationChanged?.Invoke();
    }

    private Flight? DetachObsoleteFlightUnderLock(long current)
    {
        if (_flight is null || _flight.Generation == current) return null;
        var obsolete = _flight;
        _flight = null;
        return obsolete;
    }

    private InventorySnapshot StoreUnderLock(InventorySnapshot snapshot, long capturedGeneration)
    {
        if (capturedGeneration != generation()) return snapshot;
        if (_snapshot is not null && _snapshotGeneration == capturedGeneration
            && _snapshot.CapturedAt > snapshot.CapturedAt) return _snapshot;
        _snapshot = snapshot;
        _observation = new(snapshot);
        _snapshotGeneration = capturedGeneration;
        _completedAt = _now();
        return snapshot;
    }

    private async Task LoadAsync(Flight flight, CancellationToken token, Func<CancellationToken, Task<InventorySnapshot>> loader)
    {
        try
        {
            using var lifetime = new CancellationSource(token, flight.Token, onCancellationCallbackException);
            var captureToken = lifetime.Token;
            captureToken.ThrowIfCancellationRequested();
            var result = flight.Generation == generation()
                ? await loader(captureToken).ConfigureAwait(false) : InventorySnapshot.Empty;
            captureToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                result = StoreUnderLock(result, flight.Generation);
                ClearFlightUnderLock(flight);
            }
            ObservationChanged?.Invoke();
            flight.Completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            lock (_gate) ClearFlightUnderLock(flight);
            flight.Completion.TrySetException(ex);
            // All display waiters may have left; the app still owns this worker.
            _ = flight.Completion.Task.Exception;
        }
        finally { flight.Dispose(); }
    }

    private void ClearFlightUnderLock(Flight flight)
    {
        if (ReferenceEquals(_flight, flight)) _flight = null;
    }

    private sealed class Flight : IDisposable
    {
        private readonly CancellationOperationSlot _cancellation;
        private readonly CancellationOperationSlot.Lease _lease;

        internal Flight(long generation, Action<AggregateException>? report)
        {
            Generation = generation;
            _cancellation = new(report);
            _lease = _cancellation.Replace(CancellationToken.None);
        }

        internal long Generation { get; }
        internal CancellationToken Token => _lease.Token;
        internal TaskCompletionSource<InventorySnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Cancel() => _cancellation.Cancel();
        public void Dispose() => _lease.Dispose();
    }
}
