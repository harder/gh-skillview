namespace SkillView.Threading;

/// <summary>
/// Owns a cancellation source whose callbacks cannot escape into lifecycle,
/// replacement, or timer threads. Disposal is deferred while cancellation is
/// running so a callback may synchronously dispose its own owner safely.
/// </summary>
internal sealed class CancellationSource : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration[] _parentRegistrations;
    private readonly Timer? _deadlineTimer;
    private readonly Action<AggregateException>? _onCallbackException;
    private int _activeCancellations;
    private bool _disposeRequested;
    private bool _resourcesDisposed;

    internal CancellationSource(Action<AggregateException>? onCallbackException = null)
        : this([], timeout: null, onCallbackException)
    {
    }

    internal CancellationSource(
        TimeSpan timeout,
        Action<AggregateException>? onCallbackException = null)
        : this([], timeout, onCallbackException)
    {
    }

    internal CancellationSource(
        CancellationToken parent,
        Action<AggregateException>? onCallbackException = null)
        : this([parent], timeout: null, onCallbackException)
    {
    }

    internal CancellationSource(
        CancellationToken firstParent,
        CancellationToken secondParent,
        Action<AggregateException>? onCallbackException = null)
        : this([firstParent, secondParent], timeout: null, onCallbackException)
    {
    }

    internal CancellationSource(
        CancellationToken parent,
        TimeSpan timeout,
        Action<AggregateException>? onCallbackException = null)
        : this([parent], timeout, onCallbackException)
    {
    }

    private CancellationSource(
        IReadOnlyList<CancellationToken> parents,
        TimeSpan? timeout,
        Action<AggregateException>? onCallbackException)
    {
        if (timeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _onCallbackException = onCallbackException;
        Token = _source.Token;

        var registrations = parents.Count == 0
            ? Array.Empty<CancellationTokenRegistration>()
            : new CancellationTokenRegistration[parents.Count];
        var registrationCount = 0;
        foreach (var parent in parents)
        {
            if (parent.CanBeCanceled)
            {
                // UnsafeRegister invokes synchronously when a parent is already
                // canceled. That can enter Cancel before _parentRegistrations is
                // assigned, but this instance cannot be disposed or observed by
                // external code until construction returns.
                registrations[registrationCount++] = parent.UnsafeRegister(
                    static state => ((CancellationSource)state!).Cancel(),
                    this);
            }
        }
        _parentRegistrations = registrationCount switch
        {
            0 => [],
            _ when registrationCount == registrations.Length => registrations,
            _ => registrations[..registrationCount],
        };

        if (timeout is { } dueTime)
        {
            _deadlineTimer = CreateDeadlineTimer(dueTime);
        }
    }

    internal CancellationToken Token { get; }

    internal bool IsCancellationRequested => Token.IsCancellationRequested;

    internal bool TryGetActiveToken(out CancellationToken cancellationToken)
    {
        cancellationToken = Token;
        lock (_gate)
        {
            return !_disposeRequested && !cancellationToken.IsCancellationRequested;
        }
    }

    /// <summary>
    /// Requests cancellation unless this owner was already disposed. Disposal
    /// closes cancellation admission, so canceling an uncanceled disposed owner
    /// is intentionally a no-op and leaves its stable token uncanceled.
    /// </summary>
    internal void Cancel()
    {
        lock (_gate)
        {
            if (_resourcesDisposed)
            {
                return;
            }
            _activeCancellations++;
        }

        AggregateException? callbackException = null;
        try
        {
            _source.Cancel();
        }
        catch (AggregateException ex)
        {
            callbackException = ex.Flatten();
        }
        finally
        {
            var dispose = false;
            lock (_gate)
            {
                _activeCancellations--;
                if (_disposeRequested && _activeCancellations == 0 && !_resourcesDisposed)
                {
                    _resourcesDisposed = true;
                    dispose = true;
                }
            }

            Report(callbackException);
            if (dispose)
            {
                DisposeResources();
            }
        }
    }

    public void Dispose()
    {
        var dispose = false;
        lock (_gate)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            if (_activeCancellations == 0 && !_resourcesDisposed)
            {
                _resourcesDisposed = true;
                dispose = true;
            }
        }

        if (dispose)
        {
            DisposeResources();
        }
    }

    private void Report(AggregateException? exception)
    {
        if (exception is null || _onCallbackException is null)
        {
            return;
        }

        try
        {
            _onCallbackException(exception);
        }
        catch
        {
            // A diagnostic callback must not recreate the teardown failure
            // this owner exists to contain.
        }
    }

    private void DisposeResources()
    {
        _deadlineTimer?.Dispose();
        foreach (var registration in _parentRegistrations)
        {
            registration.Dispose();
        }
        _source.Dispose();
    }

    private Timer CreateDeadlineTimer(TimeSpan dueTime)
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            return CreateTimer();
        }

        using (ExecutionContext.SuppressFlow())
        {
            return CreateTimer();
        }

        Timer CreateTimer() => new(
            static state => ((CancellationSource)state!).Cancel(),
            this,
            dueTime,
            Timeout.InfiniteTimeSpan);
    }
}
