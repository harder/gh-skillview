using SkillView.Cli;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class CliDispatcherLocalReimportTests
{
    private static InstalledSkill Target(string path, params AgentMembership[] agents) => new()
    {
        Name = "same-name",
        ResolvedPath = path,
        ScanRoot = "/skills",
        Scope = Scope.Custom,
        Agents = [.. agents],
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };

    [Fact]
    public void ExactPathAndExposureAliasesSelectOnePhysicalRowWithAllConsumers()
    {
        var selected = Target("/skills/demo", new AgentMembership("codex", "/codex/demo", true), new AgentMembership("claude", "/claude/demo", true));
        var peer = Target("/other/demo", new AgentMembership("codex", "/other/codex/demo", true));
        var inventory = InventorySnapshot.Empty with { Skills = [selected, peer] };
        Assert.Same(selected, CliDispatcher.SelectLocalReimportTarget(inventory, "/skills/demo", null));
        Assert.Same(selected, CliDispatcher.SelectLocalReimportTarget(inventory, "/codex/demo", null));
        Assert.Same(selected, CliDispatcher.SelectLocalReimportTarget(inventory, null, selected.InstallationId));
        Assert.Equal(2, selected.Agents.Length);
    }

    [Fact]
    public void AmbiguousAliasesOrMissingSelectorsRefuseRatherThanPickFirstSameName()
    {
        var first = Target("/one/demo", new AgentMembership("codex", "/alias/demo", true));
        var second = Target("/two/demo", new AgentMembership("claude", "/alias/demo", true));
        var inventory = InventorySnapshot.Empty with { Skills = [first, second] };
        Assert.Throws<IOException>(() => CliDispatcher.SelectLocalReimportTarget(inventory, "/alias/demo", null));
        Assert.Throws<IOException>(() => CliDispatcher.SelectLocalReimportTarget(inventory, "/missing/demo", null));
        Assert.Throws<IOException>(() => CliDispatcher.SelectLocalReimportTarget(inventory, null, "missing-id"));
        Assert.Throws<ArgumentException>(() => CliDispatcher.SelectLocalReimportTarget(inventory, "/one/demo", first.InstallationId));
        Assert.Throws<ArgumentException>(() => CliDispatcher.SelectLocalReimportTarget(inventory, null, null));
    }
}
