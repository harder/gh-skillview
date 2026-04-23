using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Cli;

/// Phase 7 — shape-level snapshot tests for each JSON-emitting subcommand
/// (§22 "snapshot tests on JSON output"). These tests assert the stable
/// top-level keys downstream scripts depend on. They don't compare full
/// document bytes — that would couple the tests to formatting — but they
/// do parse the rendered JSON and check the contract.
public class CliDispatcherJsonSnapshotTests
{
    private static AppOptions DefaultOptions() => new(
        InvocationMode.Standalone,
        DispatchMode.Cli,
        Debug: false,
        ScanRoots: Array.Empty<string>(),
        SubcommandName: "doctor",
        SubcommandArgs: new[] { "--json" });

    private static EnvironmentReport SampleReport() => new()
    {
        GhPath = "/usr/bin/gh",
        GhVersionRaw = "gh version 2.91.0",
        GhVersion = new SemVer(2, 91, 0),
        GhMeetsMinimum = true,
        Auth = GhAuthStatus.Unknown,
        Capabilities = CapabilityProfile.Empty with { SkillSubcommandPresent = true },
        LogDirectory = "/tmp/skillview-logs",
    };

    private static InstalledSkill Skill(string name, string path = "/p/a") => new()
    {
        Name = name,
        ResolvedPath = path,
        ScanRoot = "/p",
        Scope = Scope.User,
        Agents = ImmutableArray<AgentMembership>.Empty,
        FrontMatter = new SkillFrontMatter { Name = name, Version = "1.0", Description = "d" },
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null,
    };

    private static InventorySnapshot Snapshot(params InstalledSkill[] skills) => new()
    {
        Skills = skills.ToImmutableArray(),
        ScannedRoots = ImmutableArray.Create(new ScanRoot("/p", Scope.User, null)),
        UsedGhSkillList = false,
        CapturedAt = DateTimeOffset.UnixEpoch,
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // --- doctor ----------------------------------------------------------

    [Fact]
    public void Doctor_JsonHasExpectedTopLevelKeys()
    {
        var doc = Parse(CliDispatcher.RenderDoctorJson(SampleReport(), DefaultOptions()));
        Assert.Equal("/usr/bin/gh", doc.GetProperty("ghPath").GetString());
        Assert.True(doc.GetProperty("ghMeetsMinimum").GetBoolean());
        Assert.True(doc.GetProperty("baselineOk").GetBoolean());
        Assert.True(doc.TryGetProperty("auth", out _));
        Assert.True(doc.TryGetProperty("capabilities", out _));
        Assert.True(doc.TryGetProperty("scanRoots", out _));
    }

    // --- list ------------------------------------------------------------

    [Fact]
    public void List_JsonSerializesSkillsArray()
    {
        var snap = Snapshot(Skill("a"), Skill("b", "/p/b"));
        var doc = Parse(CliDispatcher.RenderListJson(snap, CapabilityProfile.Empty));
        Assert.True(doc.GetProperty("scannedRoots").GetArrayLength() == 1);
        var skills = doc.GetProperty("skills");
        Assert.Equal(2, skills.GetArrayLength());
        Assert.Equal("a", skills[0].GetProperty("name").GetString());
        Assert.Equal("/p/a", skills[0].GetProperty("resolvedPath").GetString());
        Assert.False(skills[0].GetProperty("ignored").GetBoolean());
    }

    // --- search ----------------------------------------------------------

    [Fact]
    public void Search_JsonEmitsResultsArray()
    {
        var parsed = new CliDispatcher.ParsedSearchArgs("render", "acme", 10, 1, Json: true);
        var rows = new[]
        {
            new SearchResultSkill(
                Description: "d",
                Namespace: "acme",
                Path: "skills/render-md",
                Repo: "acme/render",
                SkillName: "render-md",
                Stars: 42),
        };
        var doc = Parse(CliDispatcher.RenderSearchJson(rows, parsed));
        Assert.Equal("render", doc.GetProperty("query").GetString());
        Assert.Equal(10, doc.GetProperty("limit").GetInt32());
        var results = doc.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("render-md", results[0].GetProperty("skillName").GetString());
        Assert.Equal(42, results[0].GetProperty("stars").GetInt32());
    }

    // --- preview ---------------------------------------------------------

    [Fact]
    public void Preview_JsonEmitsMarkdownAndAssociatedFiles()
    {
        var preview = new PreviewResult
        {
            Repo = "acme/render",
            SkillName = "render-md",
            Version = "v2",
            Body = "# hello\nbody",
            MarkdownBody = "# hello",
            AssociatedFiles = ImmutableArray.Create("LICENSE", "README.md"),
            Succeeded = true,
            ExitCode = 0,
            ErrorMessage = null,
        };
        var doc = Parse(CliDispatcher.RenderPreviewJson(preview));
        Assert.Equal("acme/render", doc.GetProperty("repo").GetString());
        Assert.Equal("# hello", doc.GetProperty("markdown").GetString());
        Assert.Equal(2, doc.GetProperty("associatedFiles").GetArrayLength());
    }

    // --- install ---------------------------------------------------------

    [Fact]
    public void Install_JsonReportsAddedDiff()
    {
        var r = new InstallResult
        {
            Repo = "acme/render",
            SkillName = "render-md",
            Version = "v1",
            Succeeded = true,
            ExitCode = 0,
            StdOut = "",
            StdErr = "",
            ErrorMessage = null,
            CommandLine = new[] { "gh", "skill", "install" },
        };
        var p = new CliDispatcher.ParsedInstallArgs(
            Repo: "acme/render",
            SkillName: "render-md",
            Version: "v1",
            Agents: new List<string> { "claude" },
            Scope: "user",
            Path: null,
            Pin: true,
            Force: false,
            Upstream: null,
            RepoPath: null,
            FromLocal: false,
            AllowHiddenDirs: false,
            Json: true);
        var added = new[] { Skill("render-md") };
        var doc = Parse(CliDispatcher.RenderInstallJson(r, p, added));
        Assert.True(doc.GetProperty("succeeded").GetBoolean());
        Assert.Equal("render-md", doc.GetProperty("skillName").GetString());
        Assert.True(doc.GetProperty("pin").GetBoolean());
        Assert.Equal(1, doc.GetProperty("added").GetArrayLength());
    }

    // --- update ----------------------------------------------------------

    [Fact]
    public void Update_JsonReportsEntriesAndChangedDiff()
    {
        var r = new UpdateResult
        {
            DryRun = false,
            Succeeded = true,
            ExitCode = 0,
            StdOut = "",
            StdErr = "",
            ErrorMessage = null,
            CommandLine = new[] { "gh", "skill", "update" },
            Entries = ImmutableArray.Create(new UpdateEntry
            {
                Name = "render-md",
                FromVersion = "v1",
                ToVersion = "v2",
                Status = "updated",
            }),
        };
        var p = new CliDispatcher.ParsedUpdateArgs(
            Skills: new List<string>(),
            All: true,
            DryRun: false,
            Force: false,
            Unpin: false,
            Yes: true,
            Json: true);
        var changed = new[]
        {
            new CliDispatcher.UpdateDiffEntry("render-md", "/p/render-md", "v1", "v2", "sha1", "sha2"),
        };
        var doc = Parse(CliDispatcher.RenderUpdateJson(r, p, Array.Empty<InstalledSkill>(), changed));
        Assert.True(doc.GetProperty("succeeded").GetBoolean());
        Assert.True(doc.GetProperty("all").GetBoolean());
        Assert.Equal(1, doc.GetProperty("entries").GetArrayLength());
        Assert.Equal("updated", doc.GetProperty("entries")[0].GetProperty("status").GetString());
        Assert.Equal(1, doc.GetProperty("changed").GetArrayLength());
        Assert.Equal("sha2", doc.GetProperty("changed")[0].GetProperty("toSha").GetString());
    }

    // --- remove ----------------------------------------------------------

    [Fact]
    public void Remove_JsonIncludesValidationErrorsAndWarnings()
    {
        var validation = new RemoveValidator.RemoveValidation(
            Errors: ImmutableArray.Create(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.ContainsGitDirectory, ".git found")),
            Warnings: ImmutableArray<RemoveValidator.Warning>.Empty,
            ResolvedPath: "/p/a",
            IncomingSymlinkPaths: ImmutableArray<string>.Empty);
        var report = new RemoveService.RemoveReport(
            Succeeded: false,
            ResolvedPath: "/p/a",
            FilesDeleted: 0,
            DirectoriesDeleted: 0,
            Errors: ImmutableArray<string>.Empty,
            DryRun: false);
        var p = new CliDispatcher.ParsedRemoveArgs("a", null, Yes: false, Json: true);
        var doc = Parse(CliDispatcher.RenderRemoveJson(report, Skill("a"), p, validation));
        Assert.False(doc.GetProperty("allowed").GetBoolean());
        Assert.False(doc.GetProperty("succeeded").GetBoolean());
        Assert.Equal(1, doc.GetProperty("errors").GetArrayLength());
        Assert.Equal("ContainsGitDirectory",
            doc.GetProperty("errors")[0].GetProperty("kind").GetString());
        Assert.Equal(0, doc.GetProperty("warnings").GetArrayLength());
    }

