using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
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

    private static SkillViewApp CreateApp()
    {
        var services = TuiServices.Build(new Logger(LogLevel.Debug));
        var options = new AppOptions(
            InvocationMode.Standalone,
            DispatchMode.Tui,
            Debug: false,
            ScanRoots: Array.Empty<string>(),
            SubcommandName: null,
            SubcommandArgs: Array.Empty<string>());

        return new SkillViewApp(services, options);
    }

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
    public void FocusSearchFromInstalled_RestoresDefaultStatus()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        app.SetDefaultStatusForTests("gh not found — search and preview disabled; press 'd' for Doctor");

        app.FocusSearchFromInstalledForTests();

        Assert.Equal(app.DefaultStatusForTests, app.StatusTextForTests);
    }

    [Fact]
    public void BuildUi_ExposesHiddenDirToggleOnSearchScreen()
    {
        var app = CreateApp();
        using var window = app.BuildUiForTests();

        Assert.Contains(
            Descendants(window).OfType<CheckBox>(),
            box => box.Text.ToString().Contains("hidden", StringComparison.OrdinalIgnoreCase));
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

        Assert.Contains("https://ghe.example.com/owner/repo", metadata);
        Assert.DoesNotContain("https://github.com/owner/repo", metadata);
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
}
