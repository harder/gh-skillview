using SkillView.Inventory.Models;

namespace SkillView.Ui;

/// One bounded, app-owned display capture shared by tabs in a fixed app context.
/// Mutation/review capture paths bypass this cache.
internal sealed class DisplayInventoryCache(
    Func<long> generation,
    Func<CancellationToken, Task<InventorySnapshot>> capture,
    Func<Func<CancellationToken, Task>, string, bool> runBackground,
    Func<DateTimeOffset>? clock = null)
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private InventorySnapshot? _snapshot;
    private long _snapshotGeneration;
    private DateTimeOffset _completedAt;
    private Flight? _flight;

    internal async Task<InventorySnapshot> GetAsync(CancellationToken token, Func<CancellationToken, Task<InventorySnapshot>>? captureOverride = null)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Flight flight;
            bool start;
            lock (_gate)
            {
                var current = generation();
                if (_snapshot is not null && _snapshotGeneration == current
                    && _now() - _completedAt < TimeSpan.FromMinutes(1)) return _snapshot;
                // Detach obsolete work so a refresh starts immediately. The app
                // still owns and drains the old worker and its existing waiters.
                if (_flight is not null && _flight.Generation != current) _flight = null;
                start = _flight is null;
                flight = _flight ??= new Flight(current);
            }
            // Publish ownership before an inline scheduler can start the worker.
            if (start)
            {
                try
                {
                    if (!runBackground(ct => LoadAsync(flight, ct, captureOverride ?? capture), "inventory.display-capture"))
                        throw new OperationCanceledException("Inventory capture was refused during shutdown.");
                }
                catch (Exception ex)
                {
                    lock (_gate) ClearFlightUnderLock(flight);
                    flight.Completion.TrySetException(ex);
                }
            }
            InventorySnapshot result;
            try { result = await flight.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
            catch when (!token.IsCancellationRequested && flight.Generation != generation()) { continue; }
            if (flight.Generation == generation()) return result;
        }
    }

    internal void PublishFresh(InventorySnapshot snapshot, long capturedGeneration)
    {
        lock (_gate) _ = StoreUnderLock(snapshot, capturedGeneration);
    }

    private InventorySnapshot StoreUnderLock(InventorySnapshot snapshot, long capturedGeneration)
    {
        if (capturedGeneration != generation()) return snapshot;
        if (_snapshot is not null && _snapshotGeneration == capturedGeneration
            && _snapshot.CapturedAt > snapshot.CapturedAt) return _snapshot;
        _snapshot = snapshot;
        _snapshotGeneration = capturedGeneration;
        _completedAt = _now();
        return snapshot;
    }

    private async Task LoadAsync(Flight flight, CancellationToken token, Func<CancellationToken, Task<InventorySnapshot>> loader)
    {
        try
        {
            var result = flight.Generation == generation()
                ? await loader(token).ConfigureAwait(false) : InventorySnapshot.Empty;
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                result = StoreUnderLock(result, flight.Generation);
                ClearFlightUnderLock(flight);
            }
            flight.Completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            lock (_gate) ClearFlightUnderLock(flight);
            flight.Completion.TrySetException(ex);
            // All display waiters may have left; the app still owns this worker.
            _ = flight.Completion.Task.Exception;
        }
    }

    private void ClearFlightUnderLock(Flight flight)
    {
        if (ReferenceEquals(_flight, flight)) _flight = null;
    }

    private sealed class Flight(long generation)
    {
        internal long Generation { get; } = generation;
        internal TaskCompletionSource<InventorySnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
