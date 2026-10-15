using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using System.Text.Json;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class InventoryFilterTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-filter-");
    public void Dispose() => temporary.Delete(true);

    [Theory]
    [InlineData("user", "codex", true)]
    [InlineData("project", "claude", true)]
    [InlineData("project", "claude-code", true)]
    [InlineData("user", "claude-code", false)]
    [InlineData("project", "codex", false)]
    [InlineData("user", null, true)]
    [InlineData(null, "codex", true)]
    [InlineData(null, "claude", true)]
    [InlineData("custom", null, true)]
    [InlineData("custom", "codex", false)]
    public async Task CombinedFiltersMatchOneExposureAndRetainEveryConsumer(string? scope, string? agent, bool matches)
    {
        var home = Directory.CreateDirectory(Path.Combine(temporary.FullName, "home")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temporary.FullName, "project")).FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var current = Directory.CreateDirectory(Path.Combine(project, "packages", "app")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "payloads")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(root, "demo")).FullName;
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: demo\ndescription: Shared exposure fixture\n---\nBody\n");
        foreach (var parent in new[] { Path.Combine(home, ".codex", "skills"), Path.Combine(project, ".claude", "skills") })
        {
            Directory.CreateDirectory(parent);
            Directory.CreateSymbolicLink(Path.Combine(parent, "demo"), payload);
        }
        var alias = Path.Combine(temporary.FullName, "payload-alias");
        Directory.CreateSymbolicLink(alias, root);
        var logger = new Logger();
        // No Git or gh subprocess is needed for this filesystem topology.
        // Capture still uses the same explicit HOME/CWD as the subprocess owner.
        var runner = new ProcessRunner(logger, defaultWorkingDirectory: current, environment: new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["PATH"] = "",
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
            ["PI_CODING_AGENT_DIR"] = Path.Combine(home, ".pi", "agent")
        });
        var service = new LocalInventoryService(new(), new(logger), new GhSkillListAdapter(runner, logger), logger, runner);
        var options = new LocalInventoryService.Options([root, alias], true,
            Profiles: [new("codex", alias, "alias-profile")]);
        var baseline = Assert.Single((await service.CaptureAsync(null, options, TestContext.Current.CancellationToken)).Skills);
        Assert.Contains(baseline.Agents, a => a.AgentId == "codex" && a.Scope == Scope.User);
        Assert.Contains(baseline.Agents, a => a.AgentId == "claude-code" && a.Scope == Scope.Project);
        Assert.Contains(baseline.Agents, a => a.Profile == "alias-profile");
        Assert.Contains(baseline.Agents, a => PathIdentity.Equals(a.Path, Path.Combine(alias, "demo")));
        var filtered = await service.CaptureAsync(null, options with { FilterScope = scope, FilterAgent = agent },
            TestContext.Current.CancellationToken);
        if (!matches) Assert.Empty(filtered.Skills);
        else
        {
            var selected = Assert.Single(filtered.Skills);
            Assert.Equal(baseline.InstallationId, selected.InstallationId);
            Assert.Equal(baseline.Agents.ToArray(), selected.Agents.ToArray());
        }
    }

    [Fact]
    public async Task GhObservationsOutsideTheFilterKeepConsumersAndConflictingSources()
    {
        var home = Directory.CreateDirectory(Path.Combine(temporary.FullName, "home")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(root, "demo")).FullName;
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: demo\ndescription: Provider observation fixture\n---\nBody\n");
        var logger = new Logger();
        var runner = new ProcessRunner(logger, defaultWorkingDirectory: temporary.FullName, environment: new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["PATH"] = "",
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
            ["PI_CODING_AGENT_DIR"] = Path.Combine(home, ".pi", "agent")
        });
        var response = JsonSerializer.Serialize(new[]
        {
            new { skillName = "demo", agentHosts = new[] { "codex" }, scope = "user", path = payload, sourceURL = "https://github.com/team/a" },
            new { skillName = "demo", agentHosts = new[] { "claude-code" }, scope = "project", path = payload, sourceURL = "https://github.com/team/b" },
            new { skillName = "demo", agentHosts = new[] { "gemini-cli" }, scope = "user", path = payload, sourceURL = "https://github.com/team/b" }
        });
        var defaultCalls = 0;
        var adapter = new GhSkillListAdapter((executable, arguments, token) =>
        {
            token.ThrowIfCancellationRequested();
            var output = "[]";
            if (!arguments.Contains("--dir"))
            {
                defaultCalls++;
                Assert.DoesNotContain("--scope", arguments);
                Assert.DoesNotContain("--agent", arguments);
                output = response;
            }
            return Task.FromResult(new ProcessResult(executable, arguments, 0, output, "", TimeSpan.Zero));
        }, logger);
        var service = new LocalInventoryService(new(), new(logger), adapter, logger, runner);
        var options = new LocalInventoryService.Options([root], true, FilterScope: "user", FilterAgent: "codex");
        var selected = Assert.Single((await service.CaptureAsync("protocol-fixture", options, TestContext.Current.CancellationToken)).Skills);
        Assert.Equal(1, defaultCalls);
        Assert.Contains(selected.Agents, a => a.AgentId == "codex" && a.Scope == Scope.User);
        Assert.Contains(selected.Agents, a => a.AgentId == "claude-code" && a.Scope == Scope.Project);
        Assert.Contains(selected.Agents, a => a.AgentId == "gemini-cli" && a.Scope == Scope.User);
        Assert.Contains(selected.MetadataConflicts, conflict => conflict.Contains("team/a") && conflict.Contains("team/b"));
        using var json = JsonDocument.Parse(SkillView.Cli.CliDispatcher.RenderListJson(
            await service.CaptureAsync("protocol-fixture", options with { FilterAgent = "gemini" }, TestContext.Current.CancellationToken)));
        Assert.Equal(1, defaultCalls); // The unfiltered catalog remains reusable.
        var row = json.RootElement.GetProperty("skills")[0];
        Assert.Equal(selected.InstallationId, row.GetProperty("installationId").GetString());
        Assert.Contains(row.GetProperty("agents").EnumerateArray(), a => a.GetProperty("id").GetString() == "gemini-cli");
        Assert.Contains(row.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetString() is { } value && value.Contains("team/a") && value.Contains("team/b"));
    }

    [Fact]
    public void LegacyUnscopedMembershipUsesDeclaredScopeWithoutOverridingKnownExposureScope()
    {
        var legacy = MinimalSkill() with { Scope = Scope.User, Agents = [new("codex", "/legacy", false)] };
        Assert.True(LocalInventoryService.MatchesFilters(legacy, Scope.User, "codex"));
        Assert.False(LocalInventoryService.MatchesFilters(legacy, Scope.Project, "codex"));
        var knownProject = legacy with { Agents = [new("codex", "/project", false, Scope.Project)] };
        Assert.False(LocalInventoryService.MatchesFilters(knownProject, Scope.User, "codex"));
        Assert.True(LocalInventoryService.MatchesFilters(knownProject, Scope.Project, "codex"));
    }

    [Fact]
    public void AgentlessCustomInstallationKeepsScopeOnlyFiltering()
    {
        var skill = MinimalSkill();
        Assert.True(LocalInventoryService.MatchesFilters(skill, Scope.Custom, null));
        Assert.False(LocalInventoryService.MatchesFilters(skill, Scope.User, null));
        Assert.False(LocalInventoryService.MatchesFilters(skill, Scope.Custom, "codex"));
    }

    private static InstalledSkill MinimalSkill() => new()
    {
        Name = "demo",
        ResolvedPath = "/fixture/demo",
        ScanRoot = "/fixture",
        Scope = Scope.Custom,
        Agents = [],
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };
}
