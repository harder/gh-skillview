using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using SkillView.Ui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class SkillViewAppTests
{
    private static GhAuthStatus LoggedInAuth(string? activeHost = "github.com") => new()
    {
        LoggedIn = true,
        ActiveHost = activeHost,
        Account = "octocat",
        Hosts = activeHost is null ? ImmutableArray<string>.Empty : ImmutableArray.Create(activeHost),
        RawOutput = string.Empty,
    };

    private static GhAuthStatus LoggedOutAuth(string? activeHost = "github.com") => new()
    {
        LoggedIn = false,
        ActiveHost = activeHost,
        Account = null,
        Hosts = activeHost is null ? ImmutableArray<string>.Empty : ImmutableArray.Create(activeHost),
        RawOutput = string.Empty,
    };

    private static EnvironmentReport CreateEnvironmentReport() => new()
    {
        GhPath = "/usr/bin/gh",
        GhVersionRaw = "gh version 2.95.0",
        GhVersion = new SemVer(2, 95, 0),
        GhMeetsMinimum = true,
        Auth = GhAuthStatus.Unknown,
        GhSkillAvailable = true,
        LogDirectory = "/tmp/skillview-logs",
    };

    private static SkillViewApp CreateApp()
    {
        var services = TuiServices.Build(new Logger(LogLevel.Debug));
        var options = new AppOptions(
            InvocationMode.Standalone,
            DispatchMode.Tui,
            Debug: false,
            Theme: AppTheme.Default,
            ScanRoots: Array.Empty<string>(),
            SubcommandName: null,
            SubcommandArgs: Array.Empty<string>());

        return new SkillViewApp(services, options);
    }

    private static SkillViewApp CreateApp(bool probeOnRun)
    {
        var services = TuiServices.Build(new Logger(LogLevel.Debug));
        var options = new AppOptions(
            InvocationMode.Standalone,
            DispatchMode.Tui,
            Debug: false,
            Theme: AppTheme.Default,
            ScanRoots: Array.Empty<string>(),
            SubcommandName: null,
            SubcommandArgs: Array.Empty<string>());

        return new SkillViewApp(services, options, static () => Application.Create().Init(), probeOnRun);
    }

    private static AppOptions CreateOptions() => new(
        InvocationMode.Standalone,
        DispatchMode.Tui,
        Debug: false,
        Theme: AppTheme.Default,
        ScanRoots: Array.Empty<string>(),
        SubcommandName: null,
        SubcommandArgs: Array.Empty<string>());

    private static IEnumerable<View> Descendants(View root)
    {
        foreach (var child in root.SubViews)
        {
            yield return child;

            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static InventorySnapshot SnapshotWithInstalledSkill() => InventorySnapshot.Empty with
    {
        Skills = ImmutableArray.Create(new InstalledSkill
        {
            Name = "demo",
            ResolvedPath = "/skills/demo",
            ScanRoot = "/skills",
            Scope = Scope.User,
            Agents = ImmutableArray.Create(new AgentMembership("github-copilot", "/skills/demo", false)),
            FrontMatter = SkillFrontMatter.Empty,
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
        }),
    };

    private static InventorySnapshot SnapshotWithInstalledSkills() => InventorySnapshot.Empty with
    {
        Skills = ImmutableArray.Create(
            new InstalledSkill
            {
                Name = "alpha",
                ResolvedPath = "/skills/alpha",
                ScanRoot = "/skills",
                Scope = Scope.User,
                Agents = ImmutableArray.Create(new AgentMembership("github-copilot", "/skills/alpha", false)),
                FrontMatter = SkillFrontMatter.Empty,
                Validity = ValidityState.Valid,
                Provenance = Provenance.FsScan,
                Ignored = false,
                IsSymlinked = false,
                InstalledAt = null,
            },
            new InstalledSkill
            {
                Name = "beta",
                ResolvedPath = "/project/skills/beta",
                ScanRoot = "/project/skills",
                Scope = Scope.Project,
                Agents = ImmutableArray.Create(new AgentMembership("claude", "/project/skills/beta", false)),
                FrontMatter = SkillFrontMatter.Empty,
                Validity = ValidityState.MissingSkillMd,
                Provenance = Provenance.CliList,
                Ignored = false,
                IsSymlinked = true,
                InstalledAt = null,
            }),
    };

    [Fact]
    public void ShouldOpenInstalledOnStartup_ReturnsFalse_ForEmptyInventory()
    {
        Assert.False(SkillViewApp.ShouldOpenInstalledOnStartup(InventorySnapshot.Empty));
    }

    [Fact]
    public void ShouldOpenInstalledOnStartup_ReturnsTrue_WhenInventoryHasSkills()
    {
        var snapshot = SnapshotWithInstalledSkill();

        Assert.True(SkillViewApp.ShouldOpenInstalledOnStartup(snapshot));
    }

    [Fact]
    public void ShouldAutoOpenInstalledOnStartup_ReturnsFalse_AfterUserInteraction()
    {
        var snapshot = SnapshotWithInstalledSkill();

        Assert.False(SkillViewApp.ShouldAutoOpenInstalledOnStartup(
            snapshot,
            startupInstalledShown: false,
            userInteractedSinceLaunch: true));
    }

    [Fact]
    public void StartupAutoOpen_IsSuppressed_AfterLimitControlInteraction()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        _ = app.LimitUpDownForTests!.NewKeyDownEvent(new Key(KeyCode.CursorUp));

        Assert.True(app.UserInteractedSinceLaunchForTests);
        Assert.False(app.ShouldAutoOpenInstalledOnStartupForTests(SnapshotWithInstalledSkill()));
    }

    [Fact]
    public void StartupAutoOpen_IsSuppressed_AfterFocusDrivenInteraction()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        _ = app.QueryFieldForTests!.SetFocus();
        Assert.False(app.UserInteractedSinceLaunchForTests);

        _ = app.OwnerFieldForTests!.SetFocus();

        Assert.True(app.UserInteractedSinceLaunchForTests);
        Assert.False(app.ShouldAutoOpenInstalledOnStartupForTests(SnapshotWithInstalledSkill()));
    }

    [Fact]
    public void StartupAutoOpen_PrimesInitialFocus_WithoutMarkingInteraction()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        _ = app.QueryFieldForTests!.SetFocus();

        Assert.False(app.UserInteractedSinceLaunchForTests);
        Assert.True(app.ShouldAutoOpenInstalledOnStartupForTests(SnapshotWithInstalledSkill()));
    }

    [Fact]
    public async Task RunAsync_ReturnsSuccess_WithoutCreatingApplication_WhenAlreadyCanceled()
    {
        var services = TuiServices.Build(new Logger(LogLevel.Debug));
        var factoryCalled = false;
        var app = new SkillViewApp(
            services,
            CreateOptions(),
            () =>
            {
                factoryCalled = true;
                return Application.Create().Init();
            },
            probeOnRun: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exitCode = await app.RunAsync(cts.Token);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.False(factoryCalled);
    }

    [Fact]
    public void FocusSearchFromInstalled_RestoresDefaultStatus()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        app.SetDefaultStatusForTests("gh not found — search and preview disabled; press 'd' for Doctor");

        app.FocusSearchFromInstalledForTests();

        Assert.Equal(app.DefaultStatusForTests, app.StatusTextForTests);
    }

    [Fact]
    public void BuildUi_HidesDefaultDiscoverFilterSummary()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        Assert.DoesNotContain(
            Descendants(window).OfType<Label>(),
            label => label.Text.ToString().Contains("Filters:", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildUi_HidesAdvancedDiscoverFiltersBehindSummary()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        Assert.NotNull(app.AgentFieldForTests);
        Assert.DoesNotContain(
            Descendants(window).OfType<Label>(),
            label => string.Equals(label.Text.ToString(), "Agent:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LeavingDiscover_CancelsItsWorkspaceLifetime(int destinationValue)
    {
        var destination = (SkillViewTab)destinationValue;
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        var discoverLifetime = app.DiscoverLifetimeForTests;

        app.ActivateTabForTests(destination);

        Assert.True(discoverLifetime.IsCancellationRequested);
        Assert.Equal(destination, app.ActiveTabForTests);
    }

    [Fact]
    public void LeavingDoctor_CancelsItsWorkspaceLifetime_AndReactivatesDiscover()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        var firstDiscoverLifetime = app.DiscoverLifetimeForTests;
        app.EnterDoctorForTests(CreateEnvironmentReport());
        var doctorLifetime = app.DoctorLifetimeForTests;

        Assert.True(firstDiscoverLifetime.IsCancellationRequested);
        Assert.False(doctorLifetime.IsCancellationRequested);

        app.LeaveDoctorForTests();

        Assert.True(doctorLifetime.IsCancellationRequested);
        Assert.Equal(SkillViewTab.Discover, app.ActiveTabForTests);
        Assert.False(app.DiscoverLifetimeForTests.IsCancellationRequested);
    }

    [Fact]
    public void VisibleLogQueue_IsBoundedByTotalCharactersWhileHidden()
    {
        var logger = new Logger(LogLevel.Debug);
        var app = new SkillViewApp(
            TuiServices.Build(logger),
            CreateOptions(),
            static () => Application.Create().Init(),
            probeOnRun: false);
        using var window = app.BuildUiForTests();

        for (var index = 0; index < 40; index++)
        {
            logger.Info("large", $"entry-{index}-" + new string('x', 20_000));
        }

        Assert.InRange(app.VisibleLogCharacterCountForTests, 1, 256 * 1024);
    }

    [Fact]
    public void BuildUi_SeedsDiscoverDetailFeedback()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        Assert.Contains(
            Descendants(window).OfType<Label>(),
            label => label.Text.ToString().Contains("[f] Filters", StringComparison.Ordinal)
                && label.Text.ToString().Contains("[?] Help", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Descendants(window).OfType<Label>(),
            label => label.Text.ToString().Contains("Remote preview", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildUi_DiscoverFooterAvoidsRepeatingDetailActions()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        var hints = app.CurrentHintsForTests;

        Assert.Equal(4, hints.Count);
        Assert.Contains(hints, hint => hint.Key == "f" && hint.Label == "Filters");
        Assert.Contains(hints, hint => hint.Key == "1/2/3" && hint.Label == "Tabs");
        Assert.Contains(hints, hint => hint.Key == "?" && hint.Label == "Help");
        Assert.Contains(hints, hint => hint.Key == "Ctrl+Q" && hint.Label == "Quit");
        Assert.DoesNotContain(hints, hint => hint.Key == "/" && hint.Label == "Search");
        Assert.DoesNotContain(hints, hint => hint.Key == "Enter" && hint.Label == "Preview");
        Assert.DoesNotContain(hints, hint => hint.Key == "i" && hint.Label == "Install");
        Assert.DoesNotContain(hints, hint => hint.Key == "o" && hint.Label == "Open");
        Assert.DoesNotContain(hints, hint => hint.Key == "q" && hint.Label == "Quit");
    }

    [Fact]
    public void BuildUi_InstalledHints_ShowOnlyPrimaryActions()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        app.ForceActiveTabForTests(SkillViewTab.Installed);
        var hints = app.CurrentHintsForTests;

        Assert.Equal(7, hints.Count);
        Assert.Contains(hints, hint => hint.Key == "f" && hint.Label == "Filter");
        Assert.Contains(hints, hint => hint.Key == "s" && hint.Label == "Sort");
        Assert.Contains(hints, hint => hint.Key == "P" && hint.Label == "Pins");
        Assert.Contains(hints, hint => hint.Key == "G" && hint.Label == "Scope");
        Assert.Contains(hints, hint => hint.Key == "x" && hint.Label == "Remove");
        Assert.Contains(hints, hint => hint.Key == "?" && hint.Label == "Help");
        Assert.Contains(hints, hint => hint.Key == "Ctrl+Q" && hint.Label == "Quit");
        Assert.DoesNotContain(hints, hint => hint.Key == "/" && hint.Label == "Search");
        Assert.DoesNotContain(hints, hint => hint.Key == "o" && hint.Label == "Open");
        Assert.DoesNotContain(hints, hint => hint.Key == "q" && hint.Label == "Quit");
    }

    [Fact]
    public void BuildUi_ShowsContextualWorkspaceTitle()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        Assert.True(app.ContextBarForTests!.Visible);
        Assert.Contains("Discover skills", ContextBarView.FormatForTests(
            app.ContextBarForTests.CurrentStateForTests));
    }

    [Fact]
    public void DiscoverSelectionChange_ClearsPreviewFailureAndRestoresSelectionPlaceholder()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        app.LoadSearchResultsForTests(
        [
            new SearchResultSkill("Claude Web Server LLM", null, null, "diegosouzapw/awesome-omni-skill", "claude-web-server-llm", null),
            new SearchResultSkill("Web multi search", null, null, "MARUCIE/openclaw-foundry", "web-multi-search", null),
        ]);
        app.SetPreviewTextForTests("(preview failed)\n\nboom");

        app.ResultsTableForTests!.SetSelectedRow(1);

        Assert.DoesNotContain("preview failed", app.PreviewTextForTests, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Selected: MARUCIE/openclaw-foundry/web-multi-search", app.PreviewTextForTests);
    }

    [Fact]
    public void SearchResultCommit_CancelsPreviewStartedAgainstSupersededTable()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        using var preview = app.BeginPreviewRequestForTests();

        app.LoadSearchResultsForTests(
        [
            new SearchResultSkill(null, null, null, "owner/repo", "new-result", null),
        ]);

        Assert.True(preview.Token.IsCancellationRequested);
        Assert.False(preview.IsCurrent);
    }

    [Fact]
    public void DiscoverSelectionChange_ClearsLoadedPreviewAndRestoresSelectionPlaceholder()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        app.LoadSearchResultsForTests(
        [
            new SearchResultSkill("Claude Web Server LLM", null, null, "diegosouzapw/awesome-omni-skill", "claude-web-server-llm", null),
            new SearchResultSkill("Web multi search", null, null, "MARUCIE/openclaw-foundry", "web-multi-search", null),
        ]);
        app.SetPreviewTextForTests("## Preview\n\nLoaded markdown for the first skill.");

        app.ResultsTableForTests!.SetSelectedRow(1);

        Assert.DoesNotContain("Loaded markdown for the first skill.", app.PreviewTextForTests, StringComparison.Ordinal);
        Assert.Contains("Selected: MARUCIE/openclaw-foundry/web-multi-search", app.PreviewTextForTests);
    }

    [Fact]
    public void SetPreviewText_PreservesTightMarkdownLists()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        app.LoadSearchResultsForTests(
        [
            new SearchResultSkill(null, null, null, "owner/repo", "demo", null),
        ]);
        app.SetPreviewTextForTests("- alpha\n- beta");

        Assert.Contains("- alpha\n- beta", app.PreviewTextForTests, StringComparison.Ordinal);
        Assert.DoesNotContain("- alpha\n\n- beta", app.PreviewTextForTests, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSearchMetadata_KeepsSummaryCompactByOmittingDescriptionParagraph()
    {
        var rendered = SkillViewApp.RenderSearchMetadata(
            new SearchResultSkill(
                Description: "FastAPI server for local Claude-powered search workflows.",
                Namespace: null,
                Path: null,
                Repo: "owner/repo",
                SkillName: "web-search",
                Stars: 42),
            LoggedInAuth());

        Assert.Contains("- **Name:** web-search", rendered);
        Assert.Contains("- **Repo:** [owner/repo]", rendered);
        Assert.DoesNotContain("FastAPI server for local Claude-powered search workflows.", rendered);
        Assert.DoesNotContain("**About:**", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDiscoverPreviewBody_PlacesDescriptionAndPlaceholderInOneScrollableBody()
    {
        var rendered = SkillViewApp.BuildDiscoverPreviewBodyForTests(
            description: "FastAPI server for local Claude-powered search workflows.",
            previewText: "Selected: owner/repo/web-search\n\nSelect a skill to preview.",
            includeDescription: true);

        Assert.Contains("FastAPI server for local Claude-powered search workflows.", rendered);
        Assert.Contains("Selected: owner/repo/web-search", rendered);
        Assert.Contains("---", rendered);
    }

    [Fact]
    public void BuildDiscoverPreviewBody_DoesNotRepeatDescriptionAfterPreviewLoads()
    {
        var rendered = SkillViewApp.BuildDiscoverPreviewBodyForTests(
            description: "FastAPI server for local Claude-powered search workflows.",
            previewText: "## Preview\n\nLoaded markdown body.",
            includeDescription: false);

        Assert.DoesNotContain("FastAPI server for local Claude-powered search workflows.", rendered);
        Assert.Equal("## Preview\n\nLoaded markdown body.", rendered);
    }

    [Fact]
    public void BuildUi_WindowShortcutFocusesQueryField()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        app.ForceActiveTabForTests(SkillViewTab.Installed);
        _ = window.NewKeyDownEvent(new Key('/'));

        Assert.Equal(SkillViewTab.Discover, app.ActiveTabForTests);
        Assert.True(app.QueryFieldForTests!.HasFocus);
    }

    [Fact]
    public void BuildUi_WindowArrowShortcutCyclesTabsEvenWhenQueryFieldHasFocus()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        _ = app.QueryFieldForTests!.SetFocus();
        _ = window.NewKeyDownEvent(new Key(KeyCode.CursorRight));

        Assert.Equal(SkillViewTab.Installed, app.ActiveTabForTests);
    }

    [Fact]
    public void BuildUi_WindowPrintableShortcutStaysInQueryFieldWhenQueryHasFocus()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        _ = app.QueryFieldForTests!.SetFocus();
        _ = window.NewKeyDownEvent(new Key('u'));

        Assert.Equal(SkillViewTab.Discover, app.ActiveTabForTests);
        Assert.True(app.QueryFieldForTests.HasFocus);
    }

    [Fact]
    public void BuildUi_ReturningToDiscover_RestoresLastFocusedControl()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        _ = app.ResultsTableForTests!.SetFocus();
        app.ForceActiveTabForTests(SkillViewTab.Installed);
        _ = window.NewKeyDownEvent(new Key('1'));

        Assert.True(app.ResultsTableForTests.HasFocus);
        Assert.False(app.QueryFieldForTests!.HasFocus);
    }

    [Fact]
    public void BuildRepoUrl_UsesGitHubCom_WhenAuthMissing()
    {
        var url = SkillViewApp.BuildRepoUrl(null, "owner/repo");

        Assert.Equal("https://github.com/owner/repo", url);
    }

    [Fact]
    public void BuildRepoUrl_UsesActiveHost_WhenAvailable()
    {
        var url = SkillViewApp.BuildRepoUrl(LoggedInAuth("ghe.example.com"), "owner/repo");

        Assert.Equal("https://ghe.example.com/owner/repo", url);
    }

    [Fact]
    public void BuildRepoUrl_FallsBackToGitHubCom_WhenLoggedOut()
    {
        var url = SkillViewApp.BuildRepoUrl(LoggedOutAuth("ghe.example.com"), "owner/repo");

        Assert.Equal("https://github.com/owner/repo", url);
    }

    [Fact]
    public void RenderSearchMetadata_UsesActiveHost_ForRepoUrl()
    {
        var metadata = SkillViewApp.RenderSearchMetadata(
            new SearchResultSkill(
                Description: "desc",
                Namespace: "ns",
                Path: "/skills/repo",
                Repo: "owner/repo",
                SkillName: "demo",
                Stars: 42),
            LoggedInAuth("ghe.example.com"));

        Assert.Contains("- **Name:** demo", metadata);
        Assert.Contains("- **Repo:** [owner/repo](https://ghe.example.com/owner/repo)", metadata);
        Assert.Contains("- **Stars:** ★ 42", metadata);
        Assert.DoesNotContain("desc", metadata);
        Assert.DoesNotContain("**About:**", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("https://github.com/owner/repo", metadata);
        Assert.DoesNotContain("**URL**", metadata);
    }

    [Fact]
    public void BuildUi_DiscoverContextBar_DoesNotRepeatFilterSummary()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        var contextBar = app.ContextBarForTests!.CurrentStateForTests;

        Assert.Equal("Discover skills", contextBar.Workspace);
        Assert.Null(contextBar.FilterLabel);
    }

    [Theory]
    [InlineData(".github/skills/demo", true)]
    [InlineData("skills/demo", false)]
    public void ShouldAllowHiddenDirPreview_DetectsHiddenPathSegments(string? path, bool expected)
    {
        var result = SkillViewApp.ShouldAllowHiddenDirPreview(
            new SearchResultSkill(
                Description: null,
                Namespace: "ns",
                Path: path,
                Repo: "owner/repo",
                SkillName: "demo",
                Stars: null));

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("skills/demo", false, false)]
    [InlineData("skills/demo", true, true)]
    [InlineData(".github/skills/demo", false, true)]
    public void ShouldAllowHiddenDirs_UsesToggleOrHiddenPath(string? path, bool userEnabled, bool expected)
    {
        var result = SkillViewApp.ShouldAllowHiddenDirs(
            new SearchResultSkill(
                Description: null,
                Namespace: "ns",
                Path: path,
                Repo: "owner/repo",
                SkillName: "demo",
                Stars: null),
            userEnabled);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Run_DisposesCreatedApplication()
    {
        IApplication? created = null;
        IApplication? disposed = null;

        void HandleCreated(object? _, EventArgs<IApplication> e)
        {
            created = e.Value;
            e.Value.StopAfterFirstIteration = true;
        }

        void HandleDisposed(object? _, EventArgs<IApplication> e)
        {
            disposed = e.Value;
        }

        Application.InstanceCreated += HandleCreated;
        Application.InstanceDisposed += HandleDisposed;
        try
        {
            var app = CreateApp(probeOnRun: false);

            var exitCode = app.Run();

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.NotNull(created);
            Assert.Same(created, disposed);
        }
        finally
        {
            Application.InstanceCreated -= HandleCreated;
            Application.InstanceDisposed -= HandleDisposed;
        }
    }

    [Fact]
    public async Task RunAsync_DisposesCreatedApplication()
    {
        IApplication? created = null;
        IApplication? disposed = null;

        void HandleCreated(object? _, EventArgs<IApplication> e)
        {
            created = e.Value;
            e.Value.StopAfterFirstIteration = true;
        }

        void HandleDisposed(object? _, EventArgs<IApplication> e)
        {
            disposed = e.Value;
        }

        Application.InstanceCreated += HandleCreated;
        Application.InstanceDisposed += HandleDisposed;
        try
        {
            var app = CreateApp(probeOnRun: false);

            var exitCode = await app.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.NotNull(created);
            Assert.Same(created, disposed);
        }
        finally
        {
            Application.InstanceCreated -= HandleCreated;
            Application.InstanceDisposed -= HandleDisposed;
        }
    }

    [Fact]
    public async Task RunAsync_WaitsForOwnedBackgroundWorkBeforeReturning()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workFinished = false;
        SkillViewApp? subject = null;

        subject = new SkillViewApp(
            TuiServices.Build(new Logger(LogLevel.Debug)),
            CreateOptions(),
            () =>
            {
                subject!.RunBackgroundForTests(async cancellationToken =>
                {
                    started.SetResult();
                    try
                    {
                        await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        cancellationObserved.SetResult();
                        await release.Task.WaitAsync(TestContext.Current.CancellationToken);
                        workFinished = true;
                    }
                }, "held-work");

                var application = Application.Create().Init();
                application.StopAfterFirstIteration = true;
                return application;
            },
            probeOnRun: false);

        var run = subject.RunAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(run.IsCompleted);
        Assert.False(workFinished);

        release.SetResult();
        Assert.Equal(ExitCodes.Success, await run.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(workFinished);
    }

    [Fact]
    public async Task Invoke_DoesNotUseDirectFallbackAfterRunHasEnded()
    {
        var app = new SkillViewApp(
            TuiServices.Build(new Logger(LogLevel.Debug)),
            CreateOptions(),
            static () =>
            {
                var application = Application.Create().Init();
                application.StopAfterFirstIteration = true;
                return application;
            },
            probeOnRun: false);
        var original = app.DefaultStatusForTests;

        await app.RunAsync(TestContext.Current.CancellationToken);
        app.SetDefaultStatusForTests("late callback must be ignored");

        Assert.Equal(original, app.DefaultStatusForTests);
    }

    [Fact]
    public void Run_RegistersCustomScheme_BeforeCreatingApplication()
    {
        // SkillViewApp.RunAsync applies MEC config (TuiConfigurationBuilder)
        // and then WingetTuiTheme.Register before ever creating the
        // Application instance. "Base" is a built-in Terminal.Gui scheme name
        // (always present, can't be removed) that WingetTuiTheme.Register
        // overwrites with its own colors — so the observable signal that
        // Register already ran in time is the *content* of the scheme, not
        // merely its presence.
        var wingetTuiForeground = SkillView.Ui.Theming.WingetTuiTheme.TextPrimary;
        var registeredAtFactory = false;

        var services = TuiServices.Build(new Logger(LogLevel.Debug));
        var options = new AppOptions(
            InvocationMode.Standalone,
            DispatchMode.Tui,
            Debug: false,
            Theme: AppTheme.Default,
            ScanRoots: Array.Empty<string>(),
            SubcommandName: null,
            SubcommandArgs: Array.Empty<string>());

        var app = new SkillViewApp(services, options, () =>
        {
            registeredAtFactory = SchemeManager.TryGetScheme(SkillView.Ui.Theming.SchemeNames.Base, out var scheme)
                && scheme.Normal.Foreground == wingetTuiForeground;
            var created = Application.Create();
            created.Init("ansi");
            created.StopAfterFirstIteration = true;
            return created;
        }, probeOnRun: false);

        var exitCode = app.Run();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(registeredAtFactory);
    }

    [Fact]
    public void BuildUi_DefaultsToDiscoverTab()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        Assert.Equal(SkillViewTab.Discover, app.ActiveTabForTests);
    }

    [Fact]
    public void BuildUi_ExposesChangesTabHook()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        Assert.NotNull(app.ChangesTabForTests);
    }

    [Fact]
    public void BuildUi_TabBarUsesWorkflowFirstLabels()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        var tabBar = app.TabBarForTests;
        Assert.NotNull(tabBar);
        var labels = tabBar!.TabLabelsForTests;
        Assert.Contains("Discover", labels);
        Assert.Contains("Installed", labels);
        Assert.Contains("Changes", labels);
        Assert.DoesNotContain("Search", labels);
        Assert.DoesNotContain("Updates", labels);
    }

    [Fact]
    public void ContextBar_IsCreatedDuringBuildUi()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        var contextBar = app.ContextBarForTests;
        Assert.NotNull(contextBar);
    }

    [Fact]
    public void ContextBar_UpdatesWhenTabChanges()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        // Initially on Discover tab
        Assert.Equal(SkillViewTab.Discover, app.ActiveTabForTests);

        // Switch to Installed tab should update context bar
        app.ForceActiveTabForTests(SkillViewTab.Installed);
        Assert.Equal(SkillViewTab.Installed, app.ActiveTabForTests);

        // Switch back to Discover should also update
        app.ForceActiveTabForTests(SkillViewTab.Discover);
        Assert.Equal(SkillViewTab.Discover, app.ActiveTabForTests);
    }

    [Fact]
    public void InstalledSelectionChange_KeepsShellChromeFocusedOnFiltersInsteadOfRepeatingDetailMetadata()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        app.InstalledTabForTests!.LoadSeeded(SnapshotWithInstalledSkills());
        app.ForceActiveTabForTests(SkillViewTab.Installed);

        app.InstalledTabForTests.SetSelectedRowForTests(1);

        var contextBar = app.ContextBarForTests!.CurrentStateForTests;
        Assert.Null(contextBar.LocationLabel);
        Assert.Null(contextBar.ProvenanceLabel);
        Assert.Null(contextBar.HealthLabel);
        Assert.Equal(
            $"Agents {InstalledInventoryFormatter.DescribeAgents(app.InstalledTabForTests.GetSelectedSkill()!)}",
            app.StatusStripForTests!.LeftBadgesForTests);
    }

    [Fact]
    public void ContextBar_FormatIncludesWorkspaceTitle()
    {
        var state = new ContextBarState(
            Workspace: "Discover",
            AgentLabel: null,
            LocationLabel: null,
            ProvenanceLabel: null,
            HealthLabel: null,
            FilterLabel: null);
        var rendered = ContextBarView.FormatForTests(state);

        Assert.Equal("Discover", rendered);
    }

    [Fact]
    public void ContextBar_FormatIncludesAgentLabelWhenPresent()
    {
        var state = new ContextBarState(
            Workspace: "Discover",
            AgentLabel: "agent: copilot",
            LocationLabel: null,
            ProvenanceLabel: null,
            HealthLabel: null,
            FilterLabel: null);

        var rendered = ContextBarView.FormatForTests(state);

        Assert.NotEmpty(rendered);
        Assert.Contains("agent: copilot", rendered);
    }

    [Fact]
    public void ContextBar_FormatIncludesFilterLabelWhenPresent()
    {
        var state = new ContextBarState(
            Workspace: "Discover",
            AgentLabel: null,
            LocationLabel: null,
            ProvenanceLabel: null,
            HealthLabel: null,
            FilterLabel: "Filters: all owners · any agent · limit 30 · hidden dirs on");

        var rendered = ContextBarView.FormatForTests(state);

        Assert.NotEmpty(rendered);
        Assert.Contains("Filters:", rendered);
        Assert.Contains("hidden dirs on", rendered);
    }

    [Fact]
    public void CtrlQ_IsAnUnconditionalQuitKey()
    {
        Assert.True(SkillViewApp.IsUnconditionalQuitKey(
            new Key(KeyCode.Q | KeyCode.CtrlMask)));
        Assert.False(SkillViewApp.IsUnconditionalQuitKey(new Key('q')));
    }
}
