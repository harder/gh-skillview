using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Threading;

namespace SkillView.Gh;

internal sealed class GhSkillListCache
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _ttl;
    private readonly Action<AggregateException>? _onCallbackException;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InflightLoad> _inflight = new(StringComparer.Ordinal);
    private long _generation;

    internal GhSkillListCache(
        Func<DateTimeOffset>? now = null,
        TimeSpan? ttl = null,
        Action<AggregateException>? onCallbackException = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _ttl = ttl ?? TimeSpan.FromSeconds(15);
        _onCallbackException = onCallbackException;
    }

    internal bool TryGet(
        string ghPath,
        string? scope,
        string? agent,
        out ImmutableArray<GhSkillListRecord> records)
    {
        var key = BuildKey(ghPath, scope, agent);
        var now = _now();
        lock (_gate)
        {
            return TryGetLocked(key, now, out records);
        }
    }

    internal async Task<LookupResult> GetOrLoadAsync(
        string ghPath,
        string? scope,
        string? agent,
        Func<CancellationToken, Task<LoadResult>> loader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(ghPath, scope, agent);
        var now = _now();
        InflightLoad flight;
        var startsLoad = false;

        lock (_gate)
        {
            if (TryGetLocked(key, now, out var cached))
            {
                return new LookupResult(cached, FromCache: true);
            }

            if (!_inflight.TryGetValue(key, out flight!))
            {
                flight = new InflightLoad(_generation, _onCallbackException);
                _inflight.Add(key, flight);
                startsLoad = true;
            }
            flight.WaiterCount++;
        }

        if (startsLoad)
        {
            flight.Execution = CompleteLoadAsync(key, flight, loader);
        }

        try
        {
            var loaded = await flight.Completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new LookupResult(loaded.Records, FromCache: false);
        }
        finally
        {
            var finalLoader = ReleaseWaiter(key, flight);
            if (finalLoader is not null)
            {
                await finalLoader.ConfigureAwait(false);
            }
        }
    }

    private bool TryGetLocked(
        string key,
        DateTimeOffset now,
        out ImmutableArray<GhSkillListRecord> records)
    {
        if (_entries.TryGetValue(key, out var entry) && now - entry.CapturedAt <= _ttl)
        {
            records = entry.Records;
            return true;
        }

        _entries.Remove(key);
        records = ImmutableArray<GhSkillListRecord>.Empty;
        return false;
    }

    internal void Store(
        string ghPath,
        string? scope,
        string? agent,
        ImmutableArray<GhSkillListRecord> records)
    {
        var capturedAt = _now();
        lock (_gate)
        {
            _entries[BuildKey(ghPath, scope, agent)] = new CacheEntry(capturedAt, records);
        }
    }

    internal void Invalidate()
    {
        InflightLoad[] invalidated;
        lock (_gate)
        {
            _generation++;
            _entries.Clear();
            invalidated = _inflight.Values.ToArray();
            _inflight.Clear();
            foreach (var flight in invalidated)
            {
                // Do not complete waiters here. The loader may still be
                // killing and draining a child process. CompleteLoadAsync
                // publishes cancellation only after that cleanup returns.
                flight.Invalidated = true;
            }
        }

        foreach (var flight in invalidated)
        {
            flight.Cancellation.TryCancel();
        }
    }

    private async Task CompleteLoadAsync(
        string key,
        InflightLoad flight,
        Func<CancellationToken, Task<LoadResult>> loader)
    {
        LoadResult loaded = default;
        Exception? failure = null;
        CancellationToken cancellationToken = default;
        var canceled = false;
        try
        {
            loaded = await loader(flight.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            canceled = true;
            cancellationToken = ex.CancellationToken;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        bool invalidated;
        bool dispose;
        CancellationToken sharedCancellationToken;
        var capturedAt = default(DateTimeOffset);
        if (!canceled && failure is null && loaded.ShouldCache)
        {
            try { capturedAt = _now(); }
            catch (Exception ex) { failure = ex; }
        }
        lock (_gate)
        {
            invalidated = flight.Invalidated;
            if (!invalidated
                && !canceled
                && failure is null
                && _inflight.TryGetValue(key, out var current)
                && ReferenceEquals(current, flight)
                && flight.Generation == _generation)
            {
                if (loaded.ShouldCache)
                {
                    _entries[key] = new CacheEntry(capturedAt, loaded.Records);
                }
            }

            RemoveCurrentFlightLocked(key, flight);
            flight.LoaderFinished = true;
            dispose = flight.WaiterCount == 0;
            sharedCancellationToken = flight.Cancellation.Token;
        }

        if (dispose)
        {
            flight.Cancellation.Dispose();
        }

        // Publish the outcome only after the loader and shared-flight cleanup
        // above have finished. Invalidation therefore cannot release an
        // application-owned waiter while its gh process is still draining.
        if (invalidated)
        {
            flight.Completion.TrySetCanceled(sharedCancellationToken);
        }
        else if (canceled)
        {
            flight.Completion.TrySetCanceled(cancellationToken);
        }
        else if (failure is not null)
        {
            flight.Completion.TrySetException(failure);
        }
        else
        {
            flight.Completion.TrySetResult(loaded);
        }
    }

    private Task? ReleaseWaiter(string key, InflightLoad flight)
    {
        var cancel = false;
        var dispose = false;
        Task? finalLoader = null;
        lock (_gate)
        {
            flight.WaiterCount--;
            if (flight.WaiterCount == 0)
            {
                dispose = flight.LoaderFinished;
                if (!flight.Completion.Task.IsCompleted)
                {
                    RemoveCurrentFlightLocked(key, flight);
                    flight.Completion.TrySetCanceled(flight.Cancellation.Token);
                    cancel = true;
                    finalLoader = flight.Execution;
                }
            }
        }

        if (cancel)
        {
            flight.Cancellation.TryCancel();
        }
        if (dispose)
        {
            flight.Cancellation.Dispose();
        }
        return finalLoader;
    }

    private void RemoveCurrentFlightLocked(string key, InflightLoad flight)
    {
        if (_inflight.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
        {
            _inflight.Remove(key);
        }
    }

    private static string BuildKey(string ghPath, string? scope, string? agent) =>
        $"{ghPath}\n{scope ?? string.Empty}\n{agent ?? string.Empty}";

    private sealed record CacheEntry(
        DateTimeOffset CapturedAt,
        ImmutableArray<GhSkillListRecord> Records);

    internal readonly record struct LoadResult(
        ImmutableArray<GhSkillListRecord> Records,
        bool ShouldCache = true);

    internal readonly record struct LookupResult(
        ImmutableArray<GhSkillListRecord> Records,
        bool FromCache);

    private sealed class InflightLoad(
        long generation,
        Action<AggregateException>? onCallbackException)
    {
        internal CancellationSource Cancellation { get; } = new(onCallbackException);
        internal TaskCompletionSource<LoadResult> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal long Generation { get; } = generation;
        internal int WaiterCount { get; set; }
        internal bool LoaderFinished { get; set; }
        internal bool Invalidated { get; set; }
        internal Task? Execution { get; set; }
    }
}
