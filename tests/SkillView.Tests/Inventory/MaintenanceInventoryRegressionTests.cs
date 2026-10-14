using System.Collections.Immutable;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class MaintenanceInventoryRegressionTests
{
    [Fact]
    public void CurrentGhMetadata_FoldedDescriptionAndToolPatternsSurvive()
    {
        const string content = """
            ---
            name: demo
            description: >-
              Review code
              safely
            allowed-tools: Read Bash(git log:*)
            metadata:
              github-repo: https://github.com/team/repo
              github-ref: v1.2.0
              github-tree-sha: abc123
              github-path: skills/author/demo
              github-pinned: v1.2.0
              version: "1.0"
              vendor/example: keep
            ---
            # Instructions
            """;
        var (body, fm, parsed) = FrontMatterParser.Parse(content);
        Assert.True(parsed);
        Assert.Empty(fm.Diagnostics);
        Assert.Equal("Review code safely", fm.Description);
        Assert.Equal("https://github.com/team/repo", fm.Upstream);
        Assert.Equal("abc123", fm.GithubTreeSha);
        Assert.Equal("skills/author/demo", fm.GithubPath);
        Assert.Equal("v1.2.0", fm.PinnedRef);
        Assert.Equal("1.0", fm.Version);
        Assert.True(fm.Pinned);
        Assert.Equal(["Read", "Bash(git log:*)"], fm.AllowedTools);
        Assert.Equal("keep", fm.Metadata["vendor/example"]);
        Assert.Contains("Instructions", body);
    }

    [Theory]
    [InlineData("name: demo\nname: other")]
    [InlineData("name: [invalid")]
    [InlineData("name: &a demo\ndescription: *a")]
    public void InvalidOrAliasedYaml_IsNotReportedAsParsed(string yaml)
    {
        Assert.False(FrontMatterParser.Parse($"---\n{yaml}\n---\nbody").Parsed);
    }

    [Fact]
    public void MetadataValidation_IsDistinctFromYamlParsing()
    {
        var (_, fm, parsed) = FrontMatterParser.Parse("---\nname: demo\n---\nbody");
        Assert.True(parsed);
        Assert.Contains(fm.Diagnostics, text => text.Contains("description", StringComparison.Ordinal));
    }

    [Fact]
    public void ComplexityAndPrefixLimits_AreExplicit()
    {
        var huge = "description: " + new string('x', FrontMatterParser.MaxYamlChars);
        Assert.False(FrontMatterParser.Parse($"---\n{huge}\n---\n").Parsed);
        var nested = "value: " + new string('[', 30) + "x" + new string(']', 30);
        Assert.False(FrontMatterParser.Parse($"---\n{nested}\n---\n").Parsed);
    }

    [Fact]
    public void SharedInstall_MergesAllContextsOnceAndRetainsPinAndRepositoryRef()
    {
        var local = Skill("demo", Path.Combine(Path.GetTempPath(), "shared-demo")) with
        {
            Agents = [new AgentMembership("claude", "/user/demo", true, Scope.User)],
            FrontMatter = new SkillFrontMatter { Name = "demo", Version = "authored-1" },
        };
        var record = new GhSkillListRecord
        {
            Name = "demo",
            Path = local.ResolvedPath,
            Scope = "project",
            Pinned = true,
            SourceUrl = "https://github.com/team/repo",
            Version = "v2.0",
            Hosts = ["codex"],
        };
        var result = Assert.Single(LocalInventoryService.Merge([local], [record, record]));
        Assert.True(result.Pinned);
        Assert.Equal("authored-1", result.FrontMatter.Version);
        Assert.Equal("v2.0", result.FrontMatter.GithubRef);
        Assert.Equal(record.SourceUrl, result.FrontMatter.Upstream);
        Assert.Equal(2, result.Agents.Length);
        Assert.Contains(result.Agents, a => a.AgentId == "claude-code" && a.Scope == Scope.User);
        Assert.Contains(result.Agents, a => a.AgentId == "codex" && a.Scope == Scope.Project);
        Assert.Empty(CleanupClassifier.Classify(Snapshot(result), []));
    }

    [Fact]
    public void ConflictingTracking_IsVisibleInsteadOfSilentlyOverwritten()
    {
        var local = Skill("demo", "/skills/demo") with
        { FrontMatter = new SkillFrontMatter { Upstream = "https://github.com/team/a" } };
        var cli = new GhSkillListRecord { Name = "demo", Path = "/skills/demo", SourceUrl = "https://github.com/team/b" };
        var result = Assert.Single(LocalInventoryService.Merge([local], [cli]));
        Assert.Single(result.MetadataConflicts);
        Assert.Contains(CleanupClassifier.Classify(Snapshot(result), []), c => c.ReviewOnly && c.Kind == CleanupClassifier.CandidateKind.MetadataConflict);
    }

    [Fact]
    public void SameNameDifferentContextOrPin_HasNoAutomaticSurvivor()
    {
        var a = Skill("demo", "/user/demo");
        var b = Skill("demo", "/project/demo") with
        { Scope = Scope.Project, FrontMatter = new SkillFrontMatter { Pinned = true, PinnedRef = "v1" } };
        var candidates = CleanupClassifier.Classify(Snapshot(a, b), []);
        Assert.Equal(2, candidates.Length);
        Assert.All(candidates, c => { Assert.True(c.ReviewOnly); Assert.Equal(CleanupClassifier.CandidateKind.NameCollision, c.Kind); });
        Assert.DoesNotContain(candidates, c => c.Kind == CleanupClassifier.CandidateKind.Duplicate);
    }

    [Fact]
    public void FailedCatalog_DoesNotEstablishAbsence()
    {
        var bad = GhSkillListAdapter.ParseLoadResult("{broken");
        Assert.False(bad.ShouldCache);
        Assert.NotNull(bad.Error);
        var empty = GhSkillListAdapter.ParseLoadResult("[]");
        Assert.True(empty.ShouldCache);
        Assert.Null(empty.Error);
        Assert.Empty(CleanupClassifier.Classify(Snapshot(Skill("demo", "/custom/demo")) with
        { UsedGhSkillList = false, Providers = [new ProviderObservation("gh", false, [], DateTimeOffset.UtcNow, "failed")] }, []));
    }

    [Theory]
    [InlineData("description: null", "description")]
    [InlineData("description: false", "description")]
    [InlineData("description: good\nmetadata: []", "Metadata")]
    [InlineData("description: good\nmetadata: { github-pinned: false }", "github-pinned")]
    public void YamlTypes_DoNotInventRequiredStringsOrPins(string fragment, string diagnostic)
    {
        var (_, fm, parsed) = FrontMatterParser.Parse($"---\nname: demo\n{fragment}\n---\n");
        Assert.True(parsed);
        Assert.False(fm.Pinned);
        Assert.Contains(fm.Diagnostics, text => text.Contains(diagnostic, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RefNormalization_DoesNotReportExpectedGhShortening()
    {
        var local = Skill("demo", "/skills/demo") with
        { FrontMatter = new SkillFrontMatter { GithubRef = "refs/heads/main" } };
        var result = Assert.Single(LocalInventoryService.Merge([local],
            [new GhSkillListRecord { Name = "demo", Path = "/skills/demo", Version = "main" }]));
        Assert.Equal("refs/heads/main", result.FrontMatter.GithubRef);
        Assert.Empty(result.MetadataConflicts);
        var cliOnly = Assert.Single(LocalInventoryService.Merge([], [new GhSkillListRecord
        { Name = "demo", Path = "/skills/demo", Version = "v2" }]));
        Assert.Equal("v2", cliOnly.FrontMatter.GithubRef);
        Assert.Null(cliOnly.FrontMatter.Version);
    }

    [Fact]
    public void NamespacedSelector_IsExpectedButDifferentFrontmatterNamesConflict()
    {
        var local = Skill("demo", "/skills/author/demo");
        var namespaced = new GhSkillListRecord { Name = "author/demo", Path = local.ResolvedPath };
        Assert.Empty(Assert.Single(LocalInventoryService.Merge([local], [namespaced])).MetadataConflicts);
        Assert.Contains(Assert.Single(LocalInventoryService.Merge([local], [namespaced with { Name = "different" }])).MetadataConflicts,
            text => text.Contains("names", StringComparison.Ordinal));
    }

    [Fact]
    public void SuccessfulButDifferentCoverage_DoesNotEstablishOrphaning()
    {
        var snapshot = Snapshot(Skill("demo", "/custom/demo")) with
        {
            UsedGhSkillList = true,
            Providers = [new ProviderObservation("gh", true, ["/other"], DateTimeOffset.UtcNow)],
        };
        Assert.Empty(CleanupClassifier.Classify(snapshot, []));
        var covered = CleanupClassifier.Classify(snapshot with
        { Providers = [new ProviderObservation("gh", true, ["/custom"], DateTimeOffset.UtcNow)] }, []);
        Assert.True(Assert.Single(covered).ReviewOnly);
    }

    [Fact]
    public void ListJson_IncludesProviderAndFilesystemFailuresAndReviewOnlyCandidates()
    {
        var snapshot = Snapshot() with
        {
            Providers = [new ProviderObservation("gh", false, [], DateTimeOffset.UtcNow, "failed")],
            Diagnostics = new ScanDiagnostics { InaccessiblePaths = ["/denied"] },
        };
        using var document = System.Text.Json.JsonDocument.Parse(SkillView.Cli.CliDispatcher.RenderListJson(snapshot));
        Assert.True(document.RootElement.GetProperty("partial").GetBoolean());
        Assert.Equal("/denied", document.RootElement.GetProperty("inaccessiblePaths")[0].GetString());
        var candidate = new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.NameCollision,
            "/demo", "compare", null, ReviewOnly: true);
        using var cleanup = System.Text.Json.JsonDocument.Parse(SkillView.Cli.CliDispatcher.RenderCleanupJson(
            [candidate], [], new SkillView.Cli.CliDispatcher.ParsedCleanupArgs(null, false, false, true, null)));
        Assert.True(cleanup.RootElement.GetProperty("entries")[0].GetProperty("reviewOnly").GetBoolean());
    }

    private static InstalledSkill Skill(string name, string path) => new()
    {
        Name = name,
        ResolvedPath = path,
        ScanRoot = "/skills",
        Scope = Scope.User,
        Agents = [],
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null,
    };

    private static InventorySnapshot Snapshot(params InstalledSkill[] skills) => new()
    { Skills = skills.ToImmutableArray(), ScannedRoots = [], UsedGhSkillList = false, CapturedAt = DateTimeOffset.UtcNow };
}
