using SkillView.Threading;

namespace SkillView.Ui;

/// <summary>
/// Shares one cancellable asynchronous operation among concurrent callers.
/// Each caller can leave independently; the underlying operation is canceled
/// only after its final waiter leaves.
/// </summary>
internal sealed class SharedAsyncOperation<T>
{
    private readonly object _gate = new();
    private Flight? _active;

    internal async Task<T> GetAsync(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        Flight flight;
        var startsOperation = false;
        lock (_gate)
        {
            flight = _active ??= new Flight();
            if (!flight.Started)
            {
                flight.Started = true;
                startsOperation = true;
            }
            flight.WaiterCount++;
        }

        if (startsOperation)
        {
            flight.Execution = CompleteAsync(flight, operation);
        }

        try
        {
            return await flight.Completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            var finalOperation = ReleaseWaiter(flight);
            if (finalOperation is not null)
            {
                await finalOperation.ConfigureAwait(false);
            }
        }
    }

    private async Task CompleteAsync(Flight flight, Func<CancellationToken, Task<T>> operation)
    {
        try
        {
            var result = await operation(flight.Cancellation.Token).ConfigureAwait(false);
            flight.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex)
        {
            flight.Completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            flight.Completion.TrySetException(ex);
        }
        finally
        {
            var dispose = false;
            lock (_gate)
            {
                if (ReferenceEquals(_active, flight))
                {
                    _active = null;
                }
                flight.OperationFinished = true;
                dispose = flight.WaiterCount == 0;
            }
            if (dispose)
            {
                flight.Cancellation.Dispose();
            }
        }
    }

    private Task? ReleaseWaiter(Flight flight)
    {
        var cancel = false;
        var dispose = false;
        Task? finalOperation = null;
        lock (_gate)
        {
            flight.WaiterCount--;
            if (flight.WaiterCount == 0)
            {
                dispose = flight.OperationFinished;
                if (!flight.Completion.Task.IsCompleted)
                {
                    if (ReferenceEquals(_active, flight))
                    {
                        _active = null;
                    }
                    flight.Completion.TrySetCanceled(flight.Cancellation.Token);
                    cancel = true;
                    finalOperation = flight.Execution;
                }
            }
        }

        if (cancel)
        {
            flight.Cancellation.Cancel();
        }
        if (dispose)
        {
            flight.Cancellation.Dispose();
        }
        return finalOperation;
    }

    private sealed class Flight
    {
        internal CancellationSource Cancellation { get; } = new();
        internal TaskCompletionSource<T> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Started { get; set; }
        internal int WaiterCount { get; set; }
        internal bool OperationFinished { get; set; }
        internal Task? Execution { get; set; }
    }
}
