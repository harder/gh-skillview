using System.Collections.Concurrent;
using System.Diagnostics;
using SkillView.Tests.Subprocess;
using System.Runtime.ExceptionServices;
using SkillView.Gh.Models;
using SkillView.Tests.Gh;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InstallAttemptLifetimeTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task ForcedModalCloseDrainsChildAndIndependentRefreshBeforeReturningResult(int width, int height)
    {
        using var fixture = new InstallAttemptTests.Fixture("wait");
        var coordinator = fixture.Coordinator();
        var attempts = coordinator.CreateInstallAttemptRun(fixture.Report);
        var callsAfterDispose = 0;
        var modalDisposed = false;
        var result = await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            Dialog? dialog = null;
            var submitted = false;
            ExceptionDispatchInfo? failure = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { failure = ExceptionDispatchInfo.Capture(new TimeoutException()); app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                try
                {
                    if (!submitted && dialog?.SubViews.OfType<OptionSelector>().Single().Enabled == true)
                    {
                        submitted = true;
                        dialog.SubViews.OfType<Button>().Single(button => button.Text == "Install").InvokeCommand(Command.Accept);
                    }
                    if (!File.Exists(fixture.Payload("demo"))) return true;
                    // Simulate navigation/shutdown closing the nested runnable,
                    // before its canceled service result can reach the UI.
                    app.RequestStop();
                    return false;
                }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); app.RequestStop(); return false; }
            });
            var modal = new InstallConfirmModal(app, fixture.Services.InstallService, fixture.Logger, fixture.Script,
                new("fixture/source", "demo"), attempts: attempts);
            var value = modal.Show((view, _) => dialog = view, action => app.Invoke(() =>
            {
                if (modalDisposed) callsAfterDispose++;
                action();
            }));
            modalDisposed = true;
            failure?.Throw();
            return value;
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(modalDisposed);
        Assert.Equal(0, callsAfterDispose);
        Assert.Equal(InstallAttemptOutcome.ChildCanceled, result.Install!.Attempt);
        Assert.True(result.Install.ChildDrained);
        Assert.Single(result.Attempts!);
        var receipt = Assert.Single(attempts.Receipts);
        Assert.Equal(InstallRefreshState.Completed, receipt.Refresh);
        Assert.Equal(1, fixture.Captures);
        Assert.Contains(receipt.Snapshot!.Skills, skill => skill.Name == "demo");
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task ForcedModalShutdownWithRefusedTerminationReturnsUnknownWriterWithoutLateUi(int width, int height)
    {
        using var child = new RefusingTerminationChild { OnlyMutatingInstalls = true };
        using var fixture = new InstallAttemptTests.Fixture("wait", child.Runner);
        var coordinator = fixture.Coordinator();
        var attempts = coordinator.CreateInstallAttemptRun(fixture.Report);
        var generation = fixture.Services.ListAdapter.Generation;
        var deferred = new ConcurrentQueue<Action>();
        var closing = 0;
        var shutdown = new Stopwatch();
        var modal = Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            Dialog? dialog = null;
            var submitted = false;
            app.AddTimeout(TimeSpan.FromSeconds(5), () =>
            {
                Interlocked.Exchange(ref closing, 1);
                shutdown.Start();
                app.RequestStop();
                return false;
            });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (!submitted && dialog?.SubViews.OfType<OptionSelector>().Single().Enabled == true)
                {
                    submitted = true;
                    dialog.SubViews.OfType<Button>().Single(button => button.Text == "Install").InvokeCommand(Command.Accept);
                }
                if (!File.Exists(fixture.Payload("demo"))) return true;
                Interlocked.Exchange(ref closing, 1);
                shutdown.Start();
                app.RequestStop();
                return false;
            });
            var result = new InstallConfirmModal(app, fixture.Services.InstallService, fixture.Logger, fixture.Script,
                new("fixture/source", "demo"), attempts: attempts).Show((view, _) => dialog = view, action =>
                {
                    if (Volatile.Read(ref closing) != 0) deferred.Enqueue(action);
                    else app.Invoke(action);
                });
            shutdown.Stop();
            // Flush any captured late callbacks after controls/operation have
            // disposed. Their existing active guard must reject UI publication.
            while (deferred.TryDequeue(out var completion)) completion();
            return result;
        }, TestContext.Current.CancellationToken);
        try
        {
            var result = await modal.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            Assert.True(shutdown.Elapsed < TimeSpan.FromSeconds(3));
            Assert.True(child.IsAlive);
            Assert.Equal(InstallAttemptOutcome.ChildCanceled, result.Install!.Attempt);
            Assert.False(result.Install.ChildDrained);
            Assert.Contains("child stdout", result.Install.StdOut);
            Assert.Contains("child stderr", result.Install.StdErr);
            var receipt = Assert.Single(result.Attempts!);
            Assert.Equal(InstallRefreshState.WriterUnverified, receipt.Refresh);
            Assert.Equal(generation + 1, fixture.Services.ListAdapter.Generation);
            Assert.Equal(0, fixture.Captures);
            Assert.DoesNotContain(fixture.Logger.Snapshot(), entry => entry.Level == SkillView.Logging.LogLevel.Error);
            Assert.True(deferred.IsEmpty);
        }
        finally
        {
            // Cleanup survives test cancellation; the modal's own five-second
            // stop timer guarantees termination admission is reached first.
            if (!modal.IsCompleted && !child.IsAlive)
                await Task.WhenAny(modal, child.Observed).WaitAsync(TimeSpan.FromSeconds(6), CancellationToken.None);
            child.Dispose();
            await modal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReconciliationFaultRetainsAttemptBeforeCancelableDelivery()
    {
        var result = InstallResult.Failure("fixture/source", "demo", null, 1, "child failed", []) with
        { Attempt = InstallAttemptOutcome.ChildFailed, ChildDrained = true, StdOut = "partial output" };
        var run = new InstallAttemptRun(_ => throw new IOException("capture failed"));
        var receipt = await run.CompleteAsync(InstallAttemptSummary.Single(result));
        Assert.Same(result, Assert.Single(receipt.Summary.Results));
        Assert.Equal(InstallRefreshState.Failed, receipt.Refresh);
        Assert.Contains("refresh failed", receipt.Guidance);
        Assert.Contains("partial output", InstallFailureDetails.Render(receipt));
        Assert.Single(run.Receipts);
    }
}
