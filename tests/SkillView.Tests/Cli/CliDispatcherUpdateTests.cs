using System.Collections.Immutable;
using SkillView.Cli;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Cli;

public class CliDispatcherUpdateTests
{
    [Theory]
    [InlineData("--agent")]
    [InlineData("--scope")]
    public void Update_UnsupportedSelectorFlagRefuses(string flag)
    {
        Assert.NotNull(CliDispatcher.ParseUpdateArgs([flag, "demo"]).Error);
    }

    [Fact]
    public void Update_ExactTargetAndOptionTerminatorArePreserved()
    {
        var args = CliDispatcher.ParseUpdateArgs(["--path", "/skills/demo", "--install-id=abc", "--", "--demo"]);
        Assert.Null(args.Error);
        Assert.Equal("/skills/demo", args.Path);
        Assert.Equal("abc", args.InstallationId);
        Assert.Equal(["--demo"], args.Skills);
    }

    [Fact]
    public void Update_HiddenCoverageIsExplicitAndDoesNotApproveReplacement()
    {
        var args = CliDispatcher.ParseUpdateArgs(["--path", "skills/demo", "--allow-hidden-dirs", "--force"]);
        Assert.Null(args.Error); Assert.True(args.AllowHiddenDirectories); Assert.True(args.Force);
        Assert.Equal("skills/demo", args.Path);
    }

    [Fact]
    public void PackageOwnershipDoesNotLeakToSameNameOutsideLockContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "context", "skills");
        var package = new SkillPackage("owner/repo", "github", null, null, null)
        { InstallationRoot = root };
        Assert.True(package.Describes(Skill("demo", Path.Combine(root, "demo"), null, null)));
        Assert.False(package.Describes(Skill("demo", Path.Combine(Path.GetTempPath(), "custom", "demo"), null, null)));
    }

    private static InstalledSkill Skill(string name, string path, string? version, string? treeSha) => new()
    {
        Name = name,
        ResolvedPath = path,
        ScanRoot = path,
        Scope = Scope.User,
        Agents = ImmutableArray<AgentMembership>.Empty,
        FrontMatter = new SkillFrontMatter
        {
            Name = name,
            Version = version,
            GithubTreeSha = treeSha,
        },
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
    public void UpdateDiff_NoChange_EmptyList()
    {
        var before = Snapshot(Skill("a", "/p/a", "v1", "sha1"));
        var after = Snapshot(Skill("a", "/p/a", "v1", "sha1"));
        Assert.Empty(CliDispatcher.InventoryUpdateDiff(before, after));
    }

    [Fact]
    public void UpdateDiff_TreeShaChanged_Detected()
    {
        var before = Snapshot(Skill("a", "/p/a", "v1", "sha1"));
        var after = Snapshot(Skill("a", "/p/a", "v1", "sha2"));
        var changed = CliDispatcher.InventoryUpdateDiff(before, after);
        Assert.Single(changed);
        Assert.Equal("sha1", changed[0].FromSha);
        Assert.Equal("sha2", changed[0].ToSha);
    }

    [Fact]
    public void UpdateDiff_VersionChanged_Detected()
    {
        var before = Snapshot(Skill("a", "/p/a", "v1", "sha1"));
        var after = Snapshot(Skill("a", "/p/a", "v2", "sha1"));
        var changed = CliDispatcher.InventoryUpdateDiff(before, after);
        Assert.Single(changed);
        Assert.Equal("v1", changed[0].FromVersion);
        Assert.Equal("v2", changed[0].ToVersion);
    }

    [Fact]
    public void UpdateDiff_NewSkillNotReportedByUpdateDiff()
    {
        // Newly-added skills show up in InventoryDiff (Phase 4), not in the
        // update diff. This keeps the two axes cleanly separated.
        var before = Snapshot(Skill("a", "/p/a", "v1", "sha1"));
        var after = Snapshot(Skill("a", "/p/a", "v1", "sha1"), Skill("b", "/p/b", "v1", "shaB"));
        Assert.Empty(CliDispatcher.InventoryUpdateDiff(before, after));
    }

    [Fact]
    public void UpdateDiff_RemovedSkillNotReported()
    {
        var before = Snapshot(Skill("a", "/p/a", "v1", "sha1"), Skill("b", "/p/b", "v1", "shaB"));
        var after = Snapshot(Skill("a", "/p/a", "v1", "sha1"));
        Assert.Empty(CliDispatcher.InventoryUpdateDiff(before, after));
    }

    [Fact]
    public void UpdateDiff_NullToNonNullShaDetected()
    {
        // A skill whose front-matter gained a tree-sha post-update (e.g. it
        // was newly pinned to a ref) counts as a change.
        var before = Snapshot(Skill("a", "/p/a", "v1", null));
        var after = Snapshot(Skill("a", "/p/a", "v1", "shaNew"));
        var changed = CliDispatcher.InventoryUpdateDiff(before, after);
        Assert.Single(changed);
        Assert.Null(changed[0].FromSha);
        Assert.Equal("shaNew", changed[0].ToSha);
    }
}