    // --- cleanup ---------------------------------------------------------

    [Fact]
    public void Cleanup_JsonEntriesReflectCandidates()
    {
        var candidate = new CleanupClassifier.Candidate(
            CleanupClassifier.CandidateKind.Malformed,
            "/p/bad",
            "no SKILL.md",
            Skill: null);
        var p = new CliDispatcher.ParsedCleanupArgs(
            KindFilter: null,
            Apply: false,
            Yes: false,
            Json: true,
            Output: null);
        var doc = Parse(CliDispatcher.RenderCleanupJson(
            new[] { candidate },
            Array.Empty<(CleanupClassifier.Candidate, RemoveService.RemoveReport)>(),
            p));
        Assert.Equal(1, doc.GetProperty("candidates").GetInt32());
        var entries = doc.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("Malformed", entries[0].GetProperty("kind").GetString());
        Assert.Equal("/p/bad", entries[0].GetProperty("path").GetString());
        Assert.False(doc.GetProperty("apply").GetBoolean());
    }

    [Fact]
    public void CleanupReport_HeaderAndKindsRendered()
    {
        var candidate = new CleanupClassifier.Candidate(
            CleanupClassifier.CandidateKind.EmptyDirectory,
            "/p/empty",
            "directory is empty",
            Skill: null);
        var text = CliDispatcher.RenderCleanupReport(new[] { candidate });
        Assert.Contains("SkillView cleanup report", text);
        Assert.Contains("candidates: 1", text);
        Assert.Contains("EmptyDirectory", text);
        Assert.Contains("/p/empty", text);
    }
}
