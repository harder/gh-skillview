using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
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
    public async Task RunAsync_AnsiInstalledList_HandlesRepeatedArrowsBeyondBothEnds()
    {
        const int count = 40;
        var snapshot = InventorySnapshot.Empty with
        {
            Skills = Enumerable.Range(0, count).Select(i => new InstalledSkill
            {
                Name = $"skill-{i:D3}",
                ResolvedPath = $"/skills/skill-{i:D3}",
                ScanRoot = "/skills",
                Scope = Scope.User,
                Agents = [],
                FrontMatter = SkillFrontMatter.Empty,
                Validity = ValidityState.Valid,
                Provenance = Provenance.FsScan,
                Ignored = false,
                IsSymlinked = false,
                InstalledAt = null,
            }).ToImmutableArray(),
        };
        SkillViewApp? skillview = null;
        var step = 0;
        var boundaryChecked = false;
        var options = new AppOptions(
            InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []);
        skillview = new SkillViewApp(TuiServices.Build(new Logger(LogLevel.Debug)), options, () =>
        {
            var terminal = Application.Create().Init("ansi");
            terminal.Driver!.SetScreenSize(120, 36);
            terminal.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                var installed = skillview!.InstalledTabForTests!;
                if (step == 0)
                {
                    installed.LoadSeeded(snapshot);
                    skillview.ForceActiveTabForTests(SkillViewTab.Installed);
                    installed.FocusList();
                }
                if (step < count + 10)
                {
                    terminal.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.CursorDown));
                }
                else if (step < 2 * (count + 10))
                {
                    if (step == count + 10)
                    {
                        Assert.Equal("skill-039", installed.GetSelectedSkill()?.Name);
                        Assert.True(installed.TableHasFocusForTests);
                    }
                    terminal.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.CursorUp));
                }
                else
                {
                    Assert.Equal("skill-000", installed.GetSelectedSkill()?.Name);
                    Assert.True(installed.TableHasFocusForTests);
                    boundaryChecked = true;
                    terminal.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Q | KeyCode.CtrlMask));
                    return false;
                }
                step++;
                return true;
            });
            return terminal;
        }, probeOnRun: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var exitCode = await skillview.RunAsync(deadline.Token);

        Assert.True(boundaryChecked);
        Assert.False(deadline.IsCancellationRequested);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

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

    [Fact]
    public async Task RunAsync_ExternalCancellation_ReturnsCancelledAfterTeardown()
    {
        IApplication? created = null;
        IApplication? disposed = null;

        void HandleDisposed(object? _, EventArgs<IApplication> e)
        {
            if (ReferenceEquals(e.Value, created))
            {
                disposed = e.Value;
            }
        }

        var services = TuiServices.Build(new Logger(LogLevel.Debug));
        var options = new AppOptions(
            InvocationMode.Standalone,
            DispatchMode.Tui,
            Debug: false,
            Theme: AppTheme.Default,
            ScanRoots: [],
            SubcommandName: null,
            SubcommandArgs: []);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var cancellationCallbackRan = false;
        var app = new SkillViewApp(
            services,
            options,
            () =>
            {
                var application = Application.Create();
                application.Init("ansi");
                created = application;
                application.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    cancellationCallbackRan = true;
                    cancellation.Cancel();
                    return false;
                });
                return application;
            },
            probeOnRun: false);

        Application.InstanceDisposed += HandleDisposed;
        try
        {
            var exitCode = await app
                .RunAsync(cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(cancellationCallbackRan);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(ExitCodes.Cancelled, exitCode);
            Assert.NotNull(created);
            Assert.Same(created, disposed);
        }
        finally
        {
            Application.InstanceDisposed -= HandleDisposed;
        }
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

    [Fact]
    public async Task RunAsync_RapidTabSwitches_ThenQuit_CompletesWithoutTimeout()
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
        var keys = new List<Key> { new(KeyCode.Esc) };
        for (var index = 0; index < 10; index++)
        {
            keys.Add(new Key('2'));
            keys.Add(new Key('3'));
            keys.Add(new Key('1'));
        }
        keys.Add(new Key(KeyCode.Q | KeyCode.CtrlMask));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
