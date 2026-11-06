using System.Collections.Immutable;
using System.Diagnostics;
using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.IntegrationTests.Ui;

public sealed partial class SkillViewAppIntegrationTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public async Task RunAsync_DiscoverStartupTypingReachesQueryWithoutFocusShortcut(int width, int height)
    {
        SkillViewApp? skillview = null;
        var checkedInput = false;
        var options = new AppOptions(
            InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []);
        skillview = new SkillViewApp(TuiServices.Build(new Logger(LogLevel.Debug)), options, () =>
        {
            var terminal = Application.Create().Init("ansi");
            terminal.Driver!.SetScreenSize(width, height);
            terminal.AddTimeout(TimeSpan.FromMilliseconds(20), () =>
            {
                Assert.Equal(SkillViewTab.Discover, skillview!.ActiveTabForTests);
                Assert.True(skillview.QueryFieldForTests!.HasFocus, "Discover should initially focus the query input.");
                foreach (var character in "focusprobe") terminal.Keyboard.RaiseKeyDownEvent(new Key(character));
                Assert.Equal("focusprobe", skillview.QueryFieldForTests.Text);
                Assert.Equal(SkillViewTab.Discover, skillview.ActiveTabForTests);
                Assert.True(skillview.UserInteractedSinceLaunchForTests);
                checkedInput = true;
                terminal.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Q | KeyCode.CtrlMask));
                return false;
            });
            return terminal;
        }, probeOnRun: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var exitCode = await skillview.RunAsync(deadline.Token);

        Assert.True(checkedInput);
        Assert.False(deadline.IsCancellationRequested);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

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
        var keysSent = 0;
        var ticks = 0;
        var downDraws = 0;
        var upDraws = 0;
        var expectedRow = 0;
        string? observedName = null;
        var listFocused = false;
        var elapsed = Stopwatch.StartNew();
        var boundaryChecked = false;
        TimeSpan? quitRequestedAt = null;
        string Progress() => $"phase={(keysSent < count * 2 + 10 ? "down" : "up")}; ticks={ticks}; keys={keysSent}; "
            + $"expected={expectedRow}; observed={observedName}; focused={listFocused}; "
            + $"draws={downDraws}/{upDraws}; quitRequestedAt={quitRequestedAt}; elapsed={elapsed.Elapsed}";
        var options = new AppOptions(
            InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []);
        skillview = new SkillViewApp(TuiServices.Build(new Logger(LogLevel.Debug)), options, () =>
        {
            var terminal = Application.Create().Init("ansi");
            terminal.Driver!.SetScreenSize(120, 36);
            terminal.LayoutAndDrawComplete += (_, _) =>
            {
                if (keysSent > 0 && keysSent <= count * 2 + 10) downDraws++;
                else if (keysSent > count * 2 + 10) upDraws++;
            };
            terminal.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                ticks++;
                var installed = skillview!.InstalledTabForTests!;
                if (keysSent == 0)
                {
                    installed.LoadSeeded(snapshot);
                    skillview.ForceActiveTabForTests(SkillViewTab.Installed);
                    installed.FocusList();
                }
                if (keysSent == count * 2 + 10 && downDraws == 0) return true;
                if (keysSent == 2 * (count * 2 + 10))
                {
                    if (upDraws == 0) return true;
                    observedName = installed.GetSelectedSkill()?.Name;
                    listFocused = installed.TableHasFocusForTests;
                    Assert.True(observedName is null && listFocused, Progress());
                    Assert.True(downDraws > 0 && upDraws > 0, Progress());
                    boundaryChecked = true;
                    quitRequestedAt = elapsed.Elapsed;
                    terminal.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Q | KeyCode.CtrlMask));
                    return false;
                }
                // Exercise one terminal repeat burst per direction. Every key
                // still verifies selection/focus, and each boundary must actually
                // draw before changing direction/quitting. Five-key batches tied
                // selection checks to 36 timer batches and many full paints.
                for (var repeat = 0; repeat < count * 2 + 10; repeat++)
                {
                    var down = keysSent < count * 2 + 10;
                    terminal.Keyboard.RaiseKeyDownEvent(new Key(down ? KeyCode.CursorDown : KeyCode.CursorUp));
                    keysSent++;
                    expectedRow = down ? Math.Min(keysSent, count * 2 - 1)
                        : Math.Max(count * 2 - 1 - (keysSent - count * 2 - 10), 0);
                    observedName = installed.GetSelectedSkill()?.Name;
                    listFocused = installed.TableHasFocusForTests;
                    Assert.True(observedName == (expectedRow % 2 == 0 ? null : $"skill-{expectedRow / 2:D3}"), Progress());
                    Assert.True(listFocused, Progress());
                }
                return true;
            });
            return terminal;
        }, probeOnRun: false);
        // Keep startup History in the app-owned lifecycle without consulting
        // ambient runner state in this seeded keyboard/rendering fixture.
        skillview.SetHistoryLoaderForTests(_ => Task.FromResult(new ReplacementHistory([], 0, false)));
        // This watchdog bounds a stalled event loop. Two full ANSI boundary
        // paints suffice; all 180 key/selection/focus assertions remain.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var exitCode = await skillview.RunAsync(deadline.Token);

        Assert.True(boundaryChecked, Progress());
        Assert.Equal(2 * (count * 2 + 10), keysSent);
        Assert.False(deadline.IsCancellationRequested, Progress());
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

        // Terminal.Gui 2.5's facade event is thread-local. Shutdown can resume
        // off the creation thread after draining app-owned work.
        void HandleDisposed(object? sender, EventArgs<bool> e)
        {
            if (!e.Value && ReferenceEquals(sender, created)) disposed = (IApplication)sender!;
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
                application.InitializedChanged += HandleDisposed;
                application.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    cancellationCallbackRan = true;
                    cancellation.Cancel();
                    return false;
                });
                return application;
            },
            probeOnRun: false);

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
            Assert.False(created.Initialized);
            Assert.Null(created.Driver);
            Assert.Null(created.MainThreadId);
        }
        finally
        {
            if (created is not null) created.InitializedChanged -= HandleDisposed;
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
