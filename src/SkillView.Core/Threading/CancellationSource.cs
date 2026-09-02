namespace SkillView.Threading;

/// <summary>
/// Owns a cancellation source whose callbacks cannot escape into lifecycle,
/// replacement, or timer threads. Disposal is deferred while cancellation is
/// running so a callback may synchronously dispose its own owner safely.
/// </summary>
internal sealed class CancellationSource : IDisposable
{
    private static readonly TimeSpan MaxSupportedTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1L);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _source;
    private readonly CancellationTokenRegistration[] _parentRegistrations;
    private readonly Timer? _deadlineTimer;
    private readonly Action<AggregateException>? _onCallbackException;
    private int _activeCancellations;
    private bool _disposeRequested;

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
        if (timeout is { } value && !IsSupportedTimeout(value))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _source = new CancellationTokenSource();
        _onCallbackException = onCallbackException;
        Token = _source.Token;

        var registrations = parents.Count == 0
            ? Array.Empty<CancellationTokenRegistration>()
            : new CancellationTokenRegistration[parents.Count];
        var registrationCount = 0;
        try
        {
            foreach (var parent in parents)
            {
                if (parent.CanBeCanceled)
                {
                    // UnsafeRegister invokes synchronously when a parent is already
                    // canceled. That can enter TryCancel before _parentRegistrations is
                    // assigned, but this instance cannot be disposed or observed by
                    // external code until construction returns.
                    var registration = parent.UnsafeRegister(
                        static state => ((CancellationSource)state!).TryCancel(),
                        this);
                    registrations[registrationCount++] = registration;
                    if (Token.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
            for (var index = 0; index < registrationCount; index++)
            {
                registrations[index].Dispose();
            }
            _source.Dispose();
            throw;
        }

        _parentRegistrations = registrationCount switch
        {
            0 => [],
            _ when registrationCount == registrations.Length => registrations,
            _ => registrations[..registrationCount],
        };
        _deadlineTimer = null;

        if (timeout is not { } dueTime || Token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            // Keep the timer disabled until all callback-visible fields are
            // assigned. If creation or arming fails, Dispose uses the normal
            // active-cancellation deferral protocol rather than racing a
            // parent or timer callback from a partially built owner.
            _deadlineTimer = CreateDisabledDeadlineTimer();
            if (!_deadlineTimer.Change(dueTime, Timeout.InfiniteTimeSpan))
            {
                throw new InvalidOperationException("The cancellation deadline could not be scheduled.");
            }
            if (Token.IsCancellationRequested)
            {
                _deadlineTimer.Dispose();
                _deadlineTimer = null;
            }
        }
        catch
        {
            Dispose();
            throw;
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

    internal static bool IsSupportedTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero && timeout <= MaxSupportedTimeout;

    /// <summary>
    /// Requests cancellation unless this owner was already disposed. Disposal
    /// closes cancellation admission, so canceling an uncanceled disposed owner
    /// is intentionally a no-op and leaves its stable token uncanceled.
    /// </summary>
    /// <returns><see langword="true"/> when cancellation was admitted.</returns>
    internal bool TryCancel()
    {
        lock (_gate)
        {
            if (_disposeRequested)
            {
                return false;
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
                if (_disposeRequested && _activeCancellations == 0)
                {
                    // Dispose closes admission before observing the count, so
                    // the admitted call that reaches zero is the unique owner
                    // of physical resource disposal.
                    dispose = true;
                }
            }

            Report(callbackException);
            if (dispose)
            {
                DisposeResources();
            }
        }
        return true;
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
            if (_activeCancellations == 0)
            {
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

    private Timer CreateDisabledDeadlineTimer()
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
            static state => ((CancellationSource)state!).TryCancel(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }
}
