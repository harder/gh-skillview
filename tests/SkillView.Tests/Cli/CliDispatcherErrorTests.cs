using System.Collections.Immutable;
using System.IO;
using SkillView.Cli;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Cli;

/// Phase 9 — error-path and edge-case tests for CliDispatcher parsers and
/// render helpers. These cover invalid input, boundary conditions, and
/// under-tested argument combinations.
public class CliDispatcherErrorTests : IDisposable
{
    private readonly string _tempRoot;

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
        Assert.False(args.Yes);
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
    public void ParseUpdateArgs_AllWithYes()
    {
        var args = CliDispatcher.ParseUpdateArgs(new[] { "--all", "--yes" });
        Assert.True(args.All);
        Assert.True(args.Yes);
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
}
