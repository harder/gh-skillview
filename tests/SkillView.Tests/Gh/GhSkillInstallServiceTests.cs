using System.Collections.Immutable;
using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhSkillInstallServiceTests
{
    private static CapabilityProfile CapsWith(params string[] installFlags)
    {
        return CapabilityProfile.Empty with
        {
            SkillSubcommandPresent = true,
            InstallFlags = ImmutableHashSet.CreateRange(installFlags),
        };
    }

    [Fact]
    public void BuildArgs_MinimalRepoOnly()
    {
        var args = GhSkillInstallService.BuildArgs(
            "vercel-labs/skills", skillName: null, CapsWith(), new GhSkillInstallService.Options());
        Assert.Equal(new[] { "skill", "install", "vercel-labs/skills" }, args);
    }

    [Fact]
    public void BuildArgs_AppendsSkillNameAsPositional()
    {
        var args = GhSkillInstallService.BuildArgs(
            "owner/repo", "render-md", CapsWith(), new GhSkillInstallService.Options());
        Assert.Equal(new[] { "skill", "install", "owner/repo", "render-md" }, args);
    }

    [Fact]
    public void BuildArgs_VersionIsConcatenatedWithAt()
    {
        var args = GhSkillInstallService.BuildArgs(
            "owner/repo", skillName: null, CapsWith(),
            new GhSkillInstallService.Options(Version: "v2.0.0"));
        Assert.Contains("owner/repo@v2.0.0", args);
        Assert.DoesNotContain("--version", args);
    }

    [Fact]
    public void BuildArgs_AgentsAreRepeatable()
    {
        var args = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith("--agent"),
            new GhSkillInstallService.Options(Agents: new[] { "claude", "copilot", "cursor" }));
        var list = args.ToList();
        Assert.Equal(3, list.Count(x => x == "--agent"));
        Assert.Contains("claude", list);
        Assert.Contains("copilot", list);
        Assert.Contains("cursor", list);
    }

    [Fact]
    public void BuildArgs_ScopeAndPathPassthrough()
    {
        var args = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith(),
            new GhSkillInstallService.Options(Scope: "custom", Path: "/tmp/skills"));
        var list = args.ToList();
        var scopeIdx = list.IndexOf("--scope");
        var pathIdx = list.IndexOf("--path");
        Assert.True(scopeIdx >= 0);
        Assert.Equal("custom", list[scopeIdx + 1]);
        Assert.True(pathIdx >= 0);
        Assert.Equal("/tmp/skills", list[pathIdx + 1]);
    }

    [Fact]
    public void BuildArgs_PinAndForceAreFlags()
    {
        var args = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith(),
            new GhSkillInstallService.Options(Pin: true, Overwrite: true));
        Assert.Contains("--pin", args);
        Assert.Contains("--force", args);
    }

    [Fact]
    public void BuildArgs_UpstreamRequiresCapability()
    {
        // Without the capability flag, --upstream must not be emitted.
        var missing = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith(),
            new GhSkillInstallService.Options(Upstream: "https://x.test/upstream.git"));
        Assert.DoesNotContain("--upstream", missing);

        var present = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith("--upstream"),
            new GhSkillInstallService.Options(Upstream: "https://x.test/upstream.git"));
        Assert.Contains("--upstream", present);
        var idx = present.ToList().IndexOf("--upstream");
        Assert.Equal("https://x.test/upstream.git", present[idx + 1]);
    }

    [Fact]
    public void BuildArgs_AllowHiddenDirsRequiresCapability()
    {
        var off = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith(),
            new GhSkillInstallService.Options(AllowHiddenDirs: true));
        Assert.DoesNotContain("--allow-hidden-dirs", off);

        var on = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith("--allow-hidden-dirs"),
            new GhSkillInstallService.Options(AllowHiddenDirs: true));
        Assert.Contains("--allow-hidden-dirs", on);
    }

    [Fact]
    public void BuildArgs_RepoPathRequiresCapability()
    {
        var off = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith(),
            new GhSkillInstallService.Options(RepoPath: "subdir/skill"));
        Assert.DoesNotContain("--repo-path", off);

        var on = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith("--repo-path"),
            new GhSkillInstallService.Options(RepoPath: "subdir/skill"));
        Assert.Contains("--repo-path", on);
        var idx = on.ToList().IndexOf("--repo-path");
        Assert.Equal("subdir/skill", on[idx + 1]);
    }

    [Fact]
    public void BuildArgs_FromLocalRequiresCapability()
    {
        var off = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith(),
            new GhSkillInstallService.Options(FromLocal: true));
        Assert.DoesNotContain("--from-local", off);

        var on = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith("--from-local"),
            new GhSkillInstallService.Options(FromLocal: true));
        Assert.Contains("--from-local", on);
    }

    [Fact]
    public void BuildArgs_EmptyAgentEntriesAreSkipped()
    {
        var args = GhSkillInstallService.BuildArgs(
            "o/r", null, CapsWith("--agent"),
            new GhSkillInstallService.Options(Agents: new[] { "", "  ", "claude" }));
        Assert.Single(args, x => x == "--agent");
    }
}
