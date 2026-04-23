using System.Collections.Immutable;
using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhSkillSearchServiceTests
{
    private static CapabilityProfile CapsWith(params string[] searchFlags)
    {
        return CapabilityProfile.Empty with
        {
            SkillSubcommandPresent = true,
            SearchFlags = ImmutableHashSet.CreateRange(searchFlags),
        };
    }

    [Fact]
    public void BuildArgs_AlwaysIncludesJsonAndLimitWhenCapabilitiesKnown()
    {
        var caps = CapsWith("--json", "--limit", "--owner");
        var args = GhSkillSearchService.BuildArgs("render-md", caps, owner: null, limit: 30, page: 1);

        Assert.Equal(new[] { "skill", "search", "render-md", "--json", "description,namespace,path,repo,skillName,stars", "--limit", "30" }, args);
    }

    [Fact]
    public void BuildArgs_AddsOwnerOnlyWhenSupported()
    {
        var caps = CapsWith("--json", "--limit"); // no --owner
        var args = GhSkillSearchService.BuildArgs("q", caps, owner: "vercel-labs", limit: 50, page: 1);
        Assert.DoesNotContain("--owner", args);
    }

    [Fact]
    public void BuildArgs_AddsOwnerWhenSupported()
    {
        var caps = CapsWith("--json", "--limit", "--owner");
        var args = GhSkillSearchService.BuildArgs("q", caps, owner: "vercel-labs", limit: 50, page: 1);

        Assert.Contains("--owner", args);
        var ownerIdx = args.ToList().IndexOf("--owner");
        Assert.Equal("vercel-labs", args[ownerIdx + 1]);
    }

    [Fact]
    public void BuildArgs_SkipsPageWhenProbeLacksFlagEvenIfPageGtOne()
    {
        var caps = CapsWith("--json", "--limit"); // no --page
        var args = GhSkillSearchService.BuildArgs("q", caps, owner: null, limit: 30, page: 3);
        Assert.DoesNotContain("--page", args);
    }

    [Fact]
    public void BuildArgs_IncludesPageWhenSupportedAndRequested()
    {
        var caps = CapsWith("--json", "--limit", "--page");
        var args = GhSkillSearchService.BuildArgs("q", caps, owner: null, limit: 30, page: 2);
        Assert.Contains("--page", args);
        var idx = args.ToList().IndexOf("--page");
        Assert.Equal("2", args[idx + 1]);
    }

    [Fact]
    public void BuildArgs_FallbackWhenCapabilitiesUnknown_IncludesJsonAndLimit()
    {
        // Empty capabilities (e.g. probe not yet run) → still emit the core
        // flags so the call works against v2.91.0 (which has --json + --limit).
        var caps = CapabilityProfile.Empty;
        var args = GhSkillSearchService.BuildArgs("q", caps, owner: null, limit: 30, page: 1);
        Assert.Contains("--json", args);
        Assert.Contains("--limit", args);
    }
}
