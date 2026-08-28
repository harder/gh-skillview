namespace SkillView.Ui;

/// Owns one cancellable request at a time. Beginning a newer request cancels
/// the previous one outside the ownership lock, and each lease can cheaply
/// tell whether its result is still current before touching UI state.
internal sealed class LatestRequestGate : IDisposable
{
    private readonly object _gate = new();
    private SourceState? _active;
    private long _generation;

    internal Lease Begin(CancellationToken lifetime, TimeSpan timeout)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        cancellation.CancelAfter(timeout);
        var next = new SourceState(cancellation);

        long generation;
        SourceState? previous;
        lock (_gate)
        {
            generation = ++_generation;
            previous = _active;
            _active = next;
            PrepareCancellationLocked(previous);
        }
        CancelPrepared(previous);
        return new Lease(this, generation, next);
    }

    internal bool Cancel()
    {
        SourceState? active;
        lock (_gate)
        {
            _generation++;
            active = _active;
            _active = null;
            PrepareCancellationLocked(active);
        }
        CancelPrepared(active);
        return active is not null;
    }

    public void Dispose() => _ = Cancel();

    private bool IsCurrent(long generation, SourceState state)
    {
        lock (_gate)
        {
            return generation == _generation && ReferenceEquals(_active, state);
        }
    }

    private void Release(long generation, SourceState state)
    {
        var dispose = false;
        lock (_gate)
        {
            if (generation == _generation && ReferenceEquals(_active, state))
            {
                _active = null;
            }

            state.LeaseReleased = true;
            if (!state.CancellationInProgress && !state.Disposed)
            {
                state.Disposed = true;
                dispose = true;
            }
        }
        if (dispose)
        {
            state.Source.Dispose();
        }
    }

    private static void PrepareCancellationLocked(SourceState? state)
    {
        if (state is not null)
        {
            state.CancellationInProgress = true;
        }
    }

    private void CancelPrepared(SourceState? state)
    {
        if (state is null) return;
        try
        {
            state.Source.Cancel();
        }
        finally
        {
            var dispose = false;
            lock (_gate)
            {
                state.CancellationInProgress = false;
                if (state.LeaseReleased && !state.Disposed)
                {
                    state.Disposed = true;
                    dispose = true;
                }
            }
            if (dispose)
            {
                state.Source.Dispose();
            }
        }
    }

    internal sealed class Lease : IDisposable
    {
        private LatestRequestGate? _owner;
        private readonly long _generation;
        private readonly SourceState _state;

        internal Lease(LatestRequestGate owner, long generation, SourceState state)
        {
            _owner = owner;
            _generation = generation;
            _state = state;
            Token = state.Source.Token;
        }

        internal CancellationToken Token { get; }

        internal bool IsCurrent => _owner?.IsCurrent(_generation, _state) == true;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(_generation, _state);
    }

    internal sealed class SourceState(CancellationTokenSource source)
    {
        internal CancellationTokenSource Source { get; } = source;
        internal bool CancellationInProgress { get; set; }
        internal bool LeaseReleased { get; set; }
        internal bool Disposed { get; set; }
    }
}
