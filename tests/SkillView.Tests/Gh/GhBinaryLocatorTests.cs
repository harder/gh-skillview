using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhBinaryLocatorTests
{
    [Fact]
    public void MinimumVersion_is_2_91_0()
    {
        Assert.Equal(2, GhBinaryLocator.MinimumVersion.Major);
        Assert.Equal(91, GhBinaryLocator.MinimumVersion.Minor);
        Assert.Equal(0, GhBinaryLocator.MinimumVersion.Patch);
    }

    [Theory]
    [InlineData("2.91.0", true)]
    [InlineData("2.91.0-rc.1", true)]
    [InlineData("2.91.1", true)]
    [InlineData("2.92.0", true)]
    [InlineData("3.0.0", true)]
    [InlineData("2.90.9", false)]
    [InlineData("2.0.0", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("banana", false)]
    public void SatisfiesMinimum_matches_expected(string? input, bool expected)
    {
        Assert.Equal(expected, GhBinaryLocator.SatisfiesMinimum(input));
    }
}
