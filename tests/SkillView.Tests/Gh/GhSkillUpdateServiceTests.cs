using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhSkillUpdateServiceTests
{
    // gh ≥ 2.94 is required, so `--all`/`--dry-run`/`--force`/`--unpin` emit
    // unconditionally — no per-flag capability gating.

    [Fact]
    public void BuildArgs_MinimalNoOptions()
    {
        var args = GhSkillUpdateService.BuildArgs(new GhSkillUpdateService.Options());
        Assert.Equal(new[] { "skill", "update" }, args);
    }

    [Fact]
    public void BuildArgs_SkillsAppendedAsPositionals()
    {
        var args = GhSkillUpdateService.BuildArgs(
            new GhSkillUpdateService.Options(Skills: new[] { "render-md", "fetch-url" }));
        Assert.Equal(new[] { "skill", "update", "render-md", "fetch-url" }, args);
    }

    [Fact]
    public void BuildArgs_EmptySkillsAreSkipped()
    {
        var args = GhSkillUpdateService.BuildArgs(
            new GhSkillUpdateService.Options(Skills: new[] { "", "  ", "real-one" }));
        Assert.Equal(new[] { "skill", "update", "real-one" }, args);
    }

    [Fact]
    public void BuildArgs_AllEmittedWhenSet()
    {
        Assert.DoesNotContain("--all",
            GhSkillUpdateService.BuildArgs(new GhSkillUpdateService.Options(All: false)));
        Assert.Contains("--all",
            GhSkillUpdateService.BuildArgs(new GhSkillUpdateService.Options(All: true)));
    }

    [Fact]
    public void BuildArgs_DryRunEmittedWhenSet()
    {
        Assert.DoesNotContain("--dry-run",
            GhSkillUpdateService.BuildArgs(new GhSkillUpdateService.Options(DryRun: false)));
        Assert.Contains("--dry-run",
            GhSkillUpdateService.BuildArgs(new GhSkillUpdateService.Options(DryRun: true)));
    }

    [Fact]
    public void BuildArgs_ForceAndUnpinEmittedWhenSet()
    {
        var off = GhSkillUpdateService.BuildArgs(
            new GhSkillUpdateService.Options(Force: false, Unpin: false));
        Assert.DoesNotContain("--force", off);
        Assert.DoesNotContain("--unpin", off);

        var on = GhSkillUpdateService.BuildArgs(
            new GhSkillUpdateService.Options(Force: true, Unpin: true));
        Assert.Contains("--force", on);
        Assert.Contains("--unpin", on);
    }

    [Fact]
    public void BuildArgs_AllFlagsCombined()
    {
        var args = GhSkillUpdateService.BuildArgs(
            new GhSkillUpdateService.Options(
                Skills: new[] { "s1" },
                All: true, DryRun: true, Force: true, Unpin: true));
        Assert.Equal(
            new[] { "skill", "update", "--all", "--dry-run", "--force", "--unpin", "s1" },
            args);
    }

    [Fact]
    public void ParseEntries_UpdatedArrow()
    {
        var entries = GhSkillUpdateService.ParseEntries(
            "Updating render-md from v1.0.0 → v1.1.0\n");
        Assert.Single(entries);
        Assert.Equal("render-md", entries[0].Name);
        Assert.Equal("v1.0.0", entries[0].FromVersion);
        Assert.Equal("v1.1.0", entries[0].ToVersion);
        Assert.Equal("updated", entries[0].Status);
    }

    [Fact]
    public void ParseEntries_UpToDateAndPinned()
    {
        var entries = GhSkillUpdateService.ParseEntries(
            "render-md: up-to-date\nfetch-url: pinned\nold-thing: skipped\n");
        Assert.Equal(3, entries.Length);
        Assert.Equal("up-to-date", entries[0].Status);
        Assert.Equal("pinned", entries[1].Status);
        Assert.Equal("skipped", entries[2].Status);
    }

    [Fact]
    public void ParseEntries_EmptyStdoutIsEmpty()
    {
        Assert.Empty(GhSkillUpdateService.ParseEntries(""));
        Assert.Empty(GhSkillUpdateService.ParseEntries("   \n\n"));
    }
}
