using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Threading;
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

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class SkillViewAppTests
{
    [Fact]
    public void PagingUsesSubmittedQueryFiltersAndPreservesReturnContext()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        var seen = new List<(string Query, string? Owner, int Page)>();
        app.SetSearchAdapterForTests((query, options, _) =>
        {
            seen.Add((query, options.Owner, options.Page));
            return Task.FromResult(new SearchResponse(options.Page == 3 ? [] : [new(null, null, "skills/demo", "owner/repo", "page" + options.Page, 1)], 0, null));
        });
        app.QueryFieldForTests!.Text = "submitted";
        app.OwnerFieldForTests!.Text = "original-owner";
        app.SubmitSearchForTests();
        app.QueryFieldForTests.Text = "unsubmitted";
        app.OwnerFieldForTests.Text = "other-owner";
        var tab = app.DiscoverTabForTests!;
        tab.NextPage.InvokeCommand(Command.Accept);
        Assert.Equal(("submitted", "original-owner", 2), seen[^1]);
        Assert.Equal("page2", app.ResultsTableForTests!.Table![0, 1]);
        app.StopBackgroundAdmissionForTests();
        var row = app.ResultsTableForTests!.GetSelectedRow();
        var offset = app.ResultsTableForTests!.RowOffset;
        app.ActivateTabForTests(SkillViewTab.Installed);
        app.ActivateTabForTests(SkillViewTab.Discover);
        Assert.Equal("unsubmitted", app.QueryFieldForTests.Text);
        Assert.Contains("Page 2", tab.PageStatus.Text);
        Assert.Equal(row, app.ResultsTableForTests.GetSelectedRow());
        Assert.Equal(offset, app.ResultsTableForTests.RowOffset);
        Assert.Equal(2, seen.Count);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("failed")]
    [InlineData("canceled")]
    public async Task NewPageClearsTargetsAndPreviousRecoversUsingSubmittedState(string outcome)
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        var pending = new TaskCompletionSource<SearchResponse>();
        app.SetSearchAdapterForTests((_, options, _) => options.Page == 1
            ? Task.FromResult(new SearchResponse([new(null, null, "skills/demo", "owner/repo", "first", 1)], 0, null)) : pending.Task);
        app.QueryFieldForTests!.Text = "submitted";
        app.SubmitSearchForTests();
        app.QueryFieldForTests.Text = "unsubmitted";
        var tab = app.DiscoverTabForTests!;
        tab.NextPage.InvokeCommand(Command.Accept);
        Assert.Equal(0, app.ResultsTableForTests!.Table!.Rows);
        Assert.DoesNotContain("Install", app.DiscoverActionsForTests);
        if (outcome == "canceled")
        {
            app.ActivateTabForTests(SkillViewTab.Installed);
            app.ActivateTabForTests(SkillViewTab.Discover);
        }
        pending.SetResult(new([], outcome == "failed" ? 1 : 0, outcome == "failed" ? "page failure" : null));
        // Completion runs inline on the editor-owning thread in this direct-dispatch fixture.
        await Task.CompletedTask;
        Assert.True(tab.PreviousPage.Enabled);
        if (outcome == "empty") Assert.False(tab.NextPage.Enabled);
        else Assert.Contains(outcome == "failed" ? "Search failed" : "Search canceled", app.PreviewTextForTests);
        app.CaptureStatusAutoClearForTests()();
        tab.PreviousPage.InvokeCommand(Command.Accept);
        Assert.Equal(1, app.ResultsTableForTests.Table!.Rows);
        Assert.Contains("Page 1", tab.PageStatus.Text);
        Assert.Equal("first", app.ResultsTableForTests.Table![0, 1]);
        Assert.Equal("unsubmitted", app.QueryFieldForTests.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDifferentQueryKeepsEmptyFailureStateAfterStatusExpires(bool throws)
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        var previous = new SearchResultSkill("Previous", "description", null, "owner/repo", "old", null);
        app.LoadSearchResultsForTests([previous]);
        app.QueryFieldForTests!.Text = "different query";
        // BuildUiForTests dispatches directly: complete on the editor-owning thread.
        var pending = new TaskCompletionSource<SearchResponse>();
        var search = app.RunSearchForTestsAsync("different query", _ => pending.Task);
        Assert.False(app.ResultsTableForTests!.Visible);
        Assert.Equal(0, app.ResultsTableForTests.Table!.Rows);
        Assert.DoesNotContain("Install", app.DiscoverActionsForTests);
        if (throws) pending.SetException(new IOException("search-failed-sentinel"));
        else pending.SetResult(new([], 1, "search-failed-sentinel"));
        await search;
        Assert.True(app.ResultsTableForTests.Visible);
        Assert.Equal(0, app.ResultsTableForTests.Table!.Rows);
        Assert.DoesNotContain("Install", app.DiscoverActionsForTests);
        Assert.Contains("Search failed", app.PreviewTextForTests);
        Assert.Contains("search-failed-sentinel", app.StatusTextForTests);
        Assert.False(app.StatusStripForTests!.IsBusyForTests);
        app.CaptureStatusAutoClearForTests()();
        Assert.Equal(app.DefaultStatusForTests, app.StatusTextForTests);
        Assert.Contains("Search failed", app.PreviewTextForTests);
        Assert.Equal("different query", app.QueryFieldForTests.Text);
        Assert.Equal(0, app.ResultsTableForTests.Table.Rows);
        Assert.DoesNotContain("owner/repo", app.DiscoverTabForTests!.DetailPane.MetadataPane.Text);
        app.StopBackgroundAdmissionForTests();
        app.ActivateTabForTests(SkillViewTab.Installed);
        app.ActivateTabForTests(SkillViewTab.Discover);
        Assert.Contains("Search failed", app.PreviewTextForTests);
        Assert.DoesNotContain("Install", app.DiscoverActionsForTests);
        app.ResultsTableForTests.SetFocus();
        app.ResultsTableForTests.NewKeyDownEvent(new Key('i'));
        Assert.Equal("no results to install", app.StatusTextForTests);
        app.ResultsTableForTests.NewKeyDownEvent(new Key('/'));
        Assert.True(app.QueryFieldForTests.HasFocus);
        app.QueryFieldForTests.NewKeyDownEvent(new Key('q'));
        Assert.Equal("q", app.QueryFieldForTests.Text);
        await app.RunSearchForTestsAsync("retry", _ => Task.FromResult(new SearchResponse([previous], 0, null)));
        Assert.Equal(1, app.ResultsTableForTests.Table.Rows);
        Assert.Contains("Install", app.DiscoverActionsForTests);
        Assert.DoesNotContain("Search failed", app.PreviewTextForTests);
    }

    [Fact]
    public async Task EmptySuccessfulSearchReplacesPreviousResultsAndActions()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        app.LoadSearchResultsForTests([new("Previous", null, null, "owner/repo", "old", null)]);
        await app.RunSearchForTestsAsync("empty query", _ => Task.FromResult(new SearchResponse([], 0, null)));
        Assert.Equal(0, app.ResultsTableForTests!.Table!.Rows);
        Assert.DoesNotContain("Install", app.DiscoverActionsForTests);
        Assert.False(app.StatusStripForTests!.IsBusyForTests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededSearchCannotRestoreOldResultsOrEndNewerLoadingFeedback(bool fails)
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        app.LoadSearchResultsForTests([new("Previous", null, null, "owner/repo", "old", null)]);
        var old = new TaskCompletionSource<SearchResponse>();
        var current = new TaskCompletionSource<SearchResponse>();
        var first = app.RunSearchForTestsAsync("first", _ => old.Task);
        var second = app.RunSearchForTestsAsync("second", _ => current.Task);
        if (fails) old.SetException(new IOException("obsolete failure"));
        else old.SetResult(new([new("Obsolete", null, null, "owner/repo", "obsolete", null)], 0, null));
        await first;
        Assert.False(app.ResultsTableForTests!.Visible);
        Assert.True(app.StatusStripForTests!.IsBusyForTests);
        Assert.DoesNotContain("obsolete failure", app.StatusTextForTests);
        Assert.Equal(0, app.ResultsTableForTests.Table!.Rows);
        current.SetResult(new([new("Current", null, null, "owner/repo", "current", null)], 0, null));
        await second;
        Assert.True(app.ResultsTableForTests.Visible);
        Assert.Equal(1, app.ResultsTableForTests.Table!.Rows);
        Assert.Contains("Current", app.PreviewTextForTests);
        Assert.False(app.StatusStripForTests.IsBusyForTests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingPendingSearchKeepsCanceledStateOnReturnAndDropsLateCompletion(bool fails)
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        app.StopBackgroundAdmissionForTests();
        app.LoadSearchResultsForTests([new("Previous", null, null, "owner/repo", "old", null)]);
        var pending = new TaskCompletionSource<SearchResponse>();
        CancellationToken token = default;
        app.QueryFieldForTests!.Text = "pending query";
        var search = app.RunSearchForTestsAsync("pending query", ct => { token = ct; return pending.Task; });
        app.ActivateTabForTests(SkillViewTab.Installed);
        Assert.True(token.IsCancellationRequested);
        Assert.False(app.StatusStripForTests!.IsBusyForTests);
        Assert.DoesNotContain("searching", app.StatusTextForTests);
        app.SetStatusForTests("installed feedback", TuiHelpers.NotificationLevel.Info);
        app.ActivateTabForTests(SkillViewTab.Discover);
        if (fails) pending.SetException(new OperationCanceledException(token));
        else pending.SetResult(new([new("Obsolete", null, null, "owner/repo", "obsolete", null)], 0, null));
        await search;
        Assert.Equal("installed feedback", app.StatusTextForTests);
        Assert.Contains("Search canceled", app.PreviewTextForTests);
        Assert.Equal("pending query", app.QueryFieldForTests.Text);
        Assert.Equal(0, app.ResultsTableForTests!.Table!.Rows);
        Assert.DoesNotContain("Install", app.DiscoverActionsForTests);
        Assert.False(app.DiscoverTabForTests!.IsLoading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverOutcomeDoesNotFollowTabOrLetItsOldTimerEraseNewStatus(bool fails)
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        app.StopBackgroundAdmissionForTests();
        await app.RunSearchForTestsAsync("query", _ => Task.FromResult(fails
            ? new SearchResponse([], 1, "fixture failure")
            : new SearchResponse([new(null, null, null, "owner/repo", "skill", null)], 0, null)));
        var oldTimer = app.CaptureStatusAutoClearForTests();
        Assert.Contains(fails ? "search failed" : "1 result(s)", app.StatusTextForTests);
        app.ActivateTabForTests(SkillViewTab.Installed);
        Assert.Equal(app.DefaultStatusForTests, app.StatusTextForTests);
        app.SetStatusForTests("new tab feedback", TuiHelpers.NotificationLevel.Error);
        oldTimer();
        Assert.Equal("new tab feedback", app.StatusTextForTests);
        app.CaptureStatusAutoClearForTests()();
        Assert.Equal(app.DefaultStatusForTests, app.StatusTextForTests);
    }

    [Fact]
    public async Task LeavingDiscoverPreservesOtherBusyOwnerAndItsProgress()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        app.StopBackgroundAdmissionForTests();
        var other = app.BeginBusyOperationForTests("checking updates…");
        var pending = new TaskCompletionSource<SearchResponse>();
        var search = app.RunSearchForTestsAsync("query", _ => pending.Task);
        app.ActivateTabForTests(SkillViewTab.Installed);
        Assert.Equal("checking updates…", app.StatusTextForTests);
        Assert.True(app.StatusStripForTests!.IsBusyForTests);
        app.CaptureStatusAutoClearForTests()();
        Assert.Equal("checking updates…", app.StatusTextForTests);
        pending.SetResult(new([], 0, null));
        await search;
        Assert.True(app.StatusStripForTests.IsBusyForTests);
        Assert.Equal("checking updates…", app.StatusTextForTests);
        app.EndBusyOperationForTests(other);
        Assert.False(app.StatusStripForTests.IsBusyForTests);
    }

    [Fact]
    public async Task LeavingPendingDiscoverPreservesGlobalErrorAndItsTimer()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        app.StopBackgroundAdmissionForTests();
        var pending = new TaskCompletionSource<SearchResponse>();
        var search = app.RunSearchForTestsAsync("query", _ => pending.Task);
        app.SetStatusForTests("update failed", TuiHelpers.NotificationLevel.Error);
        var timer = app.CaptureStatusAutoClearForTests();
        app.ActivateTabForTests(SkillViewTab.Installed);
        pending.SetResult(new([], 0, null));
        await search;
        Assert.Equal("update failed", app.StatusTextForTests);
        Assert.False(app.StatusStripForTests!.IsBusyForTests);
        timer();
        Assert.Equal(app.DefaultStatusForTests, app.StatusTextForTests);
    }

    [Fact]
    public async Task SearchBusyOwnerIsReleasedEvenWhenLoadingCleanupThrows()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        var loading = Assert.IsType<TabLoadingView>(app.DiscoverTabForTests!.SubViews.Single(child => child is TabLoadingView));
        loading.VisibleChanged += (_, _) =>
        {
            if (!loading.Visible) throw new InvalidOperationException("cleanup-dispatch-sentinel");
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.RunSearchForTestsAsync("failed", _ =>
            Task.FromResult(new SearchResponse([], 1, "search-failed-sentinel"))));
        Assert.Equal("cleanup-dispatch-sentinel", exception.Message);
        Assert.False(app.StatusStripForTests!.IsBusyForTests);
    }

    [Theory]
    [InlineData((int)TuiHelpers.NotificationLevel.Success)]
    [InlineData((int)TuiHelpers.NotificationLevel.Error)]
    [InlineData((int)TuiHelpers.NotificationLevel.Info)]
    public void TabChangesPreserveOutcomeErrorAndOperationStatus(int level)
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        app.StopBackgroundAdmissionForTests();
        app.SetStatusForTests("retained-operation-status", (TuiHelpers.NotificationLevel)level);
        app.ActivateTabForTests(SkillViewTab.Installed);
        Assert.Equal("retained-operation-status", app.StatusTextForTests);
        app.ActivateTabForTests(SkillViewTab.Changes);
        Assert.Equal("retained-operation-status", app.StatusTextForTests);
        Assert.Contains(app.CurrentHintsForTests, hint => hint.Key == "c" && hint.Label == "Cleanup");
    }

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
        GhVersionRaw = "gh version 2.97.0",
        GhVersion = new SemVer(2, 97, 0),
        GhMeetsMinimum = true,
        Auth = GhAuthStatus.Unknown,
        GhSkillAvailable = true,
        LogDirectory = "/tmp/skillview-logs",
    };

    [Fact]
    public void InstallHandoffSeedsEmbeddedInstalledAndReturnsToUnchangedDiscover()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        app.QueryFieldForTests!.Text = "retained query";
        app.OwnerFieldForTests!.Text = "retained-owner";
        app.AgentFieldForTests!.Text = "retained-agent";
        app.LoadSearchResultsForTests(Enumerable.Range(0, 30).Select(i =>
            new SearchResultSkill("Description " + i, "ns", "skills/source", "owner/repo", "skill-" + i, i)).ToArray());
        app.ResultsTableForTests!.SetSelectedRow(20);
        app.ResultsTableForTests!.RowOffset = 15;
        app.SetPreviewTextForTests("retained preview with scroll context");
        var preview = app.PreviewTextForTests;
        var results = app.ResultsTableForTests.Table;
        var skill = new InstalledSkill
        {
            Name = "installed",
            ResolvedPath = Path.Combine(Path.GetTempPath(), "handoff-host", "materialized"),
            ScanRoot = Path.Combine(Path.GetTempPath(), "handoff-host"),
            Scope = Scope.User,
            Agents = [],
            FrontMatter = new() { Upstream = "https://github.com/owner/repo", GithubPath = "skills/source" },
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
        };
        var snapshot = InventorySnapshot.Empty with { Skills = [skill], CapturedAt = DateTimeOffset.UtcNow };
        var overview = InstalledOverview.Build(snapshot, TestContext.Current.CancellationToken);
        var group = Assert.Single(overview.Groups);
        app.RevealInstalledInstallForTests(new(snapshot, overview, group.Key, [Assert.Single(group.Copies).Key]));
        Assert.Equal(SkillViewTab.Installed, app.ActiveTabForTests);
        Assert.Same(snapshot, app.InstalledTabForTests!.CurrentSnapshot);
        Assert.False(app.InstalledTabForTests.LoadActiveForTests);
        Assert.Same(skill, app.InstalledTabForTests.GetSelectedSkill());
        app.ActivateTabForTests(SkillViewTab.Discover);
        Assert.Equal("retained query", app.QueryFieldForTests.Text);
        Assert.Equal("retained-owner", app.OwnerFieldForTests.Text);
        Assert.Equal("retained-agent", app.AgentFieldForTests.Text);
        Assert.Same(results, app.ResultsTableForTests.Table);
        Assert.Equal(20, app.ResultsTableForTests.GetSelectedRow());
        Assert.Equal(15, app.ResultsTableForTests.RowOffset);
        Assert.Equal(preview, app.PreviewTextForTests);
        Assert.False(app.DiscoverTabForTests!.IsLoading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedInstallRevealLoadsOnceWhileValidSeedDoesNotReload(bool valid)
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        var snapshot = SnapshotWithInstalledSkill();
        var overview = InstalledOverview.Build(snapshot, TestContext.Current.CancellationToken);
        var group = Assert.Single(overview.Groups);
        var inventory = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var populated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        SkillView.Ui.Tabs.InstalledTabView? tab = null;
        tab = new(action =>
        {
            action();
            if (ReferenceEquals(tab?.CurrentSnapshot, snapshot)) populated.TrySetResult();
            return Task.CompletedTask;
        }, (_, _) => { }, _ => { loads++; return inventory.Task; },
            (_, _, _) => throw new InvalidOperationException("No mutation from navigation"), () => { });
        window.Add(tab);
        app.SetInstalledTabForTests(tab);
        app.RevealInstalledInstallForTests(new(snapshot, overview, valid ? group.Key : "missing-group", [Assert.Single(group.Copies).Key]));
        Assert.Equal(SkillViewTab.Installed, app.ActiveTabForTests);
        Assert.Equal(valid ? 0 : 1, loads);
        if (!valid)
        {
            Assert.True(tab.LoadActiveForTests);
            inventory.SetResult(snapshot);
            await populated.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        Assert.Same(snapshot, tab.CurrentSnapshot);
        Assert.False(tab.LoadActiveForTests);
        app.ActivateTabForTests(SkillViewTab.Installed);
        Assert.Equal(valid ? 0 : 1, loads);
        Assert.True(tab.Visible);
        if (valid) Assert.True(tab.DetailOpenForTests);
        else Assert.True(tab.TableHasFocusForTests);
    }

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
    public async Task StartupInventoryRetainsWorkspaceAndNamedProfileOptions()
    {
        var temporary = Directory.CreateTempSubdirectory("skillview-startup-discovery-");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(temporary.FullName, "workspace")).FullName;
            var profile = Directory.CreateDirectory(Path.Combine(temporary.FullName, "profile")).FullName;
            var workspaceSkill = Directory.CreateDirectory(Path.Combine(workspace, ".claude", "skills", "workspace-demo")).FullName;
            var profileSkill = Directory.CreateDirectory(Path.Combine(profile, "profile-demo")).FullName;
            File.WriteAllText(Path.Combine(workspaceSkill, "SKILL.md"), "---\nname: workspace-demo\ndescription: Test\n---\nBody\n");
            File.WriteAllText(Path.Combine(profileSkill, "SKILL.md"), "---\nname: profile-demo\ndescription: Test\n---\nBody\n");
            var home = Directory.CreateDirectory(Path.Combine(temporary.FullName, "home")).FullName;
            var environment = new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
                ["PI_CODING_AGENT_DIR"] = Path.Combine(home, ".pi"),
                ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
                ["GIT_DIR"] = Path.Combine(home, ".git"),
                ["GIT_WORK_TREE"] = home,
                ["GIT_COMMON_DIR"] = Path.Combine(home, ".git"),
                ["GIT_CONFIG_COUNT"] = "0"
            };
            var services = TuiServices.Build(new Logger(), processEnvironment: environment, workingDirectory: home);
            var options = CreateOptions() with
            { WorkspaceDirectories = [workspace], Profiles = [new ScanRootResolver.AgentProfileRoot("claude", profile, "work")] };
            var app = new SkillViewApp(services, options);
            var snapshot = await app.CaptureStartupInventoryAsync(CreateEnvironmentReport() with { GhPath = null }, TestContext.Current.CancellationToken);
            Assert.Equal(["profile-demo", "workspace-demo"], snapshot.Skills.Select(skill => skill.Name));
            Assert.Equal("work", Assert.Single(snapshot.Skills[0].Agents).Profile);
            Assert.True(SkillViewApp.ShouldOpenInstalledOnStartup(snapshot));
        }
        finally { temporary.Delete(true); }
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
    public async Task RunAsync_ReturnsCancelled_WithoutCreatingApplication_WhenAlreadyCanceled()
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

        Assert.Equal(ExitCodes.Cancelled, exitCode);
        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task RunAsync_ReturnsCancelled_WhenCancellationWinsDuringApplicationCreation()
    {
        using var cancellation = new CancellationTokenSource();
        IApplication? created = null;
        IApplication? disposed = null;

        void HandleDisposed(object? sender, EventArgs<bool> e)
        {
            if (!e.Value && ReferenceEquals(sender, created)) disposed = (IApplication)sender!;
        }

        var app = new SkillViewApp(
            TuiServices.Build(new Logger(LogLevel.Debug)),
            CreateOptions(),
            () =>
            {
                created = Application.Create().Init();
                created.InitializedChanged += HandleDisposed;
                cancellation.Cancel();
                return created;
            },
            probeOnRun: false);

        try
        {
            var exitCode = await app.RunAsync(cancellation.Token);

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

    [Fact]
    public void FocusSearchFromInstalled_RestoresDefaultStatus()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        app.SetDefaultStatusForTests("gh not found — search and preview disabled; press 'd' for Doctor");

        app.FocusSearchFromInstalledForTests();

        Assert.Equal(app.DefaultStatusForTests, app.StatusTextForTests);
    }

    [Theory]
    [InlineData("query", KeyCode.Enter)]
    [InlineData("query", KeyCode.J | KeyCode.CtrlMask)]
    [InlineData("query", KeyCode.Esc)]
    [InlineData("owner", KeyCode.Enter)]
    [InlineData("owner", KeyCode.J | KeyCode.CtrlMask)]
    [InlineData("owner", KeyCode.Esc)]
    [InlineData("agent", KeyCode.Enter)]
    [InlineData("agent", KeyCode.J | KeyCode.CtrlMask)]
    [InlineData("agent", KeyCode.Esc)]
    [InlineData("limit", KeyCode.Enter)]
    [InlineData("limit", KeyCode.J | KeyCode.CtrlMask)]
    [InlineData("limit", KeyCode.Esc)]
    public void DiscoverSearchFields_ConsumeSubmitAndEscapeKeys(string field, KeyCode keyCode)
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        View input = field switch
        {
            "query" => app.QueryFieldForTests!,
            "owner" => app.OwnerFieldForTests!,
            "agent" => app.AgentFieldForTests!,
            "limit" => app.LimitUpDownForTests!,
            _ => throw new ArgumentException("Unknown search field", nameof(field)),
        };
        var key = new Key(keyCode);

        _ = input.NewKeyDownEvent(key);

        Assert.True(key.Handled);
        Assert.True(app.UserInteractedSinceLaunchForTests);
        Assert.Empty(app.QueryFieldForTests!.Text);
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
        using var registration = discoverLifetime.Register(() =>
            throw new InvalidOperationException("callback failed"));

        var exception = Record.Exception(() => app.ActivateTabForTests(destination));

        Assert.Null(exception);
        Assert.True(discoverLifetime.IsCancellationRequested);
        Assert.Equal(destination, app.ActiveTabForTests);
    }

    [Fact]
    public void CapturedWorkspaceToken_RemainsUsableAfterSourceDisposal()
    {
        var lifetime = new CancellationSource(TestContext.Current.CancellationToken);

        Assert.True(SkillViewApp.TryCaptureActiveLifetimeToken(lifetime, out var captured));
        lifetime.Dispose();

        Assert.False(captured.IsCancellationRequested);
        Assert.False(SkillViewApp.TryCaptureActiveLifetimeToken(lifetime, out var rejected));
        Assert.True(rejected.IsCancellationRequested);
    }

    [Fact]
    public void LeavingDoctor_CancelsItsWorkspaceLifetime_AndReactivatesDiscover()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        var firstDiscoverLifetime = app.DiscoverLifetimeForTests;
        app.EnterDoctorForTests(CreateEnvironmentReport());
        var doctorLifetime = app.DoctorLifetimeForTests;
        using var registration = doctorLifetime.Register(() =>
            throw new InvalidOperationException("callback failed"));

        Assert.True(firstDiscoverLifetime.IsCancellationRequested);
        Assert.False(doctorLifetime.IsCancellationRequested);

        var exception = Record.Exception(app.LeaveDoctorForTests);

        Assert.Null(exception);
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

        Assert.Equal(6, hints.Count);
        Assert.Contains(hints, hint => hint.Key == "/" && hint.Label == "Search");
        Assert.Contains(hints, hint => hint.Key == "Enter" && hint.Label == "Details/Expand");
        Assert.Contains(hints, hint => hint.Key == "r" && hint.Label == "Refresh");
        Assert.Contains(hints, hint => hint.Key == "c" && hint.Label == "Cleanup");
        Assert.Contains(hints, hint => hint.Key == "?" && hint.Label == "Help");
        Assert.Contains(hints, hint => hint.Key == "Ctrl+Q" && hint.Label == "Quit");
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
    public async Task AwaitDispatchAsync_KeepsRequestLeaseCurrentUntilQueuedCallbackRuns()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var gate = new LatestRequestGate();
        using var request = gate.Begin(lifetime.Token, TimeSpan.FromMinutes(1));
        Action? queued = null;
        var callbackObservedCurrentLease = false;

        var dispatch = SkillViewApp.AwaitDispatchAsync(
            callback => queued = callback,
            () => callbackObservedCurrentLease = request.IsCurrent,
            lifetime.Token);

        Assert.False(dispatch.IsCompleted);
        Assert.True(request.IsCurrent);
        Assert.NotNull(queued);

        queued();

        Assert.True(await dispatch);
        Assert.True(callbackObservedCurrentLease);
    }

    [Fact]
    public async Task AwaitDispatchAsync_CanceledLifetimeRejectsDelayedCallback()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Action? queued = null;
        var callbackRan = false;

        var dispatch = SkillViewApp.AwaitDispatchAsync(
            callback => queued = callback,
            () => callbackRan = true,
            lifetime.Token);
        Assert.NotNull(queued);

        lifetime.Cancel();

        Assert.False(await dispatch);
        queued();
        Assert.False(callbackRan);
    }

    [Fact]
    public async Task AwaitOwnedDispatchAsync_CanceledLifetimeRejectsQueuedCallback()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Action? queued = null;
        var callbackRan = false;

        var dispatch = SkillViewApp.AwaitOwnedDispatchAsync(
            callback => queued = callback,
            callback => callback(),
            () => callbackRan = true,
            lifetime.Token);
        Assert.NotNull(queued);

        lifetime.Cancel();

        Assert.False(await dispatch);
        queued();
        Assert.False(callbackRan);
    }

    [Fact]
    public async Task AwaitOwnedDispatchAsync_CancellationWaitsForStartedCallback()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Action? queued = null;
        var dispatch = SkillViewApp.AwaitOwnedDispatchAsync(
            callback => queued = callback,
            callback => callback(),
            () =>
            {
                entered.Set();
                release.Wait(TestContext.Current.CancellationToken);
            },
            lifetime.Token);
        Assert.NotNull(queued);
        var callback = Task.Factory.StartNew(
            queued,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.True(entered.Wait(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken));
            lifetime.Cancel();
            Assert.False(dispatch.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await callback;
        Assert.True(await dispatch);
    }

    [Fact]
    public async Task AwaitOwnedDispatchAsync_StagesModalAfterDispatchCallbackReturns()
    {
        Action? queued = null;
        Action? nextIteration = null;
        var callbackRan = false;

        var dispatch = SkillViewApp.AwaitOwnedDispatchAsync(
            callback => queued = callback,
            callback => nextIteration = callback,
            () => callbackRan = true,
            TestContext.Current.CancellationToken);

        Assert.NotNull(queued);
        queued();

        Assert.NotNull(nextIteration);
        Assert.False(callbackRan);
        Assert.False(dispatch.IsCompleted);

        nextIteration();

        Assert.True(await dispatch);
        Assert.True(callbackRan);
    }

    [Fact]
    public async Task AwaitOwnedDispatchAsync_ReleasesTimedEventsLockBeforeModalWork()
    {
        var timedEvents = new TimedEvents();
        Action? nextIteration = null;
        var workerQueuedProgress = false;

        var dispatch = SkillViewApp.AwaitOwnedDispatchAsync(
            callback => timedEvents.Add(
                TimeSpan.Zero,
                () =>
                {
                    callback();
                    return false;
                }),
            callback => nextIteration = callback,
            () =>
            {
                var worker = Task.Factory.StartNew(
                    () =>
                    {
                        timedEvents.Add(TimeSpan.Zero, () => false);
                        workerQueuedProgress = true;
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                Assert.True(worker.Wait(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
            },
            TestContext.Current.CancellationToken);

        timedEvents.RunTimers();

        Assert.NotNull(nextIteration);
        Assert.False(dispatch.IsCompleted);

        nextIteration();

        Assert.True(await dispatch);
        Assert.True(workerQueuedProgress);
    }

    [Fact]
    public async Task AwaitOwnedDispatchAsync_CancellationRejectsStagedModal()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Action? queued = null;
        Action? nextIteration = null;
        var callbackRan = false;

        var dispatch = SkillViewApp.AwaitOwnedDispatchAsync(
            callback => queued = callback,
            callback => nextIteration = callback,
            () => callbackRan = true,
            lifetime.Token);

        Assert.NotNull(queued);
        queued();
        Assert.NotNull(nextIteration);

        lifetime.Cancel();

        Assert.False(await dispatch);
        nextIteration();
        Assert.False(callbackRan);
    }

    [Fact]
    public void CancelingPreview_RestoresStillRunningSearchBusyState()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        var searchBusy = app.BeginBusyOperationForTests("searching retained-query…");
        using var preview = app.BeginPreviewRequestForTests();

        Assert.True(app.StatusStripForTests!.IsBusyForTests);
        Assert.Equal("preview test…", app.StatusTextForTests);

        app.ShowLogPaneForTests();

        Assert.True(app.StatusStripForTests.IsBusyForTests);
        Assert.Equal("searching retained-query…", app.StatusTextForTests);

        app.EndBusyOperationForTests(searchBusy);
        Assert.False(app.StatusStripForTests.IsBusyForTests);
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
    public void BuildUi_WindowShortcutFocusesLocalInstalledSearch()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        app.InstalledTabForTests!.LoadSeeded(InventorySnapshot.Empty);
        app.ForceActiveTabForTests(SkillViewTab.Installed);
        _ = window.NewKeyDownEvent(new Key('/'));

        Assert.Equal(SkillViewTab.Installed, app.ActiveTabForTests);
        Assert.True(app.InstalledTabForTests!.FilterHasFocus);
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
    public void BuildUi_UpdateShortcutWithNoSelectedCopyKeepsVisibleMainCheckAction()
    {
        var app = CreateApp();
        app.StopBackgroundAdmissionForTests(); // navigation must not probe this machine
        using var window = app.BuildUiForTests();
        app.ForceActiveTabForTests(SkillViewTab.Installed);
        _ = window.NewKeyDownEvent(new Key('u'));
        Assert.False(app.UpdatesWorkspaceVisibleForTests);
        Assert.Equal(SkillViewTab.Installed, app.ActiveTabForTests);
        Assert.False(app.ChangesTabForTests!.Visible);
    }

    [Fact]
    public void BuildUi_EscLeavesDiscoverQueryField()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();
        var input = app.QueryFieldForTests!;

        input.SetFocus();
        input.NewKeyDownEvent(new Key(KeyCode.Esc));

        Assert.True(app.ResultsTableForTests!.HasFocus);
        Assert.False(input.HasFocus);
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

        void HandleDisposed(object? sender, EventArgs<bool> e)
        {
            if (!e.Value && ReferenceEquals(sender, created)) disposed = (IApplication)sender!;
        }

        try
        {
            var app = new SkillViewApp(TuiServices.Build(new Logger(LogLevel.Debug)), CreateOptions(), () =>
            {
                created = Application.Create().Init();
                created.InitializedChanged += HandleDisposed;
                created.StopAfterFirstIteration = true;
                return created;
            }, probeOnRun: false);

            var exitCode = app.Run();

            Assert.Equal(ExitCodes.Success, exitCode);
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

    [Fact]
    public async Task RunAsync_DisposesCreatedApplication()
    {
        IApplication? created = null;
        IApplication? disposed = null;

        void HandleDisposed(object? sender, EventArgs<bool> e)
        {
            if (!e.Value && ReferenceEquals(sender, created)) disposed = (IApplication)sender!;
        }

        try
        {
            var app = new SkillViewApp(TuiServices.Build(new Logger(LogLevel.Debug)), CreateOptions(), () =>
            {
                created = Application.Create().Init();
                created.InitializedChanged += HandleDisposed;
                created.StopAfterFirstIteration = true;
                return created;
            }, probeOnRun: false);

            var exitCode = await app.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ExitCodes.Success, exitCode);
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
        Assert.Contains("Issues", labels);
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
    public void StatusStrip_UpdatesShortcutsWhenTabChanges()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        app.ForceActiveTabForTests(SkillViewTab.Installed);
        var installedHints = app.StatusStripForTests!.HintsForTests;
        Assert.Contains(installedHints, hint => hint is { Key: "/", Label: "Search" });
        Assert.Contains(installedHints, hint => hint is { Key: "Enter", Label: "Details/Expand" });

        app.ForceActiveTabForTests(SkillViewTab.Changes);
        Assert.Equal(0, app.ChangesTabForTests!.RowCountForTests);
        var changesHints = app.StatusStripForTests.HintsForTests;
        Assert.Contains(changesHints, hint => hint is { Key: "C", Label: "Check updates" });
        Assert.Contains(changesHints, hint => hint is { Key: "Enter", Label: "Review" });
        Assert.Contains(changesHints, hint => hint is { Key: "F2", Label: "Details" });
        Assert.Contains(changesHints, hint => hint is { Key: "c", Label: "Cleanup" });
        Assert.Contains(changesHints, hint => hint is { Key: "d", Label: "Doctor" });
        Assert.DoesNotContain(changesHints, hint => hint.Key is "f" or "x");
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
        Assert.Equal("", app.StatusStripForTests!.LeftBadgesForTests);
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
        Assert.True(SkillViewApp.IsUnconditionalQuitKey(
            new Key(KeyCode.Q | KeyCode.CtrlMask | KeyCode.AltMask)));
        Assert.False(SkillViewApp.IsUnconditionalQuitKey(new Key('q')));
    }
}
