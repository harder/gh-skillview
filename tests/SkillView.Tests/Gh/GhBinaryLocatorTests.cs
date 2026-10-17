using SkillView.Gh;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhBinaryLocatorTests
{
    [Fact]
    public void DefaultLocatorUsesItsCapturedRunnerPathAndHostRatherThanParentEnvironment()
    {
        var directory = Directory.CreateTempSubdirectory("skillview-locator-context-");
        try
        {
            var name = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
            var candidate = Path.Combine(directory.FullName, name); File.WriteAllText(candidate, "inert address fixture; never executed");
            var host = Path.Combine(directory.FullName, "explicit-host"); File.WriteAllText(host, "inert host fixture; never executed");
            var logger = new Logger();
            var values = new Dictionary<string, string> { ["PATH"] = directory.FullName, ["GH_EXTENSION"] = "0", ["GH_PATH"] = host };
            var runner = new ProcessRunner(logger, environment: values).CaptureContext();
            values["PATH"] = ""; values["GH_EXTENSION"] = "1";
            Assert.Equal(candidate, new GhBinaryLocator(runner, logger).FindOnPath(TestContext.Current.CancellationToken));
            var relative = new ProcessRunner(logger, environment: new Dictionary<string, string>
            { ["PATH"] = ".", ["GH_EXTENSION"] = "0" }, defaultWorkingDirectory: directory.FullName).CaptureContext();
            Assert.Equal(candidate, new GhBinaryLocator(relative, logger).FindOnPath(TestContext.Current.CancellationToken));
            var hosted = runner.WithContext(directory.FullName, new Dictionary<string, string> { ["GH_EXTENSION"] = "1" });
            Assert.Equal(host, new GhBinaryLocator(hosted, logger).FindOnPath(TestContext.Current.CancellationToken));
            var isolated = runner.WithIsolatedContext(directory.FullName, new Dictionary<string, string>());
            Assert.Null(new GhBinaryLocator(isolated, logger).FindOnPath(TestContext.Current.CancellationToken));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void FindOnPath_PrefersGhPathSuppliedByExtensionHost()
    {
        var logger = new Logger(LogLevel.Debug);
        var hostPath = Path.GetFullPath("gh-from-host");
        var locator = new GhBinaryLocator(
            new ProcessRunner(logger), logger,
            pathProvider: () => "ignored-path",
            fileExists: path => path == hostPath,
            extensionGhPathProvider: () => hostPath,
            extensionFlagProvider: () => "1");

        Assert.Equal(hostPath, locator.FindOnPath(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FindOnPath_IgnoresGhPathOutsideExtensionHost()
    {
        var logger = new Logger(LogLevel.Debug);
        var hostPath = Path.GetFullPath("gh-from-host");
        var locator = new GhBinaryLocator(
            new ProcessRunner(logger), logger,
            pathProvider: () => "path-entry",
            fileExists: path => path == hostPath,
            extensionGhPathProvider: () => hostPath,
            extensionFlagProvider: () => null);

        Assert.Null(locator.FindOnPath(TestContext.Current.CancellationToken));
    }

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
    public void MinimumVersion_is_2_97_0()
    {
        Assert.Equal(2, GhBinaryLocator.MinimumVersion.Major);
        Assert.Equal(97, GhBinaryLocator.MinimumVersion.Minor);
        Assert.Equal(0, GhBinaryLocator.MinimumVersion.Patch);
    }

    [Theory]
    [InlineData("2.94.0", false)]
    [InlineData("2.94.3", false)]
    [InlineData("2.95.0", false)]
    [InlineData("2.96.0", false)]
    [InlineData("2.97.0", true)]
    [InlineData("2.97.0-rc.1", true)] // SemVer strips the pre-release tag → 2.97.0
    [InlineData("2.97.4", true)]
    [InlineData("2.99.0", true)]
    [InlineData("2.100.0", true)]
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
