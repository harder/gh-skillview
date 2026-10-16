using System.Collections.Immutable;
using System.Security.Cryptography;
using SkillView.Inventory;
using SkillView.Loading;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Loading;

public sealed class LoadingConfigurationCaptureTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("skillview-loading-capture-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Home => Path.Combine(_temporary.FullName, "home");
    private string Project => Path.Combine(_temporary.FullName, "project");
    private string System => Path.Combine(_temporary.FullName, "system");
    public void Dispose() => _temporary.Delete(true);
    private string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
    }
    private ProcessRunner Runner(IReadOnlyDictionary<string, string>? overrides = null)
    {
        Directory.CreateDirectory(Home); Directory.CreateDirectory(Project); Directory.CreateDirectory(System);
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = Home,
            ["USERPROFILE"] = Home,
            ["CLAUDE_CONFIG_DIR"] = "",
            ["CODEX_HOME"] = "",
            ["GEMINI_CLI_HOME"] = "",
            ["GEMINI_CLI_TRUSTED_FOLDERS_PATH"] = "",
            ["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config"),
            ["GEMINI_CLI_SYSTEM_SETTINGS_PATH"] = "",
            ["GEMINI_CLI_SYSTEM_DEFAULTS_PATH"] = "",
            ["GEMINI_CLI_TRUST_WORKSPACE"] = "",
            ["OPENCODE_CONFIG"] = "",
            ["OPENCODE_CONFIG_DIR"] = "",
            ["OPENCODE_CONFIG_CONTENT"] = "",
            ["OPENCODE_PERMISSION"] = "",
            ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "",
            ["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "false",
            ["OPENCODE_DISABLE_CLAUDE_CODE"] = "false",
            ["OPENCODE_DISABLE_CLAUDE_CODE_SKILLS"] = "false"
        };
        if (overrides is not null) foreach (var pair in overrides) environment[pair.Key] = pair.Value;
        return new(new Logger(), environment: environment, defaultWorkingDirectory: Project);
    }
    private CapturedLoadingConfiguration Capture(string agent, IReadOnlyDictionary<string, string>? environment = null,
        string trust = "trusted", string? cwd = null, string? profile = null, string? config = null) =>
        new LoadingConfigurationCapture(Runner(environment), System).Capture(
            new(agent, profile, Project, cwd, config, ProjectTrust: trust), Token);
    private LoadingInvocationPolicy Policy(CapturedLoadingConfiguration capture) => LoadingPolicyEvaluator.Evaluate(
        capture.Context, capture.Settings, "demo", Path.Combine(Project, "demo"), null, null, false);

    [Theory]
    [InlineData(".yaml")]
    [InlineData(".toml")]
    [InlineData(".arbitrary")]
    public void ProviderOverridesParseNativeJsonInsteadOfTrustingTheFileSuffix(string suffix)
    {
        var path = Write(Path.Combine(_temporary.FullName, "override" + suffix), "{ /* comment */ \"skills\":{\"enabled\":false} }");
        var capture = Capture("gemini", new Dictionary<string, string> { ["GEMINI_CLI_SYSTEM_SETTINGS_PATH"] = path });
        Assert.False(Policy(capture).Enabled);
        Assert.Equal("observed", capture.Settings.Layers.Single(layer => layer.Scope == "system-overrides").Document.Evidence.State);
        File.WriteAllText(path, "skills:\n  enabled: false\n");
        Assert.Null(Policy(Capture("gemini", new Dictionary<string, string> { ["GEMINI_CLI_SYSTEM_SETTINGS_PATH"] = path })).Enabled);
        File.WriteAllText(path, "{\"permission\":{\"skill\":\"deny\"},}");
        Assert.False(Policy(Capture("opencode", new Dictionary<string, string> { ["OPENCODE_CONFIG"] = path })).Enabled);
    }

    [Fact]
    public void GeminiSystemOverridesWinAndUntrustedWorkspaceDoesNotSupplyPolicy()
    {
        Write(Path.Combine(Home, ".gemini", "settings.json"), """{"skills":{"disabled":["DEMO"]}}""");
        Write(Path.Combine(Project, ".gemini", "settings.json"), """{"skills":{"disabled":[],"enabled":false}}""");
        Write(Path.Combine(System, "settings.json"), """{"skills":{"enabled":true}}""");
        var capture = Capture("gemini", trust: "untrusted");
        Assert.False(Policy(capture).Enabled); // Disabled union survives system enabled=true.
        Assert.Equal("excluded", capture.Settings.Layers.Single(layer => layer.Scope == "workspace").Applicability);
        File.WriteAllText(Path.Combine(Home, ".gemini", "settings.json"), "{}");
        Assert.True(Policy(Capture("gemini", trust: "untrusted")).Enabled);
        Assert.Null(Policy(Capture("gemini", trust: "unknown")).Enabled);
        var trustedEnvironment = Capture("gemini", new Dictionary<string, string> { ["GEMINI_CLI_TRUST_WORKSPACE"] = "true" }, trust: "unknown");
        Assert.Equal("applied", trustedEnvironment.Settings.Layers.Single(layer => layer.Scope == "workspace").Applicability);
    }

    [Fact]
    public void GeminiHomeAndSystemDefaultsRespectCapturedEnvironmentOverrides()
    {
        var selectedHome = Path.Combine(_temporary.FullName, "gemini-home");
        Write(Path.Combine(selectedHome, ".gemini", "settings.json"), """{"skills":{"enabled":false}}""");
        var defaults = Write(Path.Combine(_temporary.FullName, "defaults.conf"), """{"skills":{"disabled":["other"]}}""");
        var capture = Capture("gemini", new Dictionary<string, string>
        { ["GEMINI_CLI_HOME"] = selectedHome, ["GEMINI_CLI_SYSTEM_DEFAULTS_PATH"] = defaults });
        Assert.Equal(PathIdentity.Normalize(selectedHome), capture.Context.HomeDirectory);
        Assert.False(Policy(capture).Enabled);
        Assert.Equal(defaults, capture.Settings.Layers[0].Document.Evidence.Path);
    }

    [Fact]
    public void ClaudeUntrustedProjectStillSuppliesDenyAndVisibilityButNotAllow()
    {
        Write(Path.Combine(Project, ".claude", "settings.json"), """{"permissions":{"deny":["Skill(demo)"],"allow":["Skill(other)"]},"skillOverrides":{"other":"off"}}""");
        var capture = Capture("claude", trust: "untrusted");
        var policy = Policy(capture);
        Assert.False(policy.Automatic); Assert.Equal("deny", policy.Approval);
        Assert.Null(capture.Settings.Merge("claude").Get("permissions", "allow"));
        var other = LoadingPolicyEvaluator.Evaluate(capture.Context, capture.Settings, "other", Path.Combine(Project, "other"), null, null, false);
        Assert.False(other.Enabled);
    }

    [Fact]
    public void OpenCodeDirectoryPhaseOverridesPlainProjectFilesAndPreservesHomeDirectoryPolicy()
    {
        var nested = Path.Combine(Project, "nested"); Directory.CreateDirectory(nested);
        Write(Path.Combine(nested, "opencode.json"), """{"permission":{"skill":"allow"}}""");
        Write(Path.Combine(Project, ".opencode", "opencode.json"), """{"permission":{"skill":"deny"}}""");
        Assert.False(Policy(Capture("opencode", cwd: nested)).Enabled);
        Write(Path.Combine(Home, ".opencode", "opencode.json"), """{"permission":{"skill":"ask"}}""");
        Assert.Equal("ask", Policy(Capture("opencode", cwd: nested)).Approval);
    }

    [Theory]
    [InlineData("OPENCODE_CONFIG_CONTENT", "{\"permission\":{\"skill\":\"deny\"}}")]
    [InlineData("OPENCODE_PERMISSION", "{\"skill\":\"deny\"}")]
    public void KnownUninterpretedRuntimePolicyPreventsAnEnabledDefault(string key, string value)
    {
        var capture = Capture("opencode", new Dictionary<string, string> { [key] = value });
        Assert.NotEmpty(capture.Settings.LocalPolicyBlockers);
        Assert.Null(Policy(capture).Enabled);
        Assert.DoesNotContain(value, string.Join("", capture.Settings.Evidence));
    }

    [Fact]
    public void RelativeProjectAndCwdResolveOnceAgainstTheOriginalWorkingDirectory()
    {
        var runner = Runner();
        var parent = new LoadingConfigurationCapture(runner, System).Capture(new("codex", ProjectDirectory: "child"), Token);
        Assert.Equal(PathIdentity.Normalize(Path.Combine(Project, "child")), parent.Context.ProjectDirectory);
        Assert.Equal(parent.Context.ProjectDirectory, parent.Context.WorkingDirectory);
        var nested = new LoadingConfigurationCapture(runner, System).Capture(new("codex", ProjectDirectory: "child", WorkingDirectory: "child/nested"), Token);
        Assert.Equal(parent.Context.ProjectDirectory, nested.Context.ProjectDirectory);
        Assert.Equal(PathIdentity.Normalize(Path.Combine(Project, "child", "nested")), nested.Context.WorkingDirectory);
    }

    [Fact]
    public void ConditionalLegacyTomlModeKeepsNativeMigrationPolicyUnknown()
    {
        Write(Path.Combine(Home, ".config", "opencode", "config"), "[mode.build.permission]\nskill = 'deny'\n");
        var capture = Capture("opencode");
        Assert.True(capture.Settings.HasUncertainLocalPolicy); Assert.Null(Policy(capture).Enabled);
        Assert.Contains(capture.DiscoveryLimitations, reason => reason.Contains("conditional", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", false)]
    [InlineData("false", false)]
    public void ProjectConfigFlagUsesItsOwnNativeBooleanContract(string value, bool excluded)
    {
        Write(Path.Combine(Project, "opencode.json"), "{\"permission\":{\"skill\":\"deny\"}}");
        var capture = Capture("opencode", new Dictionary<string, string> { ["OPENCODE_DISABLE_PROJECT_CONFIG"] = value });
        Assert.Equal(!excluded, capture.Settings.Layers.Any(layer => layer.Scope == "project"));
        Assert.Equal(!excluded, capture.DiscoveryRoots.Any(root => PathIdentity.Equals(root.Path, Path.Combine(Project, ".opencode", "skills"))));
        Assert.Equal(excluded, Policy(capture).Enabled);
        Assert.NotEqual(Capture("opencode").Context.ContextId, capture.Context.ContextId);
    }

    [Fact]
    public void CodexSelectedProfileAndTrustedProjectAreDifferentLayersWithDistinctContexts()
    {
        Write(Path.Combine(Home, ".codex", "config.toml"), "model = 'base'\n");
        Write(Path.Combine(Home, ".codex", "work.config.toml"), "model = 'profile'\n");
        Write(Path.Combine(Project, ".codex", "config.toml"), "model = 'project'\n");
        var trusted = Capture("codex", profile: "work");
        Assert.Equal("project", trusted.Settings.Merge("codex").Get("model")!.Text);
        var untrusted = Capture("codex", profile: "work", trust: "untrusted");
        Assert.Equal("profile", untrusted.Settings.Merge("codex").Get("model")!.Text);
        Assert.NotEqual(trusted.Context.ContextId, untrusted.Context.ContextId);
        Assert.NotEqual(trusted.Context.ContextId, Capture("codex").Context.ContextId);
        Assert.Contains(untrusted.Settings.Evidence, evidence => evidence.Purpose.EndsWith("applicability=excluded", StringComparison.Ordinal));
    }

    [Fact]
    public void GeminiSavedTrustIsCapturedWithOverridesButDoesNotInventLiveIdeTrust()
    {
        Runner();
        var rules = Write(Path.Combine(_temporary.FullName, "trust.json"), global::System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        { [Project] = "TRUST_FOLDER", [Path.Combine(Project, "child")] = "DO_NOT_TRUST" }));
        var capture = Capture("gemini", new Dictionary<string, string> { ["GEMINI_CLI_TRUSTED_FOLDERS_PATH"] = rules }, trust: "unknown");
        Assert.Contains(capture.Settings.Evidence, file => file.Path == rules && file.State == "observed");
        Assert.Contains(capture.Diagnostics, line => line.StartsWith("Gemini saved trust expectation: trusted", StringComparison.Ordinal));
        Assert.Equal("unknown", capture.Settings.ExpectedProjectTrust);
        var nested = Path.Combine(Project, "child"); Directory.CreateDirectory(nested);
        var blocked = Capture("gemini", new Dictionary<string, string> { ["GEMINI_CLI_TRUSTED_FOLDERS_PATH"] = rules }, trust: "unknown", cwd: nested);
        Assert.Contains(blocked.Diagnostics, line => line.StartsWith("Gemini saved trust expectation: untrusted", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenCodeMarkdownRoleNamesAndDirectoryMergeOrderAffectTheSelectedRole()
    {
        Write(Path.Combine(Home, ".config", "opencode", "opencode.json"), """{"agent":{"build":{"permission":{"skill":"deny"}}}}""");
        Write(Path.Combine(Home, ".config", "opencode", "agents", ".hidden", "different-file.md"), "---\nname: build\ntools:\n  skill: true\n---\nIgnored prompt body.\n");
        var capture = Capture("opencode");
        Assert.True(Policy(capture).Enabled);
        Assert.Contains(capture.Settings.Evidence, file => file.Path.EndsWith("different-file.md", StringComparison.Ordinal));
        Write(Path.Combine(Project, ".opencode", "agents", "build.md"), "---\npermission:\n  skill: ask\n---\n");
        Assert.Equal("ask", Policy(Capture("opencode")).Approval);
        Write(Path.Combine(Home, ".opencode", "opencode.json"), """{"agent":{"build":{"permission":{"skill":"deny"}}}}""");
        Assert.False(Policy(Capture("opencode")).Enabled);
    }

    [Fact]
    public void OpenCodeLegacyMarkdownModeLoadsAfterAgentAndExplicitNameCanCreateCustomRole()
    {
        Write(Path.Combine(Home, ".config", "opencode", "agents", "build.md"), "---\npermission:\n  skill: allow\n---\n");
        Write(Path.Combine(Home, ".config", "opencode", "modes", "different.md"), "---\nname: build\npermission:\n  skill: deny\n---\n");
        Write(Path.Combine(Home, ".config", "opencode", "agents", "team", "review.md"), "---\npermission:\n  skill: ask\n---\n");
        var capture = Capture("opencode");
        Assert.False(Policy(capture).Enabled);
        var custom = LoadingPolicyEvaluator.Evaluate(capture.Context with { AgentName = "team/review" }, capture.Settings, "demo", Project, null, null, false);
        Assert.True(custom.Enabled); Assert.Equal("ask", custom.Approval);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mode: subagent\n")]
    public void ValidMarkdownModesForcePrimaryEvenAfterSubagentSettings(string mode)
    {
        Write(Path.Combine(Home, ".config", "opencode", "opencode.json"), "{\"agent\":{\"build\":{\"mode\":\"subagent\"}}}");
        Write(Path.Combine(Home, ".config", "opencode", "modes", "build.md"), "---\n" + mode + "permission:\n  skill: deny\n---\n");
        var capture = Capture("opencode");
        Assert.Equal("primary", capture.Settings.Merge("opencode").Get("agent", "build", "mode")!.Text);
        Assert.False(Policy(capture).Enabled);
        File.WriteAllText(Path.Combine(Home, ".config", "opencode", "modes", "build.md"), "---\nmode: false\npermission:\n  skill: allow\n---\n");
        Assert.Null(Policy(Capture("opencode")).Enabled);
    }

    [Fact]
    public void KnownButUncapturedOpenCodeMarkdownNeverFallsBackToAllow()
    {
        Write(Path.Combine(Home, ".config", "opencode", "agents", "build.md"), "---\npermission: [malformed\n---\n");
        Assert.Null(Policy(Capture("opencode")).Enabled);
        Write(Path.Combine(Home, ".config", "opencode", "agents", "build.md"), "---\nname: build\n---\n");
        Write(Path.Combine(Home, ".config", "opencode", "agents", "duplicate.md"), "---\nname: build\n---\n");
        var duplicate = Capture("opencode");
        Assert.Null(Policy(duplicate).Enabled);
        Assert.Contains(duplicate.Settings.LocalPolicyBlockers, item => item.Contains("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalysisDoesNotWriteConfigurationOrLaunchAClient()
    {
        Runner();
        foreach (var relative in new[] { ".claude/settings.json", ".gemini/settings.json", ".config/opencode/opencode.json" })
            Write(Path.Combine(Home, relative), "{}");
        Write(Path.Combine(Home, ".codex", "config.toml"), "# empty\n");
        string[] Snapshot() => Directory.EnumerateFileSystemEntries(_temporary.FullName, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Directory.Exists(path) ? path + "/" : path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        var before = Snapshot();
        foreach (var agent in new[] { "claude", "codex", "gemini", "opencode" }) Capture(agent);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void AlreadyCanceledCaptureDoesNotProbeInvalidAddresses()
    {
        var runner = Runner(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new LoadingConfigurationCapture(runner, System)
            .Capture(new("gemini", ProjectDirectory: "invalid\0path"), canceled.Token));
        Assert.Throws<ArgumentException>(() => new LoadingConfigurationCapture(runner, System)
            .Capture(new("codex", Profile: "../escape"), Token));
    }
}
