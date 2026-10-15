using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Cli;

/// Phase 9 — error-path and edge-case tests for CliDispatcher parsers and
/// render helpers. These cover invalid input, boundary conditions, and
/// under-tested argument combinations.
public class CliDispatcherErrorTests : IDisposable
{
    private readonly string _tempRoot;
    private string? _backupStateOverride;

    public CliDispatcherErrorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "skillview-cli-err-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // --- remove parser edge cases ----------------------------------------

    [Fact]
    public void ParseRemoveArgs_NoName_ReturnsNull()
    {
        var args = CliDispatcher.ParseRemoveArgs(new[] { "--yes", "--json" });
        Assert.Null(args.Name);
    }

    [Fact]
    public void ParseRemoveArgs_NameAndAllFlags()
    {
        var args = CliDispatcher.ParseRemoveArgs(new[] { "my-skill", "--agent=claude", "--yes", "--json" });
        Assert.Equal("my-skill", args.Name);
        Assert.Equal("claude", args.Agent);
        Assert.True(args.Yes);
        Assert.True(args.Json);
    }

    [Fact]
    public void ParseRemoveArgs_AgentWithoutValue_SkipsGracefully()
    {
        // --agent at end with no value — should not crash
        var args = CliDispatcher.ParseRemoveArgs(new[] { "skill-name", "--agent" });
        Assert.Equal("skill-name", args.Name);
        Assert.Null(args.Agent);
    }

    // --- cleanup parser edge cases ---------------------------------------

    [Fact]
    public void ParseCleanupArgs_ApplyWithoutYes()
    {
        var args = CliDispatcher.ParseCleanupArgs(new[] { "--apply" });
        Assert.True(args.Apply);
        Assert.False(args.Yes);
    }

    [Fact]
    public void ParseCleanupArgs_CandidatesFilter()
    {
        var args = CliDispatcher.ParseCleanupArgs(new[] { "--candidates", "Malformed,BrokenSymlink" });
        Assert.NotNull(args.KindFilter);
        Assert.Equal(2, args.KindFilter!.Count);
        Assert.Contains("Malformed", args.KindFilter);
        Assert.Contains("BrokenSymlink", args.KindFilter);
    }

    [Fact]
    public void ParseCleanupArgs_OutputPath()
    {
        var args = CliDispatcher.ParseCleanupArgs(new[] { "--output=/tmp/report.md" });
        Assert.Equal("/tmp/report.md", args.Output);
    }

    [Fact]
    public void ParseCleanupArgs_OutputPathSeparateArg()
    {
        var args = CliDispatcher.ParseCleanupArgs(new[] { "--output", "/tmp/report.md" });
        Assert.Equal("/tmp/report.md", args.Output);
    }

