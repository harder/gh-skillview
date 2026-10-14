using System.Diagnostics;
using System.Text.Json;
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
    public async Task GhInstall_PinnedMetadataAndFilesystemExposureRoundTrip()
    {
        if (!ShouldRun) return;
        var ghPath = GhPath();
        Assert.NotNull(ghPath);
        var root = Directory.CreateTempSubdirectory("skillview-metadata-contract-");
        try
        {
            var logger = new Logger();
            var runner = new ProcessRunner(logger, environment:
                SkillView.Maintenance.SkillUpdateCoordinator.CreateStagingEnvironment(Path.Combine(root.FullName, ".fixture")));
            var result = await runner.RunAsync(ghPath!,
                ["skill", "install", "--dir", root.FullName, "--pin=v2.102.0", "--", "cli/cli", "skills/gh-skill"],
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.StdErr);
            var scanner = new SkillView.Inventory.LocalSkillScanner(logger);
            var skill = Assert.Single(scanner.Scan([new SkillView.Inventory.Models.ScanRoot(root.FullName,
                SkillView.Inventory.Models.Scope.Custom, null)]));
            Assert.Equal(SkillView.Inventory.Models.ValidityState.Valid, skill.Validity);
            Assert.True(skill.Pinned);
            Assert.Equal("v2.102.0", skill.FrontMatter.PinnedRef);
            Assert.Equal("https://github.com/cli/cli", skill.FrontMatter.Upstream);
            Assert.False(string.IsNullOrWhiteSpace(skill.TreeSha));
            Assert.Equal("skills/gh-skill", skill.FrontMatter.GithubPath);
            var capture = await new GhSkillListAdapter(runner, logger).CaptureAsync(ghPath!,
                directory: root.FullName, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(capture.Succeeded, capture.Error);
            Assert.Single(capture.Records);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task GhUpdate_SelectedPhysicalCopyChanges_OtherCopyAndLocalExtrasAreProtected()
    {
        if (!ShouldRun) return;
        var ghPath = GhPath();
        Assert.NotNull(ghPath);
        var root = Directory.CreateTempSubdirectory("skillview-exact-update-contract-");
        try
        {
            var logger = new Logger();
            var runner = new ProcessRunner(logger, environment:
                SkillView.Maintenance.SkillUpdateCoordinator.CreateStagingEnvironment(Path.Combine(root.FullName, ".fixture")));
            var installer = new GhSkillInstallService(runner, logger);
            var roots = new[] { Path.Combine(root.FullName, "first"), Path.Combine(root.FullName, "second") };
            foreach (var directory in roots)
            {
                var installed = await installer.InstallAsync(ghPath!, "cli/cli", "skills/gh-skill",
                    new GhSkillInstallService.Options(Path: directory, Version: "33789149b9def6b66ddf247438a79fac94e22074", Pin: true),
                    TestContext.Current.CancellationToken);
                Assert.True(installed.Succeeded, installed.StdErr);
            }
            var skills = new SkillView.Inventory.LocalSkillScanner(logger).Scan(roots.Select(directory =>
                new SkillView.Inventory.Models.ScanRoot(directory, SkillView.Inventory.Models.Scope.Custom, null)).ToArray());
            Assert.Equal(2, skills.Length);
            var selected = skills.Single(skill => SkillView.Inventory.PathIdentity.Equals(skill.ScanRoot, roots[0]));
            var other = skills.Single(skill => skill.InstallationId != selected.InstallationId);
            var otherBefore = SkillView.Maintenance.PayloadSnapshot.Capture(other.ResolvedPath, TestContext.Current.CancellationToken);
            var coordinator = new SkillView.Maintenance.SkillUpdateCoordinator(installer,
                new GhSkillUpdateService(runner, logger), new GhSkillListAdapter(runner, logger), logger, runner);
            var result = await coordinator.UpdateAsync(ghPath!, new GhSkillUpdateService.Options(
                Targets: [selected], Unpin: true), TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.ErrorMessage + " " + string.Join("; ", result.Outcomes.Select(o => o.Message)));
            Assert.Equal("changed", Assert.Single(result.Outcomes).Status);
            Assert.Contains("Updated gh-skill", result.StdOut);
            var otherAfter = SkillView.Maintenance.PayloadSnapshot.Capture(other.ResolvedPath, TestContext.Current.CancellationToken);
            Assert.Equal(otherBefore.Fingerprint, otherAfter.Fingerprint);
            File.WriteAllText(Path.Combine(other.ResolvedPath, "custom.txt"), "preserve");
            var customBefore = SkillView.Maintenance.PayloadSnapshot.Capture(other.ResolvedPath, TestContext.Current.CancellationToken);
            var refused = await coordinator.UpdateAsync(ghPath!, new GhSkillUpdateService.Options(
                Targets: [other], Force: true, Unpin: true), TestContext.Current.CancellationToken);
            Assert.False(refused.Succeeded);
            Assert.False(refused.MutationAttempted);
            Assert.Contains("Local changes protected", Assert.Single(refused.Outcomes).Message);
            Assert.Equal(customBefore.Fingerprint,
                SkillView.Maintenance.PayloadSnapshot.Capture(other.ResolvedPath, TestContext.Current.CancellationToken).Fingerprint);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task GhUpdate_MovedBranchRef_ReconstructsRecordedImmutableBase()
    {
        if (!ShouldRun) return;
        var ghPath = GhPath();
        Assert.NotNull(ghPath);
        var root = Directory.CreateTempSubdirectory("skillview-recorded-base-contract-");
        try
        {
            var logger = new Logger();
            var runner = new ProcessRunner(logger, environment:
                SkillView.Maintenance.SkillUpdateCoordinator.CreateStagingEnvironment(Path.Combine(root.FullName, ".fixture")));
            var installer = new GhSkillInstallService(runner, logger);
            var installed = await installer.InstallAsync(ghPath!, "cli/cli", "skills/gh-skill",
                new GhSkillInstallService.Options(Path: root.FullName,
                    Version: "33789149b9def6b66ddf247438a79fac94e22074", Pin: true), TestContext.Current.CancellationToken);
            Assert.True(installed.Succeeded, installed.StdErr);
            var skillPath = Directory.EnumerateDirectories(root.FullName)
                .Single(directory => Path.GetFileName(directory) != ".fixture");
            var markdown = Path.Combine(skillPath, "SKILL.md");
            var text = File.ReadAllText(markdown);
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"(?m)^([ \t]+github-ref:).*$", "$1 refs/heads/trunk");
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"(?m)^[ \t]+github-pinned:.*\n", "");
            File.WriteAllText(markdown, text);
            var target = Assert.Single(new SkillView.Inventory.LocalSkillScanner(logger).Scan(
                [new SkillView.Inventory.Models.ScanRoot(root.FullName, SkillView.Inventory.Models.Scope.Custom, null)]));
            Assert.False(target.Pinned);
            Assert.Equal("refs/heads/trunk", target.FrontMatter.GithubRef);
            var result = await new SkillView.Maintenance.SkillUpdateCoordinator(installer,
                new GhSkillUpdateService(runner, logger), new GhSkillListAdapter(runner, logger), logger, runner)
                .UpdateAsync(ghPath!, new GhSkillUpdateService.Options(Targets: [target]), TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.ErrorMessage + " " + string.Join("; ", result.Outcomes.Select(o => o.Message)));
            Assert.Equal("changed", Assert.Single(result.Outcomes).Status);
            Assert.Contains("Updated gh-skill", result.StdOut);
        }
        finally { root.Delete(true); }
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
        Assert.Contains("devin", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grok", output, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task GhSkillListJson_UsesExpectedFieldSetAndReturnsArray()
    {
        if (!ShouldRun) return;
        var path = GhPath();
        Assert.NotNull(path);

        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        var result = await runner.RunAsync(path!, new[]
        {
            "skill", "list", "--json",
            "skillName,agentHosts,path,pinned,scope,sourceURL,version",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, $"gh skill list --json exited {result.ExitCode}: {result.StdErr}");
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
    }
}
