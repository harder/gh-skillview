using SkillView.Logging;
using Terminal.Gui.App;

namespace SkillView.Ui;

/// <summary>
/// Owns one asynchronous operation for the lifetime of a synchronous
/// Terminal.Gui modal. The operation remains owned after its worker task has
/// completed until the queued UI completion explicitly calls <see cref="Release"/>.
/// </summary>
internal sealed class ModalOperationTracker : IDisposable
{
    internal enum Ownership
    {
        None,
        Running,
        AwaitingUiCompletion,
    }

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Action<Action> _invoke;
    private readonly Logger _logger;
    private readonly string _logCategory;
    private Task? _operation;
    private bool _releaseRequested;
    private int _active = 1;
    private bool _disposed;

    internal ModalOperationTracker(IApplication app, Logger logger, string logCategory)
        : this(app.Invoke, logger, logCategory)
    {
    }

    internal ModalOperationTracker(Action<Action> invoke, Logger logger, string logCategory)
    {
        _invoke = invoke;
        _logger = logger;
        _logCategory = logCategory;
        Token = _cancellation.Token;
    }

    /// <summary>
    /// Stable token captured before any await. It remains safe to inspect while
    /// disposal waits for the owned operation to finish.
    /// </summary>
    internal CancellationToken Token { get; }

    internal Ownership CurrentOwnership
    {
        get
        {
            lock (_gate)
            {
                return _operation switch
                {
                    null => Ownership.None,
                    { IsCompleted: false } => Ownership.Running,
                    _ => Ownership.AwaitingUiCompletion,
                };
            }
        }
    }

    internal bool TryStart(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_gate)
        {
            if (_disposed || _operation is not null)
            {
                return false;
            }

            // Yield before invoking user work so ownership is published before
            // even a synchronously-completing operation can queue its UI commit.
            _releaseRequested = false;
            _operation = StartAfterOwnershipPublishedAsync(operation, Token);
            return true;
        }
    }

    internal void Release()
    {
        lock (_gate)
        {
            _releaseRequested = true;
            if (_operation?.IsCompleted == true)
            {
                _operation = null;
                _releaseRequested = false;
            }
        }
    }

    internal void Cancel()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal void InvokeIfActive(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref _active) == 0)
        {
            return;
        }

        try
        {
            _invoke(() =>
            {
                if (Volatile.Read(ref _active) == 0)
                {
                    return;
                }

                try { action(); }
                catch (Exception ex) { _logger.Error(_logCategory, ex.Message); }
            });
        }
        catch (Exception ex)
        {
            _logger.Error(_logCategory, ex.Message);
        }
    }

    public void Dispose()
    {
        Task? operation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Exchange(ref _active, 0);
            operation = _operation;
        }

        Cancel();
        try
        {
            operation?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Worker bodies should contain their own failures, but disposal is
            // the final observation boundary and must never leak async-void-like
            // exceptions through Terminal.Gui teardown.
            _logger.Error(_logCategory, ex.Message);
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task StartAfterOwnershipPublishedAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Release can run inside the worker's queued UI callback. Do not
            // clear ownership at that point: the worker has not returned yet.
            // Clearing here closes the final worker-complete/UI-committed race.
            lock (_gate)
            {
                if (_releaseRequested)
                {
                    _operation = null;
                    _releaseRequested = false;
                }
            }
        }
    }
}
