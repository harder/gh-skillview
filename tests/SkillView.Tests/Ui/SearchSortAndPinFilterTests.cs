using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class SearchSortAndPinFilterTests
{
    [Fact]
    public void CycleSearchSort_RoundTrips_Off_To_Off()
    {
        var sort = SkillViewApp.SearchSort.Off;
        sort = SkillViewApp.CycleSearchSort(sort); Assert.Equal(SkillViewApp.SearchSort.StarsDesc, sort);
        sort = SkillViewApp.CycleSearchSort(sort); Assert.Equal(SkillViewApp.SearchSort.NameAsc, sort);
        sort = SkillViewApp.CycleSearchSort(sort); Assert.Equal(SkillViewApp.SearchSort.NameDesc, sort);
        sort = SkillViewApp.CycleSearchSort(sort); Assert.Equal(SkillViewApp.SearchSort.RepoAsc, sort);
        sort = SkillViewApp.CycleSearchSort(sort); Assert.Equal(SkillViewApp.SearchSort.Off, sort);
    }

    [Fact]
    public void ApplySearchSort_StarsDesc_OrdersByStarsHighestFirst_NullsLast()
    {
        var input = new[]
        {
            Result("a", "owner/a", stars: 5),
            Result("b", "owner/b", stars: null),
            Result("c", "owner/c", stars: 99),
            Result("d", "owner/d", stars: 30),
        };

        var ordered = SkillViewApp.ApplySearchSort(input, SkillViewApp.SearchSort.StarsDesc);

        Assert.Equal(new[] { "c", "d", "a", "b" }, ordered.Select(r => r.SkillName));
    }

    [Fact]
    public void ApplySearchSort_NameAsc_OrdersCaseInsensitively()
    {
        var input = new[]
        {
            Result("Banana", "owner/banana"),
            Result("apple", "owner/apple"),
            Result("Cherry", "owner/cherry"),
        };

        var ordered = SkillViewApp.ApplySearchSort(input, SkillViewApp.SearchSort.NameAsc);

        Assert.Equal(new[] { "apple", "Banana", "Cherry" }, ordered.Select(r => r.SkillName));
    }

    [Fact]
    public void ApplySearchSort_Off_PreservesInputOrder()
    {
        var input = new[]
        {
            Result("c", "owner/c", stars: 1),
            Result("a", "owner/a", stars: 100),
            Result("b", "owner/b", stars: 50),
        };

        var ordered = SkillViewApp.ApplySearchSort(input, SkillViewApp.SearchSort.Off);

        Assert.Equal(new[] { "c", "a", "b" }, ordered.Select(r => r.SkillName));
    }

    [Fact]
    public void CyclePin_RoundTrips()
    {
        var p = InstalledTabView.PinFilter.All;
        p = InstalledTabView.CyclePin(p); Assert.Equal(InstalledTabView.PinFilter.PinnedOnly, p);
        p = InstalledTabView.CyclePin(p); Assert.Equal(InstalledTabView.PinFilter.UnpinnedOnly, p);
        p = InstalledTabView.CyclePin(p); Assert.Equal(InstalledTabView.PinFilter.All, p);
    }

    [Fact]
    public void DescribePin_ProducesHumanLabels()
    {
        Assert.Equal("all",            InstalledTabView.DescribePin(InstalledTabView.PinFilter.All));
        Assert.Equal("pinned only",    InstalledTabView.DescribePin(InstalledTabView.PinFilter.PinnedOnly));
        Assert.Equal("unpinned only",  InstalledTabView.DescribePin(InstalledTabView.PinFilter.UnpinnedOnly));
    }

    [Fact]
    public void DescribeSearchSort_LabelsIncludeDirectionGlyph()
    {
        Assert.Contains("stars",   SkillViewApp.DescribeSearchSort(SkillViewApp.SearchSort.StarsDesc));
        Assert.Contains("↓",       SkillViewApp.DescribeSearchSort(SkillViewApp.SearchSort.StarsDesc));
        Assert.Contains("name",    SkillViewApp.DescribeSearchSort(SkillViewApp.SearchSort.NameAsc));
        Assert.Contains("↑",       SkillViewApp.DescribeSearchSort(SkillViewApp.SearchSort.NameAsc));
        Assert.Contains("off",     SkillViewApp.DescribeSearchSort(SkillViewApp.SearchSort.Off));
    }

    private static SearchResultSkill Result(string name, string repo, int? stars = 0) =>
        new(Description: null, Namespace: null, Path: null, Repo: repo, SkillName: name, Stars: stars);
}
