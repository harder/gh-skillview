using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Loading;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Loading;

public sealed class LoadingDiscoveryTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-loading-roots-");
    private readonly Logger logger = new();
    private string Home => Path.Combine(temporary.FullName, "home");
    private string Project => Path.Combine(temporary.FullName, "project");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temporary.Delete(true);
    private string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
    }
    private string Skill(string path, string name = "demo")
    {
        Write(Path.Combine(path, "SKILL.md"), $"---\nname: {name}\ndescription: Loading fixture\n---\nNever execute.\n"); return path;
    }
    private ProcessRunner Runner(IReadOnlyDictionary<string, string>? extra = null)
    {
        Directory.CreateDirectory(Home); Directory.CreateDirectory(Project);
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = Home,
            ["USERPROFILE"] = Home,
            ["PATH"] = "",
            ["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config"),
            ["XDG_STATE_HOME"] = Path.Combine(Home, "state"),
            ["CODEX_HOME"] = "",
            ["CLAUDE_CONFIG_DIR"] = "",
            ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = "",
            ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
            ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
            ["OPENCODE_CONFIG"] = "",
            ["OPENCODE_CONFIG_DIR"] = "",
            ["OPENCODE_CONFIG_CONTENT"] = "",
            ["OPENCODE_PERMISSION"] = "",
            ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "false",
            ["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "false",
            ["OPENCODE_DISABLE_CLAUDE_CODE"] = "false",
            ["OPENCODE_DISABLE_CLAUDE_CODE_SKILLS"] = "false"
        };
        foreach (var pair in extra ?? ImmutableDictionary<string, string>.Empty) environment[pair.Key] = pair.Value;
        return new(logger, environment: environment, defaultWorkingDirectory: Project);
    }
    private CapturedLoadingConfiguration Capture(IReadOnlyDictionary<string, string>? environment = null) =>
        new LoadingConfigurationCapture(Runner(environment), Path.Combine(temporary.FullName, "system"))
            .Capture(new("opencode", ProjectDirectory: Project), Token);
    private async Task<EffectiveSkillAnalysis> Inventory(string agent, ProcessRunner runner, string? config = null)
    {
        var service = new LocalInventoryService(new(), new(logger), new GhSkillListAdapter(runner, logger), logger, runner);
        return await new EffectiveSkillLoader(runner).CaptureInventoryAsync(service, null,
            new(agent, ProjectDirectory: Project, ConfigDirectory: config), [], [], [], Token);
    }

    [Fact]
    public void OptionalMissingRootsAreDistinctFromUninspectableOrNonDirectoryRoots()
    {
        var observation = LoadingDiscoveryCapture.Probe(["missing", "absent-parent", "denied", "io", "file", "directory"], Token, path => path switch
        {
            "missing" => throw new FileNotFoundException(),
            "absent-parent" => throw new DirectoryNotFoundException(),
            "denied" => throw new UnauthorizedAccessException(),
            "io" => throw new IOException(),
            "file" => FileAttributes.Normal,
            _ => FileAttributes.Directory
        });
        Assert.Equal(["directory"], observation.Roots);
        Assert.Equal(["denied", "io", "file"], observation.Failures.Select(failure => failure.Path));
        Assert.True(LoadingDiscoveryCapture.Probe(["missing"], Token, _ => throw new FileNotFoundException()).Apply(InventorySnapshot.Empty).ConfiguredRootsComplete);
        var incomplete = observation.Apply(InventorySnapshot.Empty);
        Assert.False(incomplete.ConfiguredRootsComplete); Assert.True(incomplete.IsPartial);
        Assert.Equal(3, incomplete.Diagnostics.InaccessiblePaths.Length);
    }

    [Fact]
    public void RootProbeIsBoundedAndCancellationDoesNotTurnIntoAbsence()
    {
        var calls = 0;
        var observation = LoadingDiscoveryCapture.Probe(Enumerable.Range(0, 300).Select(index => index.ToString()), Token,
            _ => { calls++; return FileAttributes.Directory; });
        Assert.Equal(256, calls); Assert.Single(observation.Failures);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => LoadingDiscoveryCapture.Probe(["invalid\0"], canceled.Token, _ => throw new Exception("must not inspect")));
    }

    [Theory]
    [InlineData("OPENCODE_DISABLE_EXTERNAL_SKILLS", true, true)]
    [InlineData("OPENCODE_DISABLE_CLAUDE_CODE", false, true)]
    [InlineData("OPENCODE_DISABLE_CLAUDE_CODE_SKILLS", false, true)]
    public void DisableFlagsExcludeOnlyTheirNativeCompatibilityRoots(string key, bool agentsExcluded, bool claudeExcluded)
    {
        var baseline = Capture();
        var disabled = Capture(new Dictionary<string, string> { [key] = "true" });
        Assert.NotEqual(baseline.Context.ContextId, disabled.Context.ContextId);
        Assert.All(disabled.DiscoveryRoots.Where(root => root.Placement == "external-agents"), root => Assert.Equal(agentsExcluded ? "excluded" : "applied", root.Applicability));
        Assert.All(disabled.DiscoveryRoots.Where(root => root.Placement == "external-claude"), root => Assert.Equal(claudeExcluded ? "excluded" : "applied", root.Applicability));
        Assert.All(disabled.DiscoveryRoots.Where(root => root.Placement == "config-directory"), root => Assert.Equal("applied", root.Applicability));
    }

    [Theory]
    [InlineData("true", "excluded")]
    [InlineData("yes", "excluded")]
    [InlineData("on", "excluded")]
    [InlineData("1", "excluded")]
    [InlineData("y", "excluded")]
    [InlineData("false", "applied")]
    [InlineData("no", "applied")]
    [InlineData("off", "applied")]
    [InlineData("0", "applied")]
    [InlineData("n", "applied")]
    [InlineData("", "conditional")]
    [InlineData("TRUE", "conditional")]
    [InlineData(" false ", "conditional")]
    public void ExternalDisableFlagUsesExactEffectBooleanTokens(string raw, string state)
    {
        var capture = Capture(new Dictionary<string, string> { ["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = raw });
        Assert.All(capture.DiscoveryRoots.Where(root => root.Placement.StartsWith("external-", StringComparison.Ordinal)), root => Assert.Equal(state, root.Applicability));
        Assert.Equal(state == "conditional", !capture.Settings.LocalPolicyBlockers.IsEmpty);
    }

    [Fact]
    public void ConfiguredPathsReplaceLowerArraysResolveFromCwdAndKeepProviderHiddenRules()
    {
        Write(Path.Combine(Home, ".config", "opencode", "opencode.json"), "{\"skills\":{\"paths\":[\"lower\"]}}");
        Write(Path.Combine(Project, "opencode.json"), "{\"skills\":{\"paths\":[\"relative\",\"~/extra\",\"~//double\",\"\"]}}");
        var custom = Path.Combine(temporary.FullName, "custom-config");
        var capture = Capture(new Dictionary<string, string> { ["OPENCODE_CONFIG_DIR"] = custom });
        var paths = capture.DiscoveryRoots.Where(root => root.Placement == "configured-path").ToArray();
        Assert.Equal(4, paths.Length);
        Assert.Equal(new[] { Path.Combine(Project, "relative"), Path.Combine(Home, "extra"), Path.Combine(Home, "double"), Project }, paths.Select(root => root.Path));
        Assert.All(paths, root => Assert.False(root.IncludeHidden));
        Assert.All(paths, root => Assert.Equal(Path.Combine(Project, "opencode.json"), root.SourceEvidence));
        Assert.Contains(capture.DiscoveryRoots, root => root.Path == Path.Combine(custom, "skill"));
        Assert.Contains(capture.DiscoveryRoots, root => root.Path == Path.Combine(Home, ".opencode", "skills"));
        Assert.False(paths[0].Covers(Path.Combine(paths[0].Path, ".hidden", "demo")));
        Assert.True(paths[0].Covers(paths[0].Path));
        Assert.True(capture.DiscoveryRoots.First(root => root.Placement == "external-agents").Covers(Path.Combine(Home, ".agents", "skills", ".hidden", "demo")));
    }

    [Fact]
    public void UnexpandedAndRemoteRootsKeepCoverageIncompleteWithoutFetching()
    {
        Write(Path.Combine(Project, "opencode.json"), "{\"skills\":{\"paths\":[\"{env:SKILLS}\"],\"urls\":[\"https://example.invalid/skills\"]}}");
        var capture = Capture();
        Assert.Empty(capture.DiscoveryRoots.Where(root => root.Placement == "configured-path"));
        Assert.Equal(2, capture.DiscoveryLimitations.Length);
        Assert.Contains(capture.DiscoveryLimitations, reason => reason.Contains("remote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InventoryDiscoversRootAndNestedSkillsUnderEveryOpenCodeConfigDirectory()
    {
        var custom = Path.Combine(temporary.FullName, "custom-config");
        var bundle = Path.Combine(Project, "bundle");
        Write(Path.Combine(Project, "opencode.json"), "{\"skills\":{\"paths\":[\"bundle\"]}}");
        var expected = new[] { Skill(bundle, "root"), Skill(Path.Combine(bundle, "outer"), "outer"), Skill(Path.Combine(bundle, "outer", "nested"), "nested"),
            Skill(Path.Combine(custom, "skill", "custom"), "custom"), Skill(Path.Combine(Home, ".opencode", "skills", "home"), "home"),
            Skill(Path.Combine(Project, ".agents", "skills", ".hidden", "external"), "external") };
        var analysis = await Inventory("opencode", Runner(new Dictionary<string, string> { ["OPENCODE_CONFIG_DIR"] = custom }));
        foreach (var path in expected)
            Assert.Contains(analysis.Candidates, row => row.EntryPath is { } entry && PathIdentity.Equals(entry, path) && row.Expected.Discoverability == "expected");
        Assert.Equal(6, analysis.Candidates.Count(row => expected.Any(path => row.EntryPath is { } entry && PathIdentity.Equals(path, entry))));
        Assert.Contains(analysis.DiscoveryRoots, root => root.Path == bundle);
    }

    [Fact]
    public async Task ExplicitConfiguredRootRemainsDiscoverableWhenCompatibilityDiscoveryIsDisabled()
    {
        var path = Skill(Path.Combine(Project, ".claude", "skills", "demo"));
        Write(Path.Combine(Project, "opencode.json"), JsonSerializer.Serialize(new { skills = new { paths = new[] { Path.GetDirectoryName(path) } } }));
        var analysis = await Inventory("opencode", Runner(new Dictionary<string, string> { ["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "true" }));
        var row = Assert.Single(analysis.Candidates, row => row.EntryPath is { } entry && PathIdentity.Equals(entry, path));
        Assert.Equal("configured-path", row.Placement); Assert.Equal("expected", row.Expected.Discoverability);
        Assert.NotEqual("disabled", row.Expected.State); // gh is unavailable, so invocation selection remains conditional.
    }

    [Fact]
    public async Task SelectedClaudeHomeDiscoversItsPluginsWithoutChangingParentRunnerOrProcessEnvironment()
    {
        var selected = Path.Combine(temporary.FullName, "alternate-claude");
        var plugin = Path.Combine(selected, "plugins", "cache", "market", "package", "1.0.0");
        Write(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{\"name\":\"package\",\"skills\":\"./skills/\"}");
        var payload = Skill(Path.Combine(plugin, "skills", "demo"));
        var runner = Runner(); var processValue = global::System.Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var analysis = await Inventory("claude", runner, selected);
        var row = Assert.Single(analysis.Candidates, row => row.EntryPath is { } entry && PathIdentity.Equals(entry, payload));
        Assert.Equal("managed-plugin", row.Placement); Assert.Equal("package:demo", row.InvocationName);
        Assert.Equal("", runner.GetEffectiveEnvironmentVariable("CLAUDE_CONFIG_DIR"));
        Assert.Equal(Project, runner.EffectiveWorkingDirectory);
        Assert.Equal(processValue, global::System.Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public async Task RecursiveOpenCodeObservationRetainsManagedOwnersInsideConfiguredPluginPayloads()
    {
        var plugin = Path.Combine(Home, ".claude", "plugins", "cache", "market", "package", "1.0.0");
        Write(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{\"name\":\"package\",\"skills\":\"./skills/\"}");
        var outer = Skill(Path.Combine(plugin, "skills", "demo"));
        var nested = Skill(Path.Combine(outer, "nested"), "nested");
        Write(Path.Combine(Project, "opencode.json"), JsonSerializer.Serialize(new { skills = new { paths = new[] { outer } } }));
        var analysis = await Inventory("opencode", Runner());
        foreach (var path in new[] { outer, nested })
        {
            var row = Assert.Single(analysis.Candidates, row => row.EntryPath is { } entry && PathIdentity.Equals(entry, path));
            Assert.Equal("configured-path", row.Placement); Assert.Equal("expected", row.Expected.Discoverability);
            Assert.Contains(row.Subject.Owners, owner => owner.ReadOnly && owner.Provider == "claude-plugin");
        }
    }
}
