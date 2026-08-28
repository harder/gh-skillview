namespace SkillView.Ui;

/// Coordinates one owned cancellation source across replacement, cancellation,
/// and lease disposal. Every operation that can touch the source itself runs
/// under the same gate, preventing Cancel from racing Dispose.
internal sealed class CancellationTokenSourceSlot
{
    private readonly object _gate = new();
    private CancellationTokenSource? _active;

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
        var next = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        lock (_gate)
        {
            _active?.Cancel();
            _active = next;
        }

        return new Lease(this, next);
    }

    internal Lease? TryBegin(CancellationToken lifetime)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        lock (_gate)
        {
            if (_active is not null)
            {
                next.Dispose();
                return null;
            }

            _active = next;
        }

        return new Lease(this, next);
    }

    internal bool Cancel()
    {
        lock (_gate)
        {
            if (_active is null)
            {
                return false;
            }

            _active.Cancel();
            _active = null;
            return true;
        }
    }

    private void Release(CancellationTokenSource source)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, source))
            {
                _active = null;
            }

            source.Dispose();
        }
    }

    internal sealed class Lease : IDisposable
    {
        private CancellationTokenSourceSlot? _owner;
        private readonly CancellationTokenSource _source;

        internal Lease(CancellationTokenSourceSlot owner, CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
            Token = source.Token;
        }

        internal CancellationToken Token { get; }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(_source);
    }
}
