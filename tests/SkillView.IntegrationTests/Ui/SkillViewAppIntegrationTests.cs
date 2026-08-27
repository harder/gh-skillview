using SkillView.Bootstrapping;
using SkillView.Logging;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.IntegrationTests.Ui;

public sealed class SkillViewAppIntegrationTests
{
    [Fact]
    public async Task RunAsync_WithAnsiDriverAndSingleTick_ReturnsSuccess()
    {
        var services = TuiServices.Build(new Logger(LogLevel.Debug));
        var options = new AppOptions(
            InvocationMode.Standalone,
            DispatchMode.Tui,
            Debug: false,
            Theme: AppTheme.Default,
            ScanRoots: [],
            SubcommandName: null,
            SubcommandArgs: []);

        var app = new SkillViewApp(services, options, CreateAnsiApp, probeOnRun: false);

        var exitCode = await app.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("ctrl-q-from-query")]
    [InlineData("q-from-discover-table")]
    [InlineData("q-from-installed")]
    [InlineData("q-from-doctor")]
    [InlineData("ctrl-q-from-help")]
    public async Task RunAsync_QuitPaths_StopFromEveryTopLevelWorkspace(string scenario)
    {
        var services = TuiServices.Build(new Logger(LogLevel.Debug));
        var options = new AppOptions(
            InvocationMode.Standalone,
            DispatchMode.Tui,
            Debug: false,
            Theme: AppTheme.Default,
            ScanRoots: [],
            SubcommandName: null,
            SubcommandArgs: []);
        Key[] keys = scenario switch
        {
            "ctrl-q-from-query" => [new Key(KeyCode.Q | KeyCode.CtrlMask)],
            "q-from-discover-table" => [new Key(KeyCode.Esc), new Key('q')],
            "q-from-installed" => [new Key(KeyCode.Esc), new Key('2'), new Key('q')],
            "q-from-doctor" => [new Key(KeyCode.Esc), new Key('d'), new Key('q')],
            "ctrl-q-from-help" =>
                [new Key(KeyCode.Esc), new Key('?'), new Key(KeyCode.Q | KeyCode.CtrlMask)],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var app = new SkillViewApp(
            services,
            options,
            () => CreateAnsiAppWithInput(keys),
            probeOnRun: false);

        var exitCode = await app.RunAsync(timeout.Token);

        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    private static IApplication CreateAnsiApp()
    {
        var app = Application.Create();
        app.Init("ansi");
        app.StopAfterFirstIteration = true;
        return app;
    }

    private static IApplication CreateAnsiAppWithInput(IReadOnlyList<Key> keys)
    {
        var app = Application.Create();
        app.Init("ansi");
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            app.AddTimeout(TimeSpan.FromMilliseconds(10 * (i + 1)), () =>
            {
                app.Keyboard.RaiseKeyDownEvent(key);
                return false;
            });
        }
        return app;
    }
}
