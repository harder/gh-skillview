using System.Text.Json;
using SkillView.Authoring;
using SkillView.Cli;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Authoring;

public sealed class AuthoringValidationTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/var/tmp", "skillview-authoring-" + Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(root, "repository");
    private string Payload => Path.Combine(Repository, "skills", "demo");
    private string Metadata => Path.Combine(Payload, "SKILL.md");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly List<string[]> calls = [];

    public AuthoringValidationTests()
    {
        Directory.CreateDirectory(Payload);
        Directory.CreateDirectory(Path.Combine(Repository, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "home"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        File.WriteAllText(Metadata, "---\nname: demo\ndescription: Fixture\n---\nAuthored text.\n");
        File.WriteAllText(Path.Combine(Payload, "never-run.sh"), "exit 88\n");
        File.WriteAllText(Path.Combine(Repository, ".git", "ignored-metadata"), "Git internals are outside byte coverage");
    }

    public void Dispose() => Directory.Delete(root, true);

    private ProcessRunner Runner() => new(new Logger(), environment: new Dictionary<string, string>
    {
        ["HOME"] = Path.Combine(root, "home"),
        ["USERPROFILE"] = Path.Combine(root, "home"),
        ["XDG_CONFIG_HOME"] = Path.Combine(root, "home", "config"),
        ["XDG_STATE_HOME"] = Path.Combine(root, "home", "state"),
        ["GH_CONFIG_DIR"] = Path.Combine(root, "home", "gh"),
        ["TMPDIR"] = Path.Combine(root, "tmp"),
        ["GIT_DIR"] = "",
        ["GIT_WORK_TREE"] = "",
        ["GIT_COMMON_DIR"] = "",
        ["GIT_CONFIG_COUNT"] = "0"
    }, defaultWorkingDirectory: root);

    private AuthoringValidationService Service(Func<ProcessRunner, string[], CancellationToken, Task<ProcessResult>>? publish = null) => new(Runner(), new Logger())
    {
        RunForTests = async (runner, executable, arguments, token) =>
        {
            var args = arguments.ToArray(); calls.Add(args);
            Assert.Equal("fake-gh", executable);
            Assert.Equal(Repository, runner.EffectiveWorkingDirectory);
            Assert.Equal(Path.Combine(root, "home"), runner.GetEffectiveEnvironmentVariable("HOME"));
            Assert.Equal(Path.Combine(root, "home", "gh"), runner.GetEffectiveEnvironmentVariable("GH_CONFIG_DIR"));
            Assert.Equal(Path.Combine(root, "tmp"), runner.GetEffectiveEnvironmentVariable("TMPDIR"));
            Assert.Equal("false", runner.GetEffectiveEnvironmentVariable("GH_TELEMETRY"));
            if (args.SequenceEqual(new[] { "--version" })) return Result(args, stdout: "gh version 2.102.0 (fixture)");
            if (args.SequenceEqual(new[] { "skill", "--help" })) return Result(args);
            Assert.Equal(new[] { "skill", "publish", "--dry-run", "--", Repository }, args);
            return publish is null ? Result(args) : await publish(runner, args, token);
        }
    };

    private static ProcessResult Result(string[] arguments, int code = 0, string stdout = "", string stderr = "") => new("fake-gh", arguments, code, stdout, stderr, TimeSpan.Zero) { Started = true };

    [Fact]
    public async Task ExplicitRepositoryUsesOnlyDryRunAndKeepsAssessmentsSeparate()
    {
        var parentHome = System.Environment.GetEnvironmentVariable("HOME");
        var authored = File.ReadAllBytes(Metadata);
        var result = await Service().ValidateAsync("repository", "fake-gh", Token);
        Assert.Equal("completed", result.Status);
        Assert.Equal("unchanged-observed-candidates", result.AuthoredBytes);
        Assert.Equal("bounded-candidates", result.DiscoveryCoverage);
        Assert.Single(result.Payloads); Assert.Equal(Payload, result.Payloads[0].Path);
        Assert.Equal(authored, File.ReadAllBytes(Metadata));
        Assert.Equal(parentHome, System.Environment.GetEnvironmentVariable("HOME"));
        Assert.Equal(3, calls.Count);
        using var json = JsonDocument.Parse(CliDispatcher.RenderAuthoringJson(result));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("not-reviewed", json.RootElement.GetProperty("assessments").GetProperty("sourceReviewed").GetString());
        Assert.Equal("unknown", json.RootElement.GetProperty("assessments").GetProperty("agentCompatibility").GetString());
        Assert.Equal("not-assessed", json.RootElement.GetProperty("assessments").GetProperty("safety").GetString());
    }

    [Fact]
    public async Task FailedStartAfterSuccessfulProbesRefusesWithoutClaimingExecutionOrPostCapture()
    {
        var result = await Service((_, args, _) => Task.FromResult(Result(args, -1, stderr: "executable no longer exists") with { Started = false }))
            .ValidateAsync(Repository, "fake-gh", Token);
        Assert.Equal(3, calls.Count);
        Assert.Equal("refused", result.Status);
        Assert.Equal("none", result.Execution);
        Assert.Equal("captured-before", result.AuthoredBytes);
        Assert.Equal(-1, result.CommandExitCode);
        Assert.Contains("executable no longer exists", result.StdErr);
        Assert.Contains(result.Diagnostics, item => item.Code == "gh-start-failed");
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "gh-command-failed");
        Assert.All(result.Payloads, payload => Assert.Null(payload.AfterFingerprint));
        using var json = JsonDocument.Parse(CliDispatcher.RenderAuthoringJson(result));
        Assert.Equal("none", json.RootElement.GetProperty("execution").GetString());
        Assert.Equal("unavailable", json.RootElement.GetProperty("assessments").GetProperty("format").GetString());
        Assert.Contains("gh-start-failed", CliDispatcher.RenderAuthoringText(result));
    }

    [Fact]
    public async Task GhFailureRetainsConcreteDiagnosticsWithoutRunningFix()
    {
        var result = await Service((_, args, _) => Task.FromResult(Result(args, 1, stderr: "demo: missing required field: description\n"))).ValidateAsync(Repository, "fake-gh", Token);
        Assert.Equal("failed", result.Status);
        Assert.Equal(1, result.CommandExitCode);
        Assert.Contains("missing required field", result.StdErr);
        Assert.Contains(result.Diagnostics, item => item.Code == "gh-command-failed");
        Assert.Equal("unchanged-observed-candidates", result.AuthoredBytes);
    }

    [Fact]
    public async Task UnexpectedAuthoredEditIsReportedAndNeverRepaired()
    {
        var result = await Service((_, args, _) =>
        {
            File.WriteAllText(Metadata, "Unexpected replacement\n");
            return Task.FromResult(Result(args));
        }).ValidateAsync(Repository, "fake-gh", Token);
        Assert.Equal("partial", result.Status); Assert.Equal("changed", result.AuthoredBytes);
        Assert.Contains(result.Diagnostics, item => item.Code == "authored-payload-changed");
        Assert.Equal("Unexpected replacement\n", File.ReadAllText(Metadata));
    }

    [Fact]
    public async Task NewCandidateIsDetectedDuringPostVerification()
    {
        var result = await Service((_, args, _) =>
        {
            Directory.CreateDirectory(Path.Combine(Repository, "other"));
            File.WriteAllText(Path.Combine(Repository, "other", "SKILL.md"), "new candidate");
            return Task.FromResult(Result(args));
        }).ValidateAsync(Repository, "fake-gh", Token);
        Assert.Equal("partial", result.Status); Assert.Equal("changed", result.AuthoredBytes);
        Assert.Contains(result.Diagnostics, item => item.Code == "candidate-set-changed");
    }

    [Fact]
    public async Task TruncatedCommandSuccessIsPartial()
    {
        var result = await Service((_, args, _) => Task.FromResult(Result(args) with { StdErrTruncated = true })).ValidateAsync(Repository, "fake-gh", Token);
        Assert.Equal("partial", result.Status); Assert.True(result.OutputTruncated);
        Assert.Contains(result.Diagnostics, item => item.Code == "output-truncated");
    }

    [Fact]
    public async Task InterruptedCommandStillChecksPayloadsAndRetainsOutput()
    {
        var result = await Service((_, args, _) => Task.FromResult(Result(args, 130, stderr: "incomplete diagnostics") with { Cancelled = true })).ValidateAsync(Repository, "fake-gh", Token);
        Assert.Equal("partial", result.Status); Assert.Equal("unchanged-observed-candidates", result.AuthoredBytes);
        Assert.Contains("incomplete diagnostics", result.StdErr);
    }

    [Fact]
    public async Task NoMatchingLayoutSkipsWithoutAnyCommand()
    {
        Directory.Delete(Path.Combine(Repository, "skills"), true);
        var result = await Service().ValidateAsync(Repository, "fake-gh", Token);
        Assert.Equal("skipped", result.Status); Assert.Equal("none", result.Execution); Assert.Empty(calls);
    }

    [Fact]
    public async Task RootMetadataShortCircuitIsRefusedInsteadOfPretendingChildrenWereValidated()
    {
        File.WriteAllText(Path.Combine(Repository, "SKILL.md"), "root");
        var result = await Service().ValidateAsync(Repository, "fake-gh", Token);
        Assert.Equal("refused", result.Status); Assert.Equal("none", result.Execution); Assert.Empty(calls);
        Assert.Contains(result.Diagnostics, item => item.Message.Contains("short-circuits", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LinkedMetadataIsRefusedBeforeGh()
    {
        if (OperatingSystem.IsWindows()) return;
        File.Delete(Metadata); File.CreateSymbolicLink(Metadata, Path.Combine(root, "outside"));
        var result = await Service().ValidateAsync(Repository, "fake-gh", Token);
        Assert.Equal("refused", result.Status); Assert.Empty(calls);
    }

    [Fact]
    public async Task DiscoveryBoundRefusesBeforeGh()
    {
        for (var index = 0; index <= AuthoringValidationService.MaxNamespaceEntries; index++) File.WriteAllText(Path.Combine(Repository, "entry" + index), "");
        var result = await Service().ValidateAsync(Repository, "fake-gh", Token);
        Assert.Equal("refused", result.Status); Assert.Empty(calls);
        Assert.Contains(result.Diagnostics, item => item.Message.Contains("8192", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--fix")]
    [InlineData("--tag")]
    [InlineData("--path")]
    [InlineData("--install-id")]
    public void InstalledSelectorsAndPublishingFlagsAreRejected(string option)
    {
        Assert.NotNull(CliDispatcher.ParseAuthoringArgs(["--repository", Repository, "--dry-run", option]).Error);
    }

    [Fact]
    public void ExplicitDryRunAndRepositoryAreMandatoryAndDuplicateFlagsAreRejected()
    {
        Assert.NotNull(CliDispatcher.ParseAuthoringArgs(["--repository", Repository]).Error);
        Assert.NotNull(CliDispatcher.ParseAuthoringArgs(["--dry-run"]).Error);
        Assert.NotNull(CliDispatcher.ParseAuthoringArgs(["--repository", Repository, "--dry-run", "--dry-run"]).Error);
        Assert.Null(CliDispatcher.ParseAuthoringArgs(["--repository", Repository, "--dry-run", "--json"]).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringProbeRetainsCallerMeaningAndNeverStartsDryRun(bool duringHelp)
    {
        using var canceled = new CancellationTokenSource();
        var service = new AuthoringValidationService(Runner(), new Logger())
        {
            RunForTests = (_, _, arguments, _) =>
            {
                if (duringHelp && arguments.SequenceEqual(new[] { "--version" }))
                    return Task.FromResult(Result(arguments.ToArray(), stdout: "gh version 2.102.0"));
                canceled.Cancel();
                return Task.FromResult(Result(arguments.ToArray(), 130) with { Cancelled = true });
            }
        };
        var report = await service.ValidateAsync(Repository, "fake-gh", canceled.Token);
        Assert.Equal("none", report.Execution);
        Assert.Contains(report.Diagnostics, item => item.Code == "canceled");
    }

    [Fact]
    public async Task PreCancellationDoesNotStartACommand()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ValidateAsync(Repository, "fake-gh", canceled.Token));
        Assert.Empty(calls);
    }

    [Fact]
    public void HumanDiagnosticsSanitizeTerminalEscapesWhileJsonPreservesEvidence()
    {
        var report = new AuthoringValidationReport(Repository, "failed", "gh-skill-publish-dry-run", "fake-gh", "2.102.0", 1,
            "\u001b[31mraw\u001b[0m", "", false, "bounded-candidates", "unknown", [], []);
        Assert.DoesNotContain("\u001b", CliDispatcher.RenderAuthoringText(report));
        using var json = JsonDocument.Parse(CliDispatcher.RenderAuthoringJson(report));
        Assert.Equal(report.StdOut, json.RootElement.GetProperty("stdout").GetString());
    }
}
