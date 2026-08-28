namespace SkillView.Ui;

/// <summary>
/// Owns fire-and-forget work for one application lifecycle. A reservation is
/// registered before the operation can start, so shutdown can atomically stop
/// accepting work and await every operation that was already admitted.
/// </summary>
internal sealed class BackgroundTaskTracker
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _active = [];
    private readonly Action<Exception> _onUnhandledException;
    private bool _accepting = true;

    internal BackgroundTaskTracker(Action<Exception> onUnhandledException)
    {
        _onUnhandledException = onUnhandledException;
    }

    internal bool TryRun(Func<Task> operation, bool runOnThreadPool = false)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_accepting)
            {
                return false;
            }
            _active.Add(completion.Task);
        }

        _ = CompleteAsync(operation, completion, runOnThreadPool);
        return true;
    }

    internal void StopAccepting()
    {
        lock (_gate)
        {
            _accepting = false;
        }
    }

    internal async Task DrainAsync()
    {
        Task[] active;
        lock (_gate)
        {
            if (_accepting)
            {
                throw new InvalidOperationException("StopAccepting must be called before draining tasks.");
            }
            active = _active.ToArray();
        }

        await Task.WhenAll(active).ConfigureAwait(false);
    }

    private async Task CompleteAsync(
        Func<Task> operation,
        TaskCompletionSource completion,
        bool runOnThreadPool)
    {
        try
        {
            if (runOnThreadPool)
            {
                await Task.Run(operation).ConfigureAwait(false);
            }
            else
            {
                await operation().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            try { _onUnhandledException(ex); }
            catch { /* fault reporting must not create an unobserved tracker task */ }
        }
        finally
        {
            completion.TrySetResult();
            lock (_gate)
            {
                _active.Remove(completion.Task);
            }
        }
    }
}
