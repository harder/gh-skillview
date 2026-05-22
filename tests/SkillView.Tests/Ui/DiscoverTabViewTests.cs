using SkillView.Ui.Tabs;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class DiscoverTabViewTests
{
    [Fact]
    public void BuildFilterSummary_OmitsDefaultState()
    {
        var summary = DiscoverTabView.BuildFilterSummaryForTests(
            owner: string.Empty,
            agent: string.Empty,
            limit: 30,
            hiddenDirs: false);

        Assert.Equal(string.Empty, summary);
    }

    [Fact]
    public void BuildFilterSummary_UsesCompactWingetStyleCopy()
    {
        var summary = DiscoverTabView.BuildFilterSummaryForTests(
            owner: "github",
            agent: "copilot",
            limit: 25,
            hiddenDirs: true);

        Assert.Equal("Filters: owner github · agent copilot · limit 25 · hidden dirs on", summary);
    }

    [Fact]
    public void BuildFacetSummary_UsesLocationsWording()
    {
        var summary = DiscoverTabView.BuildFacetSummaryForTests(
            agent: "github-copilot",
            location: "/home/user/.github/skills",
            provenance: "fs-scan",
            hiddenDirs: false);

        Assert.Contains("Locations:", summary);
        Assert.DoesNotContain("roots", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, "on")]
    [InlineData(false, "off")]
    public void BuildFacetSummary_IncludesHiddenDirState(bool hiddenDirs, string expected)
    {
        var summary = DiscoverTabView.BuildFacetSummaryForTests(
            agent: string.Empty,
            location: string.Empty,
            provenance: string.Empty,
            hiddenDirs: hiddenDirs);

        Assert.Contains($"Hidden dirs: {expected}", summary);
    }

    [Fact]
    public void BuildFacetSummary_OmitsEmptyFields()
    {
        var summary = DiscoverTabView.BuildFacetSummaryForTests(
            agent: string.Empty,
            location: string.Empty,
            provenance: string.Empty,
            hiddenDirs: false);

        Assert.DoesNotContain("Agent:", summary);
        Assert.DoesNotContain("Locations:", summary);
        Assert.DoesNotContain("Provenance:", summary);
    }
}
