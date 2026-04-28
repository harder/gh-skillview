using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class SkillViewAppTests
{
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
}
