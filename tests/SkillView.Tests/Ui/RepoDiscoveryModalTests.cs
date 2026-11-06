using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class RepoDiscoveryModalTests
{
    [Theory]
    [InlineData(80, 24, "Enter")]
    [InlineData(120, 36, "Enter")]
    [InlineData(80, 24, "Cancel")]
    [InlineData(120, 36, "Escape")]
    [InlineData(80, 24, "Close")]
    public async Task FailureRetainsLiteralContextAndSafeDefaultWithoutRetry(int width, int height, string action)
    {
        var calls = 0;
        var ticks = 0;
        const string error = "Connection failed. Retry when available.\n```\n[untrusted](https://invalid.example/)\n\u001b[31mplain\u001b[0m\u0007\u0000";
        var result = await Run(width, height, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Failure(error));
        }, (app, dialog, operation) =>
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            if (++ticks < 3) return; // The failure remains stable; no automatic retry.
            Assert.Equal(1, calls);
            Assert.Equal(["Retry", "Cancel"], dialog.SubViews.OfType<Button>().Select(button => button.Text));
            var cancel = dialog.SubViews.OfType<Button>().Single(button => button.Text == "Cancel");
            Assert.True(cancel.HasFocus);
            Assert.True(cancel.IsDefault);
            Assert.False(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Retry").IsDefault);
            var output = dialog.SubViews.OfType<Editor>().Single();
            Assert.True(output.ReadOnly);
            Assert.True(output.WordWrap);
            Assert.Equal(0, output.CaretOffset);
            Assert.Contains("No skills have been installed. Retry to choose a subset, or Cancel.", output.Text);
            Assert.Contains("Connection failed. Retry when available.", output.Text);
            Assert.Contains("```\n[untrusted](https://invalid.example/)", output.Text);
            Assert.DoesNotContain('\u001b', output.Text);
            Assert.DoesNotContain('\u0007', output.Text);
            Assert.DoesNotContain('\u0000', output.Text);
            Assert.All(dialog.SubViews, control =>
            {
                Assert.InRange(control.Frame.Right, 1, dialog.Viewport.Width);
                Assert.InRange(control.Frame.Bottom, 1, dialog.Viewport.Height);
            });
            switch (action)
            {
                case "Enter": app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter)); break;
                case "Escape": app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); break;
                case "Cancel": cancel.InvokeCommand(Command.Accept); break;
                default: app.RequestStop(); break;
            }
        }, lifetimeToken: TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.Equal(1, calls);
        Assert.True(ticks >= 3);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task ExplicitRetryReturnsOnlySuccessfulSubsetListing(int width, int height)
    {
        var calls = 0;
        var expected = Success();
        var result = await Run(width, height, _ => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? Failure("First listing failed") : expected),
            (app, dialog, operation) =>
            {
                if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None || calls != 1) return;
                var retry = dialog.SubViews.OfType<Button>().Single(button => button.Text == "Retry");
                retry.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
            }, lifetimeToken: TestContext.Current.CancellationToken);
        Assert.Same(expected, result);
        Assert.NotNull(result);
        Assert.Equal(2, calls);
        Assert.Equal(["demo", "other"], result.Skills.Select(skill => skill.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyAndThrownFailuresRemainRetryable(bool throws)
    {
        var calls = 0;
        var result = await Run(80, 24, _ =>
        {
            Interlocked.Increment(ref calls);
            if (throws) throw new IOException("Read-only listing failed\nRetry the connection");
            return Task.FromResult(Success() with { Skills = [] });
        }, (app, dialog, operation) =>
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            Assert.True(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Retry").Enabled);
            Assert.Contains(throws ? "Retry the connection" : "No skills were discovered.", dialog.SubViews.OfType<Editor>().Single().Text);
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
        }, lifetimeToken: TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("Enter", false)]
    [InlineData("Cancel", false)]
    [InlineData("Close", false)]
    [InlineData("Lifetime", false)]
    [InlineData("Stale", false)]
    [InlineData("Enter", true)]
    [InlineData("Cancel", true)]
    [InlineData("Lifetime", true)]
    [InlineData("Stale", true)]
    public async Task PendingOrQueuedSuccessCannotReopenAfterCancellationOrStaleOwner(string action, bool queue)
    {
        using var lifetime = new CancellationTokenSource();
        var pending = new TaskCompletionSource<RepoSkillListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = new ConcurrentQueue<Action>();
        var current = true;
        var calls = 0;
        var acted = false;
        var result = await Run(80, 24, _ =>
        {
            Interlocked.Increment(ref calls);
            return queue ? Task.FromResult(Success()) : pending.Task;
        }, (app, dialog, _) =>
        {
            if (acted)
            {
                while (callbacks.TryDequeue(out var queued)) queued();
                return;
            }
            if (calls == 0 || (queue && callbacks.IsEmpty)) return;
            acted = true;
            if (action == "Lifetime") lifetime.Cancel();
            else if (action == "Stale") current = false;
            else if (action == "Close") app.RequestStop();
            else if (action == "Enter") app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
            else app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
            pending.TrySetResult(Success());
            while (callbacks.TryDequeue(out var callback)) callback();
        }, () => current, lifetime.Token, callbacks);
        while (callbacks.TryDequeue(out var callback)) callback();
        Assert.True(acted);
        Assert.Null(result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RetryRejectsChangedOwnerAndDoesNotStartAnotherChild()
    {
        var current = true;
        var calls = 0;
        var result = await Run(80, 24, _ => { calls++; return Task.FromResult(Failure("failed")); }, (app, dialog, operation) =>
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            current = false;
            dialog.SubViews.OfType<Button>().Single(button => button.Text == "Retry").InvokeCommand(Command.Accept);
        }, () => current, TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task RepeatedFailureRetainsBoundedDetailAndRequiresAnotherExplicitRetry(int width, int height)
    {
        var calls = 0;
        var checkedAgain = false;
        var error = "Connection failed.\n" + new string('`', 100) + "\n" + new string('x', 12000);
        var result = await Run(width, height, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Failure(error));
        }, (app, dialog, operation) =>
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            var pane = dialog.SubViews.OfType<Editor>().Single();
            Assert.InRange(pane.Text.Length, 8192, 8600);
            Assert.EndsWith("[truncated]", pane.Text);
            Assert.Contains(new string('`', 100), pane.Text);
            Assert.Equal(0, pane.CaretOffset);
            if (calls == 1)
            {
                dialog.SubViews.OfType<Button>().Single(button => button.Text == "Retry").InvokeCommand(Command.Accept);
                return;
            }
            checkedAgain = true;
            Assert.Equal(2, calls);
            Assert.True(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Cancel").HasFocus);
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
        }, lifetimeToken: TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.True(checkedAgain);
        Assert.Equal(2, calls);
    }

    private static RepoSkillListing Failure(string message) => RepoSkillListing.Failure("fixture/tools", null, 1, message, ["skill", "install", "--", "fixture/tools"]);
    private static RepoSkillListing Success() => new()
    {
        Repo = "fixture/tools",
        Skills = [new("demo", "Inert fixture"), new("other", "Other fixture")],
        Succeeded = true,
        ExitCode = 0,
        CommandLine = ["skill", "install", "--", "fixture/tools"]
    };

    private static async Task<RepoSkillListing?> Run(int width, int height, Func<CancellationToken, Task<RepoSkillListing>> list,
        Action<IApplication, Dialog, ModalOperationTracker> tick, Func<bool>? current = null,
        CancellationToken lifetimeToken = default, ConcurrentQueue<Action>? callbacks = null)
    {
        return await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            var logger = new Logger(LogLevel.Debug);
            app.Driver!.SetScreenSize(width, height);
            ExceptionDispatchInfo? failure = null;
            var timedOut = false;
            app.AddTimeout(TimeSpan.FromSeconds(6), () => { timedOut = true; app.RequestStop(); return false; });
            var result = RepoDiscoveryModal.Show(app, logger, "fixture/tools", list, current ?? (() => true), lifetimeToken,
                (dialog, operation) => app.AddTimeout(TimeSpan.FromMilliseconds(20), () =>
                {
                    try { tick(app, dialog, operation); }
                    catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); app.RequestStop(); return false; }
                    return dialog.IsRunning;
                }), callbacks is null ? null : callbacks.Enqueue);
            failure?.Throw();
            Assert.False(timedOut, string.Join("\n", logger.Snapshot().Select(entry => entry.Message)));
            return result;
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
