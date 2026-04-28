using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class SkillViewAppTests
{
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
}
