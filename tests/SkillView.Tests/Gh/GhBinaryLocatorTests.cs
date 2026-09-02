using SkillView.Gh;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhBinaryLocatorTests
{
    [Fact]
    public void FindOnPath_CancellationDuringPathReadStopsBeforeFileProbe()
    {
        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        using var cancellation = new CancellationTokenSource();
        var probes = 0;
        var locator = new GhBinaryLocator(
            runner,
            logger,
            pathProvider: () =>
            {
                cancellation.Cancel();
                return "first-entry";
            },
            fileExists: _ =>
            {
                probes++;
                return false;
            });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            locator.FindOnPath(cancellation.Token));
        Assert.Equal(0, probes);
    }

    [Fact]
    public void FindOnPath_CancellationStopsBetweenEntries()
    {
        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        using var cancellation = new CancellationTokenSource();
        var probes = 0;
        var locator = new GhBinaryLocator(
            runner,
            logger,
            pathProvider: () => string.Join(
                Path.PathSeparator,
                "first-entry",
                "second-entry"),
            fileExists: _ =>
            {
                probes++;
                cancellation.Cancel();
                return false;
            });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            locator.FindOnPath(cancellation.Token));
        Assert.Equal(1, probes);
    }

    [Fact]
    public void MinimumVersion_is_2_99_0()
    {
        Assert.Equal(2, GhBinaryLocator.MinimumVersion.Major);
        Assert.Equal(99, GhBinaryLocator.MinimumVersion.Minor);
        Assert.Equal(0, GhBinaryLocator.MinimumVersion.Patch);
    }

    [Theory]
    [InlineData("2.94.0", false)]
    [InlineData("2.94.3", false)]
    [InlineData("2.95.0", false)]
    [InlineData("2.98.0", false)]
    [InlineData("2.99.0", true)]
    [InlineData("2.99.0-rc.1", true)] // SemVer strips the pre-release tag → 2.99.0
    [InlineData("2.99.4", true)]
    [InlineData("3.0.0", true)]
    [InlineData("2.92.0", false)]
    [InlineData("2.0.0", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("banana", false)]
    public void SatisfiesMinimum_matches_expected(string? input, bool expected)
    {
        Assert.Equal(expected, GhBinaryLocator.SatisfiesMinimum(input));
    }
}
