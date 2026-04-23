using System.Diagnostics;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Gh;

/// Contract tests that run against a real `gh` binary. Gated behind
/// the SKILLVIEW_CONTRACT_TESTS environment variable so they only run in
/// the nightly workflow (or local opt-in). Shape-level assertions only —
/// never assert on live search data or exact help text.
[Trait("Category", "Contract")]
public class GhCliContractTests
{
    private static bool ShouldRun =>
        string.Equals(
            System.Environment.GetEnvironmentVariable("SKILLVIEW_CONTRACT_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static string? GhPath()
    {
        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        var locator = new GhBinaryLocator(runner, logger);
        return locator.FindOnPath();
    }

    [Fact]
    public async Task GhVersion_ProducesParseableOutput()
    {
        if (!ShouldRun) return;
        var path = GhPath();
        Assert.NotNull(path);

        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        var locator = new GhBinaryLocator(runner, logger);
        var version = await locator.GetVersionAsync(path!);

        Assert.NotNull(version);
        Assert.True(SemVer.TryParse(version, out var parsed));
        Assert.True(parsed >= GhBinaryLocator.MinimumVersion,
            $"gh version {version} is below minimum {GhBinaryLocator.MinimumVersion}");
    }

    [Fact]
    public async Task GhSkillHelp_ContainsExpectedSubcommands()
    {
        if (!ShouldRun) return;
        var path = GhPath();
        Assert.NotNull(path);

        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        var result = await runner.RunAsync(path!, new[] { "skill", "--help" });

        Assert.True(result.Succeeded, $"gh skill --help exited with {result.ExitCode}");
        var output = result.StdOut;

        // Shape-level: these subcommands must appear in the help text.
        Assert.Contains("search", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("install", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preview", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GhSkillSearchHelp_MentionsJsonFlag()
    {
        if (!ShouldRun) return;
        var path = GhPath();
        Assert.NotNull(path);

        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        var result = await runner.RunAsync(path!, new[] { "skill", "search", "--help" });

        Assert.True(result.Succeeded, $"gh skill search --help exited with {result.ExitCode}");
        Assert.Contains("--json", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GhSkillInstallHelp_MentionsExpectedFlags()
    {
        if (!ShouldRun) return;
        var path = GhPath();
        Assert.NotNull(path);

        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        var result = await runner.RunAsync(path!, new[] { "skill", "install", "--help" });

        Assert.True(result.Succeeded, $"gh skill install --help exited {result.ExitCode}");
        var output = result.StdOut;

        Assert.Contains("--agent", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--scope", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GhSkillUpdateHelp_MentionsAllFlag()
    {
        if (!ShouldRun) return;
        var path = GhPath();
        Assert.NotNull(path);

        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        var result = await runner.RunAsync(path!, new[] { "skill", "update", "--help" });

        Assert.True(result.Succeeded, $"gh skill update --help exited {result.ExitCode}");
        Assert.Contains("--all", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapabilityProbe_ParsesRealHelpOutput()
    {
        if (!ShouldRun) return;
        var path = GhPath();
        Assert.NotNull(path);

        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);

        // Run the same help commands the capability probe uses.
        var searchHelp = await runner.RunAsync(path!, new[] { "skill", "search", "--help" });
        var installHelp = await runner.RunAsync(path!, new[] { "skill", "install", "--help" });
        var updateHelp = await runner.RunAsync(path!, new[] { "skill", "update", "--help" });

        // All should succeed.
        Assert.True(searchHelp.Succeeded);
        Assert.True(installHelp.Succeeded);
        Assert.True(updateHelp.Succeeded);

        // Parse with the real parser — verify it doesn't crash or return empty.
        var searchTokens = CapabilityProbeParser.ScanTokens(
            searchHelp.StdOut, CapabilityProbeParser.ProbedTokens["search"]);
        var installTokens = CapabilityProbeParser.ScanTokens(
            installHelp.StdOut, CapabilityProbeParser.ProbedTokens["install"]);
        var updateTokens = CapabilityProbeParser.ScanTokens(
            updateHelp.StdOut, CapabilityProbeParser.ProbedTokens["update"]);

        Assert.NotEmpty(searchTokens);
        Assert.NotEmpty(installTokens);
        Assert.NotEmpty(updateTokens);

        // Key flags should appear as parsed tokens.
        Assert.Contains("--json", searchTokens);
        Assert.Contains("--agent", installTokens);
        Assert.Contains("--all", updateTokens);
    }
}
