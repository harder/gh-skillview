using SkillView.Cli;
using Xunit;

namespace SkillView.Tests.Cli;

/// Phase 7 — argv parser surface for every CLI subcommand. These tests pin
/// the `--flag value` / `--flag=value` equivalence, positional ordering,
/// and the `owner/repo@ref` shorthand. They run without touching `gh` or
/// the filesystem because the parsers are pure over `IReadOnlyList<string>`.
public class CliDispatcherParserTests
{
    // --- list ------------------------------------------------------------

    [Fact]
    public void List_ParsesScopeAgentPathAndJson()
    {
        var opts = CliDispatcher.ParseListArgs(
            new[] { "--scope=user", "--agent", "claude", "--path=/p", "--json" },
            out var json);
        Assert.True(json);
        Assert.Equal("user", opts.scope);
        Assert.Equal("claude", opts.agent);
        Assert.Equal("/p", opts.path);
        Assert.Contains("/p", opts.scanRoots);
    }

    [Fact]
    public void List_AllowHiddenFlagAcceptedWithoutError()
    {
        var opts = CliDispatcher.ParseListArgs(
            new[] { "--allow-hidden-dirs" },
            out var json);
        Assert.False(json);
        Assert.Null(opts.scope);
    }

    // --- search ----------------------------------------------------------

    [Fact]
    public void Search_CollectsPositionalAsQuery()
    {
        var p = CliDispatcher.ParseSearchArgs(new[] { "render", "--owner=acme", "--limit", "30", "--json" });
        Assert.Equal("render", p.Query);
        Assert.Equal("acme", p.Owner);
        Assert.Equal(30, p.Limit);
        Assert.True(p.Json);
    }

    [Fact]
    public void Search_MissingQueryReturnsNullQuery()
    {
        var p = CliDispatcher.ParseSearchArgs(new[] { "--limit=5" });
        Assert.Null(p.Query);
        Assert.Equal(5, p.Limit);
    }

    // --- preview ---------------------------------------------------------

    [Fact]
    public void Preview_RepoAtRefShorthandExtractsVersion()
    {
        var p = CliDispatcher.ParsePreviewArgs(new[] { "acme/repo@v2.0.0", "render-md" });
        Assert.Equal("acme/repo", p.Repo);
        Assert.Equal("render-md", p.SkillName);
        Assert.Equal("v2.0.0", p.Version);
    }

    [Fact]
    public void Preview_ExplicitVersionWinsWhenNoShorthand()
    {
        var p = CliDispatcher.ParsePreviewArgs(new[] { "acme/repo", "--version", "main" });
        Assert.Equal("acme/repo", p.Repo);
        Assert.Equal("main", p.Version);
    }

    // --- install ---------------------------------------------------------

    [Fact]
    public void Install_MultipleAgentsAndAllFlags()
    {
        var p = CliDispatcher.ParseInstallArgs(new[]
        {
            "acme/repo@v1", "render-md",
            "--agent=claude", "--agent", "cursor",
            "--scope", "user", "--pin", "--force", "--from-local",
            "--upstream=https://git/example", "--repo-path=/skills",
            "--allow-hidden-dirs", "--json",
        });
        Assert.Equal("acme/repo", p.Repo);
        Assert.Equal("render-md", p.SkillName);
        Assert.Equal("v1", p.Version);
        Assert.Equal(new[] { "claude", "cursor" }, p.Agents);
        Assert.Equal("user", p.Scope);
        Assert.True(p.Pin);
        Assert.True(p.Force);
        Assert.True(p.FromLocal);
        Assert.True(p.AllowHiddenDirs);
        Assert.Equal("https://git/example", p.Upstream);
        Assert.Equal("/skills", p.RepoPath);
        Assert.True(p.Json);
    }

    [Fact]
    public void Install_MissingRepoYieldsNullRepo()
    {
        var p = CliDispatcher.ParseInstallArgs(new[] { "--agent=claude" });
        Assert.Null(p.Repo);
    }

    // --- update ----------------------------------------------------------

    [Fact]
    public void Update_AllFlagsAndPositionals()
    {
        var p = CliDispatcher.ParseUpdateArgs(new[]
        {
            "render-md", "fetch-url",
            "--all", "--dry-run", "--force", "--unpin",
            "--non-interactive", "--json",
        });
        Assert.Equal(new[] { "render-md", "fetch-url" }, p.Skills);
        Assert.True(p.All);
        Assert.True(p.DryRun);
        Assert.True(p.Force);
        Assert.True(p.Unpin);
        Assert.True(p.Yes); // --non-interactive maps to Yes
        Assert.True(p.Json);
    }

    // --- remove ----------------------------------------------------------

    [Fact]
    public void Remove_NameAndAgent()
    {
        var p = CliDispatcher.ParseRemoveArgs(new[] { "render-md", "--agent=claude", "--yes", "--json" });
        Assert.Equal("render-md", p.Name);
        Assert.Equal("claude", p.Agent);
        Assert.True(p.Yes);
        Assert.True(p.Json);
    }

    [Fact]
    public void Remove_MissingNameYieldsNull()
    {
        var p = CliDispatcher.ParseRemoveArgs(new[] { "--yes" });
        Assert.Null(p.Name);
        Assert.True(p.Yes);
    }

    // --- cleanup ---------------------------------------------------------

    [Fact]
    public void Cleanup_CandidateFilterIsCommaSplit()
    {
        var p = CliDispatcher.ParseCleanupArgs(new[] { "--candidates=Malformed,EmptyDirectory", "--apply", "--yes" });
        Assert.NotNull(p.KindFilter);
        Assert.Equal(new[] { "Malformed", "EmptyDirectory" }, p.KindFilter);
        Assert.True(p.Apply);
        Assert.True(p.Yes);
    }

    [Fact]
    public void Cleanup_OutputPathCaptured()
    {
        var p = CliDispatcher.ParseCleanupArgs(new[] { "--output", "/tmp/report.txt" });
        Assert.Equal("/tmp/report.txt", p.Output);
        Assert.False(p.Apply);
    }
}
