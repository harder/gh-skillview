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
        var version = await locator.GetVersionAsync(path!, TestContext.Current.CancellationToken);

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
        var result = await runner.RunAsync(path!, new[] { "skill", "--help" }, cancellationToken: TestContext.Current.CancellationToken);

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
        var result = await runner.RunAsync(path!, new[] { "skill", "search", "--help" }, cancellationToken: TestContext.Current.CancellationToken);

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
        var result = await runner.RunAsync(path!, new[] { "skill", "install", "--help" }, cancellationToken: TestContext.Current.CancellationToken);

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
        var result = await runner.RunAsync(path!, new[] { "skill", "update", "--help" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, $"gh skill update --help exited {result.ExitCode}");
        Assert.Contains("--all", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GhSkillListHelp_MentionsJsonFlag()
    {
        if (!ShouldRun) return;
        var path = GhPath();
        Assert.NotNull(path);

        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        var result = await runner.RunAsync(path!, new[] { "skill", "list", "--help" }, cancellationToken: TestContext.Current.CancellationToken);

        // gh ≥ 2.94 ships `gh skill list --json` — SkillView's primary inventory source.
        Assert.True(result.Succeeded, $"gh skill list --help exited {result.ExitCode}");
        Assert.Contains("--json", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }
}