    [Fact]
    public async Task CleanupApply_RemovesBrokenSymlinkCandidate()
    {
        var brokenLink = Path.Combine(_tempRoot, "broken-link");
        Directory.CreateSymbolicLink(brokenLink, Path.Combine(_tempRoot, "missing-target"));

        var (exitCode, stdout, stderr) = await RunCliAsync(
            "skillview",
            "--scan-root", _tempRoot,
            "cleanup", "--candidates", "BrokenSymlink", "--apply", "--yes", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("\"apply\": true", stdout);
        Assert.Contains("\"kind\": \"BrokenSymlink\"", stdout);
        Assert.Contains("\"succeeded\": true", stdout);
        Assert.False(PathResolver.IsSymlink(brokenLink));
        using var result = JsonDocument.Parse(stdout);
        var impact = Assert.Single(result.RootElement.GetProperty("applied").EnumerateArray()).GetProperty("impact");
        Assert.Equal(brokenLink, impact.GetProperty("targetPath").GetString());
        Assert.False(impact.GetProperty("payloadRetained").GetBoolean());
        Assert.Equal(JsonValueKind.Null, impact.GetProperty("retainedPayloadPath").ValueKind);
        Assert.Single(impact.GetProperty("affectedExposures").EnumerateArray());
        Assert.True(string.IsNullOrWhiteSpace(stderr));
    }

    [Fact]
    public async Task QuarantinePreviewReportsPayloadRetainedAtOriginalPathWithoutMutation()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempRoot, "quarantine-skills")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(root, "demo")).FullName;
        const string markdown = "---\nname: demo\ndescription: Test\n---\nPayload\n";
        File.WriteAllText(Path.Combine(target, "SKILL.md"), markdown);
        var (code, output, error) = await RunCliAsync("skillview", "--scan-root", root, "quarantine", "--path", target, "--json");
        Assert.Equal(ExitCodes.Success, code); Assert.Equal("", error);
        using var result = JsonDocument.Parse(output);
        var operation = Assert.Single(result.RootElement.GetProperty("operations").EnumerateArray());
        Assert.Equal("preview", operation.GetProperty("status").GetString());
        var impact = operation.GetProperty("impact");
        Assert.True(impact.GetProperty("payloadRetained").GetBoolean());
        Assert.Equal(OwnershipPath.CanonicalCandidate(target), impact.GetProperty("retainedPayloadPath").GetString());
        Assert.Contains("preview", impact.GetProperty("retentionEvidence").GetString());
        Assert.Equal(markdown, File.ReadAllText(Path.Combine(target, "SKILL.md")));
        Assert.False(Directory.Exists(operation.GetProperty("retainedPath").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackupJsonCapturesExactSelectionWithoutChangingEitherCopy(bool relativeSelector)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempRoot, "backup-skills")).FullName;
        var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        const string markdown = "---\nname: duplicate\ndescription: Test\n---\nPayload\n";
        File.WriteAllText(Path.Combine(first, "SKILL.md"), markdown);
        File.WriteAllText(Path.Combine(second, "SKILL.md"), markdown);
        File.WriteAllBytes(Path.Combine(first, "resource.bin"), [0, 255, 42]);
        var before = SkillView.Maintenance.PayloadSnapshot.Capture(first, TestContext.Current.CancellationToken);
        var (code, output, error) = await RunCliAsync("skillview", "--scan-root", root, "backup", "--path", relativeSelector ? Path.GetRelativePath(_tempRoot, first) : first, "--json");
        Assert.Equal(ExitCodes.Success, code);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
        using var result = JsonDocument.Parse(output);
        Assert.True(result.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal(before.Fingerprint, result.RootElement.GetProperty("fingerprint").GetString());
        Assert.True(File.Exists(result.RootElement.GetProperty("archivePath").GetString()));
        Assert.Equal(before.Fingerprint, SkillView.Maintenance.PayloadSnapshot.Capture(first, TestContext.Current.CancellationToken).Fingerprint);
        Assert.Equal(markdown, File.ReadAllText(Path.Combine(second, "SKILL.md")));
        Assert.Equal(["SKILL.md"], Directory.EnumerateFileSystemEntries(second).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("--path", "one", "--path", "two")]
    [InlineData("--path", "one", "--install-id", "id")]
    [InlineData("--install-id", "one", "--install-id", "two")]
    [InlineData("--path", "--json")]
    [InlineData("--name", "duplicate")]
    public async Task BackupRejectsAmbiguousOrIncompleteSelectorsBeforeCreatingState(params string[] arguments)
    {
        var (code, _, _) = await RunCliAsync("skillview", ["backup", .. arguments]);
        Assert.Equal(ExitCodes.InvalidUsage, code);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "fixture-home", "maintenance-state")));
    }

    [Fact]
    public async Task HistoryJsonVerifiesExactRecordReportsCorruptionAndDoesNotAlterPayload()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempRoot, "history-skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(root, "demo")).FullName;
        const string markdown = "---\nname: demo\ndescription: Test\n---\nPayload\n";
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), markdown);
        var (backupCode, backupOutput, _) = await RunCliAsync("skillview", "--scan-root", root, "backup", "--path", payload, "--json");
        Assert.Equal(ExitCodes.Success, backupCode);
        using var backup = JsonDocument.Parse(backupOutput);
        var id = backup.RootElement.GetProperty("id").GetString()!;
        var archive = backup.RootElement.GetProperty("archivePath").GetString()!;
        var (code, output, error) = await RunCliAsync("skillview", "history", "--id", id, "--json");
        Assert.Equal(ExitCodes.Success, code); Assert.Equal("", error);
        using var history = JsonDocument.Parse(output);
        var entry = Assert.Single(history.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal(id, entry.GetProperty("id").GetString()); Assert.Equal("verified", entry.GetProperty("status").GetString());
        Assert.Equal(1, entry.GetProperty("payloadEntries").GetInt32());
        var (missingCode, missingOutput, _) = await RunCliAsync("skillview", "history", "--id", new string('0', 32), "--json");
        Assert.Equal(ExitCodes.NoMatches, missingCode);
        using var missing = JsonDocument.Parse(missingOutput); Assert.Empty(missing.RootElement.GetProperty("entries").EnumerateArray());
        File.WriteAllBytes(archive, [0, 1]);
        var (corruptCode, corruptOutput, _) = await RunCliAsync("skillview", "history", "--json");
        Assert.Equal(ExitCodes.UserError, corruptCode);
        using var corrupt = JsonDocument.Parse(corruptOutput);
        Assert.Equal("corrupt", Assert.Single(corrupt.RootElement.GetProperty("entries").EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal(markdown, File.ReadAllText(Path.Combine(payload, "SKILL.md")));
    }

    [Theory]
    [InlineData("--id")]
    [InlineData("--id", "../escape")]
    [InlineData("--id", "00000000000000000000000000000000", "--id", "00000000000000000000000000000000")]
    [InlineData("--apply")]
    public async Task HistoryInvalidArgumentsDoNotCreateState(params string[] arguments)
    {
        var (code, _, _) = await RunCliAsync("skillview", ["history", .. arguments]);
        Assert.Equal(ExitCodes.InvalidUsage, code);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "fixture-home", "maintenance-state")));
    }

    [Fact]
    public async Task HistoryMissingStateReportsRefusalWithoutCreatingIt()
    {
        var (code, output, _) = await RunCliAsync("skillview", "history", "--json");
        Assert.Equal(ExitCodes.EnvironmentError, code);
        using var result = JsonDocument.Parse(output); Assert.False(result.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "fixture-home", "maintenance-state")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveGitfileRefusesBothPreviewAndExecutionWithoutDeletingFiles(bool execute)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempRoot, "gitfile-skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(root, "gitfile-skill")).FullName;
        const string markdown = "---\nname: gitfile-skill\ndescription: Fixture\n---\nBody\n";
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), markdown);
        const string marker = "gitdir: ../repository/.git/worktrees/gitfile-skill\n";
        File.WriteAllText(Path.Combine(payload, ".git"), marker);
        var arguments = new List<string> { "--scan-root", root, "remove", "gitfile-skill", "--json" };
        if (execute) arguments.Add("--yes");
        var (code, output, _) = await RunCliAsync("skillview", arguments.ToArray());
        Assert.Equal(ExitCodes.UserError, code);
        using var result = JsonDocument.Parse(output);
        Assert.False(result.RootElement.GetProperty("allowed").GetBoolean());
        Assert.Contains(result.RootElement.GetProperty("errors").EnumerateArray(), row => row.GetProperty("kind").GetString() == "ContainsGitDirectory");
        Assert.Equal(0, result.RootElement.GetProperty("filesDeleted").GetInt32());
        Assert.Empty(result.RootElement.GetProperty("runtimeErrors").EnumerateArray());
        Assert.Equal(markdown, File.ReadAllText(Path.Combine(payload, "SKILL.md")));
        Assert.Equal(marker, File.ReadAllText(Path.Combine(payload, ".git")));
    }

    [Theory]
    [InlineData("universal")]
    [InlineData("claude")]
    [InlineData("pi")]
    [InlineData("workspace")]
    [InlineData("profile")]
    public async Task BackupRefusesStateInsideInitiallyMissingConfiguredAgentRoots(string context)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempRoot, "backup-skills")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(root, "demo")).FullName;
        File.WriteAllText(Path.Combine(target, "SKILL.md"), "---\nname: demo\ndescription: Test\n---\nBody\n");
        var home = Path.Combine(_tempRoot, "fixture-home");
        var workspace = Directory.CreateDirectory(Path.Combine(_tempRoot, "workspace")).FullName;
        var agentRoot = context switch
        {
            "universal" => Path.Combine(home, ".agents", "skills"),
            "claude" => Path.Combine(home, "claude", "skills"),
            "pi" => Path.Combine(home, "pi", "skills"),
            "workspace" => Path.Combine(workspace, ".github", "skills"),
            _ => Path.Combine(_tempRoot, "future-profile")
        };
        _backupStateOverride = agentRoot;
        Assert.False(Directory.Exists(agentRoot));
        string[] extra = context == "workspace" ? ["--workspace", workspace]
            : context == "profile" ? ["--profile", "claude:work=" + agentRoot] : [];
        var (code, output, _) = await RunCliAsync("skillview", ["--scan-root", root, .. extra, "backup", "--path", target, "--json"]);
        Assert.Equal(ExitCodes.EnvironmentError, code);
        using var result = JsonDocument.Parse(output);
        Assert.False(result.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.False(Directory.Exists(agentRoot));
        Assert.Equal(["SKILL.md"], Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName));
    }

    [Fact]
    public async Task BackupRefusesIncompleteConfiguredRootCoverageBeforeCreatingState()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempRoot, "backup-skills")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(root, "demo")).FullName;
        File.WriteAllText(Path.Combine(target, "SKILL.md"), "---\nname: demo\ndescription: Test\n---\nBody\n");
        var roots = new List<string> { "--scan-root", root };
        for (var i = 0; i < 256; i++) roots.AddRange(["--scan-root", Path.Combine(_tempRoot, "missing-" + i)]);
        var (code, output, _) = await RunCliAsync("skillview", [.. roots, "backup", "--path", target, "--json"]);
        Assert.Equal(ExitCodes.EnvironmentError, code);
        using var result = JsonDocument.Parse(output);
        Assert.False(result.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "fixture-home", "maintenance-state")));
    }

    [Theory]
    [InlineData("quarantine", "")]
    [InlineData("quarantine", "--path demo --yes")]
    [InlineData("quarantine", "--path demo --yes --standalone")]
    [InlineData("quarantine", "--path demo --yes --standalone --expect invalid")]
    [InlineData("quarantine", "--path first --path second")]
    [InlineData("quarantine", "--path first --install-id second")]
    [InlineData("undo", "")]
    [InlineData("undo", "--id ../outside --yes")]
    [InlineData("quarantine-history", "--yes")]
    [InlineData("restore-plan", "")]
    [InlineData("restore-plan", "--path demo --yes")]
    [InlineData("restore-plan", "--path first --install-id second")]
    [InlineData("restore-inspect", "--id ../outside")]
    [InlineData("restore-apply", "--id 00000000000000000000000000000000 --yes --expect bad")]
    [InlineData("restore-apply", "--id 00000000000000000000000000000000 --yes --disposition overwrite --expect 0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("restore-recover", "--id 00000000000000000000000000000000 --yes")]
    [InlineData("restore-undo", "--id 00000000000000000000000000000000 --expect bad")]
    [InlineData("update-plan", "")]
    [InlineData("update-plan", "--path demo --yes")]
    [InlineData("update-plan", "--path demo --unpin --unpin")]
    [InlineData("update-inspect", "--id ../outside")]
    [InlineData("update-apply", "--id 00000000000000000000000000000000 --yes --expect bad")]
    [InlineData("update-recover", "--id 00000000000000000000000000000000 --yes")]
    [InlineData("update-undo", "--id 00000000000000000000000000000000 --expect bad")]
    [InlineData("update-repair-owner", "--id 00000000000000000000000000000000 --yes")]
    [InlineData("update-repair-owner", "--id 00000000000000000000000000000000 --expect 0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("update-repair-owner", "--id 00000000000000000000000000000000 --yes --expect bad")]
    [InlineData("update-repair-owner", "--id ../outside --yes --expect 0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task QuarantineInvalidConfirmationOrSelectorIsRejectedBeforeInventory(string command, string arguments)
    {
        var argv = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (code, _, error) = await RunCliAsync("skillview", [command, .. argv]);
        Assert.Equal(ExitCodes.InvalidUsage, code);
        Assert.Contains("skillview:", error);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "fixture-home", "maintenance-state")));
    }

    [Fact]
    public async Task RestoreAcceptsExplicitHiddenInventoryScanFlag()
    {
        var (code, output, _) = await RunCliAsync("skillview", ["restore-inspect", "--id", new string('0', 32), "--allow-hidden-dirs", "--json"]);
        Assert.Equal(ExitCodes.UserError, code);
        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.GetProperty("succeeded").GetBoolean());
    }

    // --- update parser edge cases ----------------------------------------

    [Fact]
    public void ParseUpdateArgs_NoSkillsNoFlags_EmptyResult()
    {
        var args = CliDispatcher.ParseUpdateArgs(Array.Empty<string>());
        Assert.Empty(args.Skills);
        Assert.False(args.All);
        Assert.False(args.DryRun);
        Assert.False(args.Force);
        Assert.False(args.Unpin);
        Assert.False(args.Json);
    }

    [Fact]
    public void ParseUpdateArgs_MultipleSkills()
    {
        var args = CliDispatcher.ParseUpdateArgs(new[] { "skill-a", "skill-b", "--dry-run" });
        Assert.Equal(2, args.Skills.Count);
        Assert.Contains("skill-a", args.Skills);
        Assert.Contains("skill-b", args.Skills);
        Assert.True(args.DryRun);
    }

    [Fact]
    public void ParseUpdateArgs_AllIsNonInteractive()
    {
        // gh 2.94 `--all` updates without prompting; `--yes` is accepted but
        // ignored (not forwarded to gh, which has no such flag).
        var args = CliDispatcher.ParseUpdateArgs(new[] { "--all", "--yes" });
        Assert.True(args.All);
    }

    // --- search parser edge cases ----------------------------------------

    [Fact]
    public void ParseSearchArgs_LimitNonNumeric_DefaultsToNull()
    {
        var args = CliDispatcher.ParseSearchArgs(new[] { "query", "--limit", "abc" });
        Assert.Equal("query", args.Query);
        Assert.Null(args.Limit);
    }

    [Fact]
    public void ParseSearchArgs_PageSupport()
    {
        var args = CliDispatcher.ParseSearchArgs(new[] { "query", "--page", "3" });
        Assert.Equal("query", args.Query);
        Assert.Equal(3, args.Page);
    }

    // --- preview parser edge cases ---------------------------------------

    [Fact]
    public void ParsePreviewArgs_RepoAtRef()
    {
        var args = CliDispatcher.ParsePreviewArgs(new[] { "owner/repo@v2.0.0" });
        Assert.Equal("owner/repo", args.Repo);
        Assert.Equal("v2.0.0", args.Version);
    }

    [Fact]
    public void ParsePreviewArgs_ExplicitVersionOverridesAtRef()
    {
        var args = CliDispatcher.ParsePreviewArgs(new[] { "owner/repo@v1.0", "--version", "v3.0" });
        Assert.Equal("v3.0", args.Version);
    }

    [Fact]
    public void ParsePreviewArgs_NoRepo_ReturnsNull()
    {
        var args = CliDispatcher.ParsePreviewArgs(new[] { "--json" });
        Assert.Null(args.Repo);
        Assert.True(args.Json);
    }

    // --- render helpers --------------------------------------------------

    [Fact]
    public void RenderCleanupReport_EmptyCandidates_ContainsZeroCount()
    {
        var report = CliDispatcher.RenderCleanupReport(Array.Empty<CleanupClassifier.Candidate>());
        Assert.Contains("candidates: 0", report);
    }

    [Fact]
    public void RenderCleanupReport_WithCandidates_ListsAllKindsAndPaths()
    {
        var candidates = new[]
        {
            new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.Malformed,
                "/path/a",
                "reason a",
                null),
            new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.BrokenSymlink,
                "/path/b",
                "reason b",
                null),
        };
        var report = CliDispatcher.RenderCleanupReport(candidates);
        Assert.Contains("candidates: 2", report);
        Assert.Contains("/path/a", report);
        Assert.Contains("/path/b", report);
        Assert.Contains("Malformed", report);
        Assert.Contains("BrokenSymlink", report);
    }

    [Fact]
    public void RenderCleanupJson_NoApply_OmitsAppliedArray()
    {
        var candidates = ImmutableArray<CleanupClassifier.Candidate>.Empty;
        var applied = Array.Empty<(CleanupClassifier.Candidate, RemoveService.RemoveReport)>();
        var parsed = new CliDispatcher.ParsedCleanupArgs(null, Apply: false, Yes: false, Json: true, Output: null);
        var json = CliDispatcher.RenderCleanupJson(candidates, applied, parsed);
        Assert.Contains("\"apply\": false", json);
        Assert.DoesNotContain("\"applied\"", json);
    }

    [Fact]
    public void RenderCleanupJson_WithApply_IncludesAppliedArray()
    {
        var candidates = ImmutableArray<CleanupClassifier.Candidate>.Empty;
        var applied = new[]
        {
            (new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.EmptyDirectory,
                "/empty",
                "empty dir",
                null),
            new RemoveService.RemoveReport(true, "/empty", 0, 1,
                ImmutableArray<string>.Empty, false)),
        };
        var parsed = new CliDispatcher.ParsedCleanupArgs(null, Apply: true, Yes: true, Json: true, Output: null);
        var json = CliDispatcher.RenderCleanupJson(candidates, applied, parsed);
        Assert.Contains("\"apply\": true", json);
        Assert.Contains("\"applied\"", json);
    }

    [Fact]
    public void RenderCleanupJson_WithDuplicateSkip_ReportsSkipSeparately()
    {
        var unique = new CleanupClassifier.Candidate(
            CleanupClassifier.CandidateKind.SourceOrphaned,
            "/duplicate",
            "not in gh inventory",
            null);
        var duplicate = unique with
        {
            Kind = CleanupClassifier.CandidateKind.Duplicate,
            Reason = "same skill name",
        };
        var applied = new[]
        {
            (unique, new RemoveService.RemoveReport(
                true,
                unique.Path,
                0,
                1,
                ImmutableArray<string>.Empty,
                false)),
        };
        var parsed = new CliDispatcher.ParsedCleanupArgs(
            null,
            Apply: true,
            Yes: true,
            Json: true,
            Output: null);

        using var document = JsonDocument.Parse(CliDispatcher.RenderCleanupJson(
            [unique, duplicate],
            applied,
            parsed,
            [duplicate]));

        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("skippedCandidates").GetInt32());
        Assert.Single(root.GetProperty("applied").EnumerateArray());
        var skipped = Assert.Single(root.GetProperty("skipped").EnumerateArray());
        Assert.Equal("/duplicate", skipped.GetProperty("path").GetString());
        Assert.Equal("Duplicate", skipped.GetProperty("kind").GetString());
        Assert.Equal("duplicate candidate path", skipped.GetProperty("reason").GetString());
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(string processPath, params string[] args)
    {
        await CliConsoleCapture.Gate.WaitAsync().ConfigureAwait(false);
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        Console.SetOut(stdoutWriter);
        Console.SetError(stderrWriter);

        try
        {
            var options = ArgParser.Parse(processPath, args);
            var home = Directory.CreateDirectory(Path.Combine(_tempRoot, "fixture-home")).FullName;
            var services = TuiServices.Build(new Logger(LogLevel.Info), processEnvironment: new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
                ["SKILLVIEW_STATE_HOME"] = _backupStateOverride ?? Path.Combine(home, "maintenance-state"),
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude"),
                ["PI_CODING_AGENT_DIR"] = Path.Combine(home, "pi"),
                ["GIT_DIR"] = Path.Combine(_tempRoot, ".git"),
                ["GIT_WORK_TREE"] = _tempRoot,
                ["GIT_COMMON_DIR"] = Path.Combine(_tempRoot, ".git"),
                ["GIT_CONFIG_COUNT"] = "0"
            }, workingDirectory: _tempRoot);
            var exitCode = await CliDispatcher.RunAsync(options, services).ConfigureAwait(false);
            return (exitCode, stdoutWriter.ToString(), stderrWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            CliConsoleCapture.Gate.Release();
        }
    }
}
