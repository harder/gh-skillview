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
