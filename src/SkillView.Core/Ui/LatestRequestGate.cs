namespace SkillView.Ui;

/// Owns one cancellable request at a time. Beginning a newer request cancels
/// the previous one, and each lease can cheaply tell whether its result is
/// still current before touching UI state.
internal sealed class LatestRequestGate : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private long _generation;

    internal Lease Begin(CancellationToken lifetime, TimeSpan timeout)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        cancellation.CancelAfter(timeout);

        CancellationTokenSource? previous;
        long generation;
        lock (_gate)
        {
            generation = ++_generation;
            previous = _active;
            _active = cancellation;
        }
        previous?.Cancel();
        return new Lease(this, generation, cancellation);
    }

    internal bool Cancel()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            _generation++;
            active = _active;
            _active = null;
        }
        active?.Cancel();
        return active is not null;
    }

    public void Dispose() => _ = Cancel();

    private bool IsCurrent(long generation, CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            return generation == _generation && ReferenceEquals(_active, cancellation);
        }
    }

    private void Release(long generation, CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (generation == _generation && ReferenceEquals(_active, cancellation))
            {
                _active = null;
            }
        }
        cancellation.Dispose();
    }

    internal sealed class Lease : IDisposable
    {
        private LatestRequestGate? _owner;
        private readonly long _generation;
        private readonly CancellationTokenSource _cancellation;

        internal Lease(LatestRequestGate owner, long generation, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _generation = generation;
            _cancellation = cancellation;
        }

        internal CancellationToken Token => _cancellation.Token;

        internal bool IsCurrent => _owner?.IsCurrent(_generation, _cancellation) == true;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(_generation, _cancellation);
    }
}
