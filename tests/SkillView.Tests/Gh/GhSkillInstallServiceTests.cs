using System.Linq;
using SkillView.Gh;
using SkillView.Gh.Models;
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

    // --- discovery listing (gh ≥ 2.95, cli/cli#13548) --------------------

    [Fact]
    public void BuildListArgs_HasNoSkillNameAndNoAll()
    {
        // The bare repo (no skill, no --all) is what triggers gh's
        // non-interactive listing path.
        var args = GhSkillInstallService.BuildListArgs("owner/repo", version: null, allowHiddenDirs: false);
        Assert.Equal(new[] { "skill", "install", "owner/repo" }, args);
    }

    [Fact]
    public void BuildListArgs_VersionConcatenatedAndHiddenDirsFlag()
    {
        var args = GhSkillInstallService.BuildListArgs("owner/repo", "v1.2.0", allowHiddenDirs: true);
        Assert.Equal(new[] { "skill", "install", "owner/repo@v1.2.0", "--allow-hidden-dirs" }, args);
        Assert.DoesNotContain("--all", args);
    }

    [Fact]
    public void ParseRepoSkillListing_ParsesTabSeparatedRows()
    {
        var stdout = "code-review\tReviews pull requests\ngit-commit\tWrites commit messages\n";
        var skills = GhSkillInstallService.ParseRepoSkillListing(stdout);

        Assert.Equal(2, skills.Length);
        Assert.Equal("code-review", skills[0].Name);
        Assert.Equal("Reviews pull requests", skills[0].Description);
        Assert.Equal("git-commit", skills[1].Name);
        Assert.Equal("Writes commit messages", skills[1].Description);
    }

    [Fact]
    public void ParseRepoSkillListing_ToleratesBlankLinesNameOnlyHeaderAndCrlf()
    {
        var stdout = "SKILL\tDESCRIPTION\r\n\r\nlonely-skill\r\ncode-review\tReviews PRs\r\n";
        var skills = GhSkillInstallService.ParseRepoSkillListing(stdout);

        // Header dropped, blank line skipped, name-only line kept with empty desc.
        Assert.Equal(2, skills.Length);
        Assert.Equal("lonely-skill", skills[0].Name);
        Assert.Equal("", skills[0].Description);
        Assert.Equal("code-review", skills[1].Name);
    }

    [Theory]
    [InlineData("[root] code-review", "code-review")]          // root namespace → bare name
    [InlineData("[monalisa] code-review", "monalisa/code-review")] // other namespace → ns/name
    [InlineData("[ROOT] x", "x")]                                // case-insensitive root
    [InlineData("plain-name", "plain-name")]                    // no tag → unchanged
    [InlineData("[root] a/b", "a/b")]                            // already-pathed name kept
    [InlineData("[unterminated name", "[unterminated name")]    // malformed → unchanged
    [InlineData("[root] ", "[root] ")]                          // empty rest → unchanged
    public void NormalizeSkillName_StripsNamespaceTag(string raw, string expected)
    {
        Assert.Equal(expected, GhSkillInstallService.NormalizeSkillName(raw));
    }

    [Fact]
    public void ParseRepoSkillListing_StripsRootNamespaceFromNames()
    {
        // gh emits "[root] name<TAB>desc"; the picker/install must use bare name.
        var stdout = "[root] code-review\tReviews PRs\n[root] git-commit\t\n";
        var skills = GhSkillInstallService.ParseRepoSkillListing(stdout);

        Assert.Equal(new[] { "code-review", "git-commit" }, skills.Select(s => s.Name));
        Assert.Equal("Reviews PRs", skills[0].Description);
    }

    [Fact]
    public void ParseRepoSkillListing_DeduplicatesByNameAndHandlesEmpty()
    {
        Assert.Empty(GhSkillInstallService.ParseRepoSkillListing(null));
        Assert.Empty(GhSkillInstallService.ParseRepoSkillListing("   \n  \n"));

        var dup = GhSkillInstallService.ParseRepoSkillListing("a\tone\na\ttwo\nb\tthree");
        Assert.Equal(2, dup.Length);
        Assert.Equal("one", dup.Single(s => s.Name == "a").Description);
    }

    [Fact]
    public void BuildInstallPlan_AllSelectedCollapsesToAll()
    {
        var discovered = new[] { new RepoSkill("a", ""), new RepoSkill("b", "") };
        var plan = GhSkillInstallService.BuildInstallPlan(
            discovered, new HashSet<string> { "a", "b" });

        Assert.True(plan.UseAll);
        Assert.True(plan.SkillNames.IsDefaultOrEmpty);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public void BuildInstallPlan_SubsetInstallsByName()
    {
        var discovered = new[] { new RepoSkill("a", ""), new RepoSkill("b", ""), new RepoSkill("c", "") };
        var plan = GhSkillInstallService.BuildInstallPlan(
            discovered, new HashSet<string> { "a", "c" });

        Assert.False(plan.UseAll);
        Assert.Equal(new[] { "a", "c" }, plan.SkillNames);
    }

    [Fact]
    public void BuildInstallPlan_NoneSelectedIsEmpty()
    {
        var discovered = new[] { new RepoSkill("a", "") };
        var plan = GhSkillInstallService.BuildInstallPlan(discovered, new HashSet<string>());

        Assert.False(plan.UseAll);
        Assert.True(plan.IsEmpty);
    }
}
