using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhSkillSearchServiceTests
{
    // gh ≥ 2.94 is required, so `--json`/`--owner`/`--page` are always
    // available and emit unconditionally; there is no per-flag gating and no
    // `--limit` (gh 2.94 paginates instead).

    [Fact]
    public void BuildArgs_AlwaysIncludesJsonFields()
    {
        var args = GhSkillSearchService.BuildArgs("render-md", owner: null, page: 1);
        Assert.Equal(
            new[] { "skill", "search", "render-md", "--json", "description,namespace,path,repo,skillName,stars" },
            args);
    }

    [Fact]
    public void BuildArgs_NeverEmitsLimit()
    {
        var args = GhSkillSearchService.BuildArgs("q", owner: null, page: 1);
        Assert.DoesNotContain("--limit", args);
    }

    [Fact]
    public void BuildArgs_AddsOwnerWhenProvided()
    {
        var args = GhSkillSearchService.BuildArgs("q", owner: "vercel-labs", page: 1);
        Assert.Contains("--owner", args);
        var ownerIdx = args.ToList().IndexOf("--owner");
        Assert.Equal("vercel-labs", args[ownerIdx + 1]);
    }

    [Fact]
    public void BuildArgs_OmitsOwnerWhenAbsent()
    {
        var args = GhSkillSearchService.BuildArgs("q", owner: null, page: 1);
        Assert.DoesNotContain("--owner", args);
    }

    [Fact]
    public void BuildArgs_SkipsPageForFirstPage()
    {
        var args = GhSkillSearchService.BuildArgs("q", owner: null, page: 1);
        Assert.DoesNotContain("--page", args);
    }

    [Fact]
    public void BuildArgs_IncludesPageWhenGreaterThanOne()
    {
        var args = GhSkillSearchService.BuildArgs("q", owner: null, page: 2);
        Assert.Contains("--page", args);
        var idx = args.ToList().IndexOf("--page");
        Assert.Equal("2", args[idx + 1]);
    }
}
