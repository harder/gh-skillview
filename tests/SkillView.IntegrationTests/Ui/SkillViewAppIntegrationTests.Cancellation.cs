using SkillView.Bootstrapping;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Terminal.Gui.App;
using Xunit;

namespace SkillView.IntegrationTests.Ui;

public sealed partial class SkillViewAppIntegrationTests
{
    [Theory]
    [InlineData("before-first-draw")]
    [InlineData("history-queued")]
    [InlineData("history-read-blocked")]
    public async Task RunAsync_CancellationAcrossHistoryStages_DisposesExactInstanceAfterDrain(string stage)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var canceledFromLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IApplication? created = null, disposed = null;
        var creationThread = 0; var historyThread = 0; var disposalThread = 0;
        var reads = 0;
        var callbackRan = false;
        void ObserveDisposed(object? sender, EventArgs<bool> args)
        {
            if (args.Value || !ReferenceEquals(sender, created)) return;
            disposalThread = Environment.CurrentManagedThreadId;
            Volatile.Write(ref disposed, (IApplication)sender!);
        }
        void CancelFromLoop()
        {
            callbackRan = true;
            cancellation.Cancel();
            canceledFromLoop.TrySetResult();
        }
        void BeforeFirstDraw(object? sender, SessionTokenEventArgs args)
        {
            created!.SessionBegun -= BeforeFirstDraw;
            CancelFromLoop();
        }
        var subject = new SkillViewApp(TuiServices.Build(new Logger(LogLevel.Debug)),
            new AppOptions(InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []), () =>
            {
                created = Application.Create().Init("ansi");
                created.Driver!.SetScreenSize(80, 24);
                creationThread = Environment.CurrentManagedThreadId;
                created.InitializedChanged += ObserveDisposed;
                if (stage == "before-first-draw") created.SessionBegun += BeforeFirstDraw;
                else created.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (!historyQueued.Task.IsCompleted) return true;
                    CancelFromLoop();
                    return false;
                });
                return created;
            }, probeOnRun: false);
        subject.SetHistoryLoaderForTests(async token =>
        {
            token.ThrowIfCancellationRequested();
            historyThread = Environment.CurrentManagedThreadId;
            historyQueued.TrySetResult();
            if (stage == "history-queued") await neverRead.Task.WaitAsync(token).ConfigureAwait(false);
            else
            {
                Interlocked.Increment(ref reads);
                // Model an admitted off-UI native read that must finish before
                // cleanup, even after its owner requests cancellation.
                await releaseRead.Task.ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return new ReplacementHistory([], 0, false);
        });

        // A dedicated UI thread remains blocked on the RunAsync result while
        // the held read is released elsewhere. This guarantees an async-drain
        // thread handoff without sleeps, retries or pool scheduling assumptions.
        var run = Task.Factory.StartNew(() =>
        {
            try
            {
                var pending = subject.RunAsync(cancellation.Token);
                runYielded.TrySetResult();
                return pending.GetAwaiter().GetResult();
            }
            finally
            {
                if (created is not null)
                {
                    created.SessionBegun -= BeforeFirstDraw;
                    created.InitializedChanged -= ObserveDisposed;
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await canceledFromLoop.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (stage == "history-read-blocked")
            {
                // Cancellation can be observed before the GUI loop exits.
                // Keep the read held until RunAsync has actually yielded in
                // the drain, so its cleanup cannot still complete inline.
                await runYielded.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.False(run.IsCompleted);
                Assert.Null(Volatile.Read(ref disposed));
                Assert.NotNull(created);
                Assert.True(created.Initialized);
                Assert.NotNull(created.Driver);
            }
            releaseRead.TrySetResult();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(callbackRan);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(ExitCodes.Cancelled, exit);
            Assert.NotNull(created);
            Assert.Same(created, disposed);
            Assert.False(created.Initialized);
            Assert.Null(created.Driver);
            Assert.Null(created.MainThreadId);
            Assert.Equal(stage == "history-read-blocked" ? 1 : 0, reads);
            if (stage != "before-first-draw") Assert.NotEqual(creationThread, historyThread);
            if (stage == "history-read-blocked") Assert.NotEqual(creationThread, disposalThread);
        }
        finally
        {
            releaseRead.TrySetResult();
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }
}
