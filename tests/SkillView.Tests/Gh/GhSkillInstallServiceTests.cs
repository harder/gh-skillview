using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhSkillInstallServiceTests
{
    // gh ≥ 2.94 is required, so every flag emits unconditionally — there is no
    // per-flag capability gating.

    [Fact]
    public void BuildArgs_MinimalRepoOnly()
    {
        var args = GhSkillInstallService.BuildArgs(
            "vercel-labs/skills", skillName: null, new GhSkillInstallService.Options());
        Assert.Equal(new[] { "skill", "install", "vercel-labs/skills" }, args);
    }

    [Fact]
    public void BuildArgs_AppendsSkillNameAsPositional()
    {
        var args = GhSkillInstallService.BuildArgs(
            "owner/repo", "render-md", new GhSkillInstallService.Options());
        Assert.Equal(new[] { "skill", "install", "owner/repo", "render-md" }, args);
    }

    [Fact]
    public void BuildArgs_VersionIsConcatenatedWithAt()
    {
        var args = GhSkillInstallService.BuildArgs(
            "owner/repo", skillName: null,
            new GhSkillInstallService.Options(Version: "v2.0.0"));
        Assert.Contains("owner/repo@v2.0.0", args);
        Assert.DoesNotContain("--version", args);
    }

    [Fact]
    public void BuildArgs_AgentsAreRepeatable()
    {
        var args = GhSkillInstallService.BuildArgs(
            "o/r", null,
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
            "o/r", null,
            new GhSkillInstallService.Options(Scope: "custom", Path: "/tmp/skills"));
        var list = args.ToList();
        var scopeIdx = list.IndexOf("--scope");
        // A custom directory maps to gh's `--dir` (gh has no `--path`).
        var dirIdx = list.IndexOf("--dir");
        Assert.True(scopeIdx >= 0);
        Assert.Equal("custom", list[scopeIdx + 1]);
        Assert.True(dirIdx >= 0);
        Assert.Equal("/tmp/skills", list[dirIdx + 1]);
        Assert.DoesNotContain("--path", list);
    }

    [Fact]
    public void BuildArgs_PinAndForceAreFlags()
    {
        var args = GhSkillInstallService.BuildArgs(
            "o/r", null,
            new GhSkillInstallService.Options(Pin: true, Overwrite: true));
        Assert.Contains("--pin", args);
        Assert.Contains("--force", args);
    }

    [Fact]
    public void BuildArgs_UpstreamEmittedWhenProvided()
    {
        var args = GhSkillInstallService.BuildArgs(
            "o/r", null,
            new GhSkillInstallService.Options(Upstream: "https://x.test/upstream.git"));
        Assert.Contains("--upstream", args);
        var idx = args.ToList().IndexOf("--upstream");
        Assert.Equal("https://x.test/upstream.git", args[idx + 1]);
    }

    [Fact]
    public void BuildArgs_AllowHiddenDirsEmittedWhenSet()
    {
        var off = GhSkillInstallService.BuildArgs(
            "o/r", null, new GhSkillInstallService.Options(AllowHiddenDirs: false));
        Assert.DoesNotContain("--allow-hidden-dirs", off);

        var on = GhSkillInstallService.BuildArgs(
            "o/r", null, new GhSkillInstallService.Options(AllowHiddenDirs: true));
        Assert.Contains("--allow-hidden-dirs", on);
    }

    [Fact]
    public void BuildArgs_FromLocalEmittedWhenSet()
    {
        var off = GhSkillInstallService.BuildArgs(
            "o/r", null, new GhSkillInstallService.Options(FromLocal: false));
        Assert.DoesNotContain("--from-local", off);

        var on = GhSkillInstallService.BuildArgs(
            "o/r", null, new GhSkillInstallService.Options(FromLocal: true));
        Assert.Contains("--from-local", on);
    }

    [Fact]
    public void BuildArgs_AllEmittedWithoutSkillName()
    {
        var args = GhSkillInstallService.BuildArgs(
            "o/r", skillName: null, new GhSkillInstallService.Options(All: true));
        Assert.Equal(new[] { "skill", "install", "o/r", "--all" }, args);
    }

    [Fact]
    public void BuildArgs_EmptyAgentEntriesAreSkipped()
    {
        var args = GhSkillInstallService.BuildArgs(
            "o/r", null,
            new GhSkillInstallService.Options(Agents: new[] { "", "  ", "claude" }));
        Assert.Single(args, x => x == "--agent");
    }
}
