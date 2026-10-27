using SkillView.Bootstrapping;
using SkillView.Logging;
using SkillView.Ui;
using Terminal.Gui.App;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class SkillViewFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_DisposesApplicationAfterUiFailure(bool failDuringDisposal)
    {
        IApplication? terminal = null;
        IApplication? disposed = null;
        var failureReached = false;
        var failure = new InvalidOperationException("injected UI failure");
        var logger = new Logger(LogLevel.Debug);
        var skillview = new SkillViewApp(TuiServices.Build(logger), new AppOptions(
            InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []),
            () =>
            {
                terminal = Application.Create().Init();
                terminal.InitializedChanged += OnDisposed;
                terminal.Driver!.SetScreenSize(120, 36);
                terminal.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    failureReached = true;
                    if (failDuringDisposal)
                    {
                        terminal.TopRunnableView!.Disposing += (_, _) => throw failure;
                        terminal.RequestStop();
                        return false;
                    }
                    throw failure;
                });
                return terminal;
            }, probeOnRun: false);

        void OnDisposed(object? sender, EventArgs<bool> args)
        {
            if (!args.Value && ReferenceEquals(terminal, sender)) disposed = (IApplication)sender!;
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var exception = await Record.ExceptionAsync(() => skillview.RunAsync(deadline.Token));
            Assert.True(failureReached);
            Assert.False(deadline.IsCancellationRequested);
            Assert.Same(failure, exception);
            Assert.Same(terminal, disposed);
            Assert.False(terminal!.Initialized);
            Assert.Null(terminal.Driver);
            Assert.Null(terminal.MainThreadId);
            Assert.Contains(logger.Snapshot(), entry => entry.Message.Contains("injected UI failure", StringComparison.Ordinal));
        }
        finally
        {
            if (terminal is not null) terminal.InitializedChanged -= OnDisposed;
            if (disposed is null) terminal?.Dispose();
        }
    }
}
