using SkillView.Threading;

namespace SkillView.Ui;

/// Coordinates one owned cancellation source across replacement, cancellation,
/// and lease disposal. Ownership transitions run under one gate, while
/// cancellation callbacks run outside it; disposal is deferred until an
/// in-progress cancellation finishes.
internal sealed class CancellationOperationSlot
{
    private readonly object _gate = new();
    private readonly Action<AggregateException>? _onCallbackException;
    private SourceState? _active;

    internal CancellationOperationSlot(Action<AggregateException>? onCallbackException = null)
    {
        _onCallbackException = onCallbackException;
    }

    internal bool HasActive
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

    internal Lease Replace(CancellationToken lifetime)
    {
        var next = new SourceState(new CancellationSource(lifetime, _onCallbackException));
        SourceState? previous;
        lock (_gate)
        {
            previous = _active;
            _active = next;
            PrepareCancellationLocked(previous);
        }
        CancelPrepared(previous);

        return new Lease(this, next);
    }

    internal Lease? TryBegin(CancellationToken lifetime)
    {
        var next = new SourceState(new CancellationSource(lifetime, _onCallbackException));
        lock (_gate)
        {
            if (_active is not null)
            {
                next.Source.Dispose();
                return null;
            }

            _active = next;
        }

        return new Lease(this, next);
    }

    internal bool Cancel()
    {
        SourceState? active;
        lock (_gate)
        {
            if (_active is null)
            {
                return false;
            }

            active = _active;
            _active = null;
            PrepareCancellationLocked(active);
        }
        CancelPrepared(active);
        return true;
    }

    private void Release(SourceState state)
    {
        var dispose = false;
        lock (_gate)
        {
            if (ReferenceEquals(_active, state))
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
            state.Source.TryCancel();
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
        private CancellationOperationSlot? _owner;
        private readonly SourceState _state;

        internal Lease(CancellationOperationSlot owner, SourceState state)
        {
            _owner = owner;
            _state = state;
            Token = state.Source.Token;
        }

        internal CancellationToken Token { get; }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(_state);
    }

    internal sealed class SourceState(CancellationSource source)
    {
        internal CancellationSource Source { get; } = source;
        internal bool CancellationInProgress { get; set; }
        internal bool LeaseReleased { get; set; }
        internal bool Disposed { get; set; }
    }
}
