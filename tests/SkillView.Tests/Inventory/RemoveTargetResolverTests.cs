using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class RemoveTargetResolverTests : IDisposable
{
    private readonly string _tempRoot;

    public RemoveTargetResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "skillview-remove-targets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Evaluate_SymlinkTargetRemainsExecutable_WhenCanonicalRemovalIsBlocked()
    {
        if (OperatingSystem.IsWindows()) return;

        var canonicalDir = MakeSkillDir("demo", withGit: true);
        var symlinkPath = Path.Combine(_tempRoot, "claude-demo");
        Directory.CreateSymbolicLink(symlinkPath, canonicalDir);

        var skill = MakeSkill(
            canonicalDir,
            "demo",
            agents: ImmutableArray.Create(new AgentMembership("claude", symlinkPath, true)));

        var snapshot = Snapshot(skill);
        var targets = RemoveTargetResolver.BuildTargets(skill, snapshot);
        var symlinkTarget = Assert.Single(targets.Where(t => t.Kind == RemoveTargetKind.AgentSymlink));

        var evaluation = RemoveTargetResolver.Evaluate(symlinkTarget, snapshot);

        Assert.True(evaluation.CanExecute);
        Assert.False(evaluation.RequiresSecondConfirm);
        Assert.Contains("Unlink", symlinkTarget.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildTargets_IncludesPackageGroup_WhenPackageSourceMatchesMultipleSkills()
    {
        var package = new SkillPackage("npm:@acme/demo-pack", "npm", "https://github.com/acme/demo-pack", null, null);
        var first = MakeSkill(MakeSkillDir("one"), "one", package: package);
        var second = MakeSkill(MakeSkillDir("two"), "two", package: package);
        var third = MakeSkill(MakeSkillDir("three"), "three");

        var targets = RemoveTargetResolver.BuildTargets(first, Snapshot(first, second, third));

        var packageTarget = Assert.Single(targets.Where(t => t.Kind == RemoveTargetKind.PackageGroup));
        Assert.Equal(2, packageTarget.Skills.Length);
        Assert.All(packageTarget.Skills, skill => Assert.Equal(package.Source, skill.Package?.Source));
    }

    [Fact]
    public void BuildTargets_IncludesRepoGroup_FromSharedUpstreamMetadata()
    {
        var upstream = "https://github.com/acme/demo-repo";
        var first = MakeSkill(MakeSkillDir("one"), "one", upstream: upstream);
        var second = MakeSkill(MakeSkillDir("two"), "two", upstream: upstream);
        var third = MakeSkill(MakeSkillDir("three"), "three", upstream: "https://github.com/acme/other");

        var targets = RemoveTargetResolver.BuildTargets(first, Snapshot(first, second, third));

        var repoTarget = Assert.Single(targets.Where(t => t.Kind == RemoveTargetKind.RepoGroup));
        Assert.Equal(2, repoTarget.Skills.Length);
        Assert.All(repoTarget.Skills, skill => Assert.Equal(upstream, skill.FrontMatter.Upstream));
    }

    private InventorySnapshot Snapshot(params InstalledSkill[] skills) => new()
    {
        Skills = skills.ToImmutableArray(),
        ScannedRoots = ImmutableArray.Create(new ScanRoot(_tempRoot, Scope.User, "claude")),
        UsedGhSkillList = false,
        CapturedAt = DateTimeOffset.UtcNow,
    };

    private string MakeSkillDir(string name, bool withGit = false)
    {
        var dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: " + name + "\n---\nbody");
        if (withGit)
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
        }

        return dir;
    }

    private InstalledSkill MakeSkill(
        string dir,
        string name,
        ImmutableArray<AgentMembership>? agents = null,
        SkillPackage? package = null,
        string? upstream = null) => new()
    {
        Name = name,
        ResolvedPath = dir,
        ScanRoot = _tempRoot,
        Scope = Scope.User,
        Agents = agents ?? ImmutableArray<AgentMembership>.Empty,
        FrontMatter = new SkillFrontMatter
        {
            Name = name,
            Upstream = upstream,
        },
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = agents?.Any(a => a.IsSymlink) == true,
        InstalledAt = null,
        Package = package,
    };
}
