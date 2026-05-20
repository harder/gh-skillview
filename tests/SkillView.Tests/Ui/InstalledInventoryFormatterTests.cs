using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledInventoryFormatterTests
{
    [Fact]
    public void DescribeLocation_UserScope_ReturnsUser()
    {
        var skill = MakeSkill(scope: Scope.User, path: "/home/user/.config/github-copilot/extensions/skills/my-skill");

        Assert.Equal("User", InstalledInventoryFormatter.DescribeLocation(skill));
    }

    [Fact]
    public void DescribeLocation_ProjectScope_ReturnsProject()
    {
        var skill = MakeSkill(scope: Scope.Project, path: "/repo/.github/copilot/extensions/skills/my-skill");

        Assert.Equal("Project", InstalledInventoryFormatter.DescribeLocation(skill));
    }

    [Fact]
    public void DescribeLocation_CustomScope_ReturnsCustom()
    {
        var skill = MakeSkill(scope: Scope.Custom, path: "/custom/skills/my-skill");

        Assert.Equal("Custom", InstalledInventoryFormatter.DescribeLocation(skill));
    }

    [Fact]
    public void DescribeAgents_NoAgents_ReturnsDash()
    {
        var skill = MakeSkill(agents: ImmutableArray<AgentMembership>.Empty);

        Assert.Equal("—", InstalledInventoryFormatter.DescribeAgents(skill));
    }

    [Fact]
    public void DescribeAgents_SingleAgent_ReturnsBadge()
    {
        var skill = MakeSkill(agents: ImmutableArray.Create(new AgentMembership("copilot", "/skills/my-skill", false)));

        var result = InstalledInventoryFormatter.DescribeAgents(skill);

        Assert.False(string.IsNullOrEmpty(result));
        Assert.DoesNotContain("—", result);
    }

    [Fact]
    public void DescribeAgents_DuplicateAgents_DeduplicatesBadges()
    {
        var skill = MakeSkill(agents: ImmutableArray.Create(
            new AgentMembership("copilot", "/skills/my-skill", false),
            new AgentMembership("copilot", "/other/path", true)));

        var single = MakeSkill(agents: ImmutableArray.Create(new AgentMembership("copilot", "/skills/my-skill", false)));

        Assert.Equal(
            InstalledInventoryFormatter.DescribeAgents(single),
            InstalledInventoryFormatter.DescribeAgents(skill));
    }

    private static InstalledSkill MakeSkill(
        Scope scope = Scope.User,
        string path = "/skills/my-skill",
        ImmutableArray<AgentMembership>? agents = null) => new()
    {
        Name = "my-skill",
        ResolvedPath = path,
        ScanRoot = "/skills",
        Scope = scope,
        Agents = agents ?? ImmutableArray<AgentMembership>.Empty,
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null,
    };
}
