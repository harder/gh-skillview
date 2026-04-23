using System.Collections.Immutable;
using SkillView.Cli;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Cli;

public class CliDispatcherInstallTests
{
    private static InstalledSkill Skill(string name, string path) => new()
    {
        Name = name,
        ResolvedPath = path,
        ScanRoot = path,
        Scope = Scope.User,
        Agents = ImmutableArray<AgentMembership>.Empty,
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null,
    };

    private static InventorySnapshot Snapshot(params InstalledSkill[] skills) => new()
    {
        Skills = skills.ToImmutableArray(),
        ScannedRoots = ImmutableArray<ScanRoot>.Empty,
        UsedGhSkillList = false,
        CapturedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void InventoryDiff_NoChange_EmptyAdded()
    {
        var before = Snapshot(Skill("a", "/p/a"), Skill("b", "/p/b"));
        var after = Snapshot(Skill("a", "/p/a"), Skill("b", "/p/b"));
        Assert.Empty(CliDispatcher.InventoryDiff(before, after));
    }

    [Fact]
    public void InventoryDiff_NewSkill_ReportedAsAdded()
    {
        var before = Snapshot(Skill("a", "/p/a"));
        var after = Snapshot(Skill("a", "/p/a"), Skill("b", "/p/b"));
        var added = CliDispatcher.InventoryDiff(before, after);
        Assert.Single(added);
        Assert.Equal("b", added[0].Name);
    }

    [Fact]
    public void InventoryDiff_RemovedSkillNotReported()
    {
        // Diff reports what's new in `after` — removals aren't the install
        // path's concern (Phase 6 covers remove).
        var before = Snapshot(Skill("a", "/p/a"), Skill("b", "/p/b"));
        var after = Snapshot(Skill("a", "/p/a"));
        Assert.Empty(CliDispatcher.InventoryDiff(before, after));
    }

    [Fact]
    public void InventoryDiff_KeyOnResolvedPath()
    {
        // Same name but different path = new entry.
        var before = Snapshot(Skill("a", "/p/a"));
        var after = Snapshot(Skill("a", "/p/a"), Skill("a", "/other/a"));
        var added = CliDispatcher.InventoryDiff(before, after);
        Assert.Single(added);
        Assert.Equal("/other/a", added[0].ResolvedPath);
    }
}
