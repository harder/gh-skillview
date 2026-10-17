namespace SkillView.Ui;

/// Latest pending selection wins. A started dispatch stays exclusively owned
/// until its awaited UI completion returns, including the synchronous modal.
internal sealed class ChangesSelectionGate(Action<AggregateException>? report) : IDisposable
{
    private readonly LatestRequestGate _pending = new(report);
    private int _dispatchActive;

    internal Lease? Begin(CancellationToken lifetime)
    {
        if (Volatile.Read(ref _dispatchActive) != 0) return null;
        return new(this, _pending.Begin(lifetime, TimeSpan.FromMinutes(2)));
    }

    internal void Cancel() => _pending.Cancel();
    public void Dispose() => _pending.Dispose();

    internal sealed class Lease(ChangesSelectionGate owner, LatestRequestGate.Lease request) : IDisposable
    {
        private int _enteredDispatch;
        internal CancellationToken Token => request.Token;
        internal bool IsCurrent => request.IsCurrent && !Token.IsCancellationRequested;

        internal bool TryEnterDispatch()
        {
            if (!IsCurrent || Interlocked.CompareExchange(ref owner._dispatchActive, 1, 0) != 0) return false;
            if (!IsCurrent)
            {
                Volatile.Write(ref owner._dispatchActive, 0);
                return false;
            }
            Volatile.Write(ref _enteredDispatch, 1);
            return true;
        }

        public void Dispose()
        {
            request.Dispose();
            if (Interlocked.Exchange(ref _enteredDispatch, 0) != 0)
                Volatile.Write(ref owner._dispatchActive, 0);
        }
    }
}
