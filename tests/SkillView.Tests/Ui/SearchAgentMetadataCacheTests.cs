using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class SearchAgentMetadataCacheTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("claude", "claude-code")]
    [InlineData("Claude", "claude-code")]
    [InlineData("claude-code", "claude-code")]
    [InlineData("copilot", "github-copilot")]
    [InlineData("github-copilot", "github-copilot")]
    [InlineData("gemini", "gemini-cli")]
    [InlineData("gemini-cli", "gemini-cli")]
    [InlineData("cursor", "cursor")]
    public void NormalizeAgent_ReturnsCurrentGhIds(string? input, string? expected)
    {
        Assert.Equal(expected, SearchAgentMetadataCache.NormalizeAgent(input));
    }

    [Fact]
    public void ExtractAgentsFromMarkdown_ParsesFrontMatterAgents()
    {
        const string markdown = """
            ---
            name: demo
            agents:
              - claude
              - github-copilot
            ---

            # Demo
            """;

        var agents = SearchAgentMetadataCache.ExtractAgentsFromMarkdown(markdown);

        Assert.Equal(
            new[] { "claude-code", "github-copilot" },
            agents);
    }

    [Fact]
    public void FilterResults_UsesCachedPreviewMetadata()
    {
        var first = Skill("owner/one", "alpha");
        var second = Skill("owner/two", "beta");
        var cache = new SearchAgentMetadataCache();

        cache.Store(first, ImmutableArray.Create("claude-code"));
        cache.Store(second, ImmutableArray.Create("github-copilot"));

        var filtered = cache.Filter(
            new[] { first, second },
            requestedAgent: "claude");

        Assert.Collection(
            filtered,
            only => Assert.Equal("alpha", only.SkillName));
    }

    [Fact]
    public void MissingMetadata_DoesNotMatchAgentFilter()
    {
        var first = Skill("owner/one", "alpha");
        var second = Skill("owner/two", "beta");
        var cache = new SearchAgentMetadataCache();

        cache.Store(first, ImmutableArray.Create("claude-code"));

        var filtered = cache.Filter(
            new[] { first, second },
            requestedAgent: "claude-code");

        Assert.Collection(
            filtered,
            only => Assert.Equal("alpha", only.SkillName));
    }

    [Fact]
    public void Store_EvictsLeastRecentlyUsedMetadataAtCapacity()
    {
        var cache = new SearchAgentMetadataCache(capacity: 2);
        var first = Skill("owner/one", "alpha");
        var second = Skill("owner/two", "beta");
        var third = Skill("owner/three", "gamma");

        cache.Store(first, ImmutableArray.Create("claude-code"));
        cache.Store(second, ImmutableArray.Create("github-copilot"));
        Assert.True(cache.Has(first));
        cache.Store(third, ImmutableArray.Create("gemini-cli"));

        Assert.Equal(2, cache.CountForTests);
        Assert.True(cache.Has(first));
        Assert.False(cache.Has(second));
        Assert.True(cache.Has(third));
    }

    [Fact]
    public void Store_UpdatingExistingValueRefreshesRecency()
    {
        var cache = new SearchAgentMetadataCache(capacity: 2);
        var first = Skill("owner/one", "alpha");
        var second = Skill("owner/two", "beta");
        var third = Skill("owner/three", "gamma");

        cache.Store(first, ImmutableArray.Create("claude-code"));
        cache.Store(second, ImmutableArray.Create("github-copilot"));
        cache.Store(first, ImmutableArray.Create("gemini-cli"));
        cache.Store(third, ImmutableArray.Create("github-copilot"));

        Assert.True(cache.Has(first));
        Assert.False(cache.Has(second));
        Assert.True(cache.Has(third));
        Assert.Single(cache.Filter([first], "gemini-cli"));
    }

    private static SearchResultSkill Skill(string repo, string skillName) =>
        new(
            Description: null,
            Namespace: "demo",
            Path: "skills/" + skillName,
            Repo: repo,
            SkillName: skillName,
            Stars: 1);
}
