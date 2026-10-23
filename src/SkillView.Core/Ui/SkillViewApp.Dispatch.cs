using SkillView.Threading;
using Terminal.Gui.App;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{

    private void Invoke(Action action)
    {
        var lifetime = _runLifetime;
        var app = _app;

        if (app is not null)
        {
            if (lifetime?.IsCancellationRequested == true)
            {
                return;
            }

            app.Invoke(() =>
            {
                if (lifetime?.IsCancellationRequested == true)
                {
                    return;
                }

                action();
            });
            return;
        }

        // Unit helpers build the view before a real run and intentionally use
        // direct dispatch. Once lifecycle entry has happened, a missing app
        // always means teardown or post-teardown and must be a no-op.
        if (!_hasEnteredRunLifecycle)
        {
            action();
        }
    }

    private async Task<bool> InvokeAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        var lifetime = _runLifetime;
        var app = _app;

        if (app is not null)
        {
            using var dispatchLifetime = lifetime is null
                ? new CancellationSource(
                    cancellationToken,
                    CancellationCallbackReporter.For(_services.Logger, "UI dispatch"))
                : new CancellationSource(
                    cancellationToken,
                    lifetime.Token,
                    CancellationCallbackReporter.For(_services.Logger, "UI dispatch"));
            return await AwaitDispatchAsync(
                    app.Invoke,
                    action,
                    dispatchLifetime.Token)
                .ConfigureAwait(false);
        }

        if (!_hasEnteredRunLifecycle && !cancellationToken.IsCancellationRequested)
        {
            action();
            return true;
        }
        return false;
    }

    private async Task<bool> InvokeOwnedAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        var lifetime = _runLifetime;
        var app = _app;

        if (app is not null)
        {
            using var dispatchLifetime = lifetime is null
                ? new CancellationSource(
                    cancellationToken,
                    CancellationCallbackReporter.For(_services.Logger, "owned UI dispatch"))
                : new CancellationSource(
                    cancellationToken,
                    lifetime.Token,
                    CancellationCallbackReporter.For(_services.Logger, "owned UI dispatch"));
            return await AwaitOwnedDispatchAsync(
                    app.Invoke,
                    callback => StageForNextIteration(app, callback),
                    action,
                    dispatchLifetime.Token)
                .ConfigureAwait(false);
        }

        if (!_hasEnteredRunLifecycle && !cancellationToken.IsCancellationRequested)
        {
            action();
            return true;
        }
        return false;
    }

    internal static async Task<bool> AwaitDispatchAsync(
        Action<Action> dispatch,
        Action action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatch(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetResult(false);
                return;
            }

            try
            {
                action();
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        try
        {
            return await completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    internal static async Task<bool> AwaitOwnedDispatchAsync(
        Action<Action> dispatch,
        Action<Action> stageForNextIteration,
        Action action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(stageForNextIteration);
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // 0 = queued, 1 = staged for the next iteration, 2 = callback started,
        // 3 = canceled before start. IApplication.Invoke runs worker-thread
        // callbacks as TimedEvents callbacks. Starting a synchronous modal in
        // that callback holds Terminal.Gui's timer lock for the entire nested
        // run, deadlocking any modal worker that reports progress via Invoke.
        // Stage the modal for the next main-loop iteration so the timer callback
        // returns and releases that lock first.
        var dispatchState = 0;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() =>
        {
            var state = Volatile.Read(ref dispatchState);
            while (state is 0 or 1)
            {
                var observed = Interlocked.CompareExchange(ref dispatchState, 3, state);
                if (observed == state)
                {
                    completion.TrySetResult(false);
                    return;
                }
                state = observed;
            }
        });

        dispatch(() =>
        {
            if (Interlocked.CompareExchange(ref dispatchState, 1, 0) != 0)
            {
                completion.TrySetResult(false);
                return;
            }

            try
            {
                stageForNextIteration(() =>
                {
                    if (Interlocked.CompareExchange(ref dispatchState, 2, 1) != 1)
                    {
                        completion.TrySetResult(false);
                        return;
                    }

                    try
                    {
                        action();
                        completion.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return await completion.Task.ConfigureAwait(false);
    }

    private static void StageForNextIteration(IApplication app, Action action)
    {
        void RunOnce(object? sender, EventArgs args)
        {
            app.Iteration -= RunOnce;
            action();
        }

        app.Iteration += RunOnce;
    }

    /// Fire-and-forget background work with exception guard. Catches any
    /// unhandled exception, logs it, and shows a status bar message so
    /// failures are never silently swallowed.
    private void RunBackground(Func<CancellationToken, Task> work, string operation)
    {
        var cancellationToken = GetRunLifetimeToken();
        RunOwnedTask(async () =>
        {
            try
            {
                await work(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _services.Logger.Debug(operation, $"{operation} canceled during shutdown");
            }
            catch (Exception ex)
            {
                _services.Logger.Error(operation, ex.Message);
                Invoke(() =>
                {
                    ClearBusy();
                    var snippet = TuiHelpers.ErrorSnippet(ex.Message);
                    SetStatus(snippet.Length > 0
                        ? $"{operation} failed: {snippet}"
                        : $"{operation} failed — see logs (l)",
                        TuiHelpers.NotificationLevel.Error);
                });
            }
        }, operation, runOnThreadPool: true);
    }

    private void RunOwnedTask(Func<Task> work, string operation) =>
        RunOwnedTask(work, operation, runOnThreadPool: false);

    private void RunOwnedTask(Func<Task> work, string operation, bool runOnThreadPool)
    {
        if (!_backgroundTasks.TryRun(work, runOnThreadPool))
        {
            _services.Logger.Debug(operation, $"{operation} skipped during shutdown");
        }
    }

    private CancellationToken GetRunLifetimeToken() => _runLifetime?.Token ?? CancellationToken.None;
}
