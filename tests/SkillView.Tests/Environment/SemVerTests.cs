using SkillView.Diagnostics;
using Xunit;

namespace SkillView.Tests.Environment;

public class SemVerTests
{
    [Theory]
    [InlineData("2.91.0", 2, 91, 0)]
    [InlineData("v2.91.0", 2, 91, 0)]
    [InlineData("2.91", 2, 91, 0)]
    [InlineData("2.91.0-rc.1", 2, 91, 0)]
    [InlineData("2.91.0+build.3", 2, 91, 0)]
    [InlineData("2.91.0 (2026-03-14)", 2, 91, 0)]
    [InlineData(" 10.1.5 ", 10, 1, 5)]
    public void Parses_canonical_forms(string input, int maj, int min, int patch)
    {
        Assert.True(SemVer.TryParse(input, out var v));
        Assert.Equal(maj, v.Major);
        Assert.Equal(min, v.Minor);
        Assert.Equal(patch, v.Patch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1")]
    [InlineData("2.91.0.1")]
    [InlineData("-1.0.0")]
    public void Rejects_invalid(string? input)
    {
        Assert.False(SemVer.TryParse(input, out _));
    }

    [Fact]
    public void Ordering_is_major_then_minor_then_patch()
    {
        var a = new SemVer(2, 90, 9);
        var b = new SemVer(2, 91, 0);
        var c = new SemVer(2, 91, 1);
        var d = new SemVer(3, 0, 0);

        Assert.True(a < b);
        Assert.True(b < c);
        Assert.True(c < d);
        Assert.True(d > a);
    }

    [Fact]
    public void ToString_is_invariant_three_part()
    {
        Assert.Equal("2.91.0", new SemVer(2, 91, 0).ToString());
    }
}
