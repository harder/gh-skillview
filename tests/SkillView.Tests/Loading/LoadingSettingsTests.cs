using System.Collections.Immutable;
using SkillView.Loading;
using Xunit;

namespace SkillView.Tests.Loading;

public sealed class LoadingSettingsTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("skillview-loading-policy-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private int _file;
    public void Dispose() => _temporary.Delete(true);
    private LoadingSettingsLayer Layer(string text, string applicability = "applied", string extension = ".json")
    {
        var path = Path.Combine(_temporary.FullName, ++_file + extension);
        File.WriteAllText(path, text);
        return new("fixture-" + _file, applicability, new LoadingConfigurationReader().Read(path, "fixture", Token));
    }
    private SkillLoadingContext Context(string agent, string? agentName = null) => SkillLoadingContext.Capture(
        agent, null, _temporary.FullName, _temporary.FullName, _temporary.FullName, _temporary.FullName,
        null, agentName, "trusted", "fixture", "documented-policy-fixture");
    private LoadingInvocationPolicy Evaluate(string agent, LoadingSettings settings, string name = "demo",
        LoadingValue? metadata = null, string? agentName = null, bool plugin = false) =>
        LoadingPolicyEvaluator.Evaluate(Context(agent, agentName), settings, name, Path.Combine(_temporary.FullName, name), metadata, metadata, plugin);

    [Theory]
    [InlineData("{\"tools\":{\"skill\":false},\"permission\":{\"skill\":\"allow\"}}", true)]
    [InlineData("{\"tools\":{\"skill\":false}}", false)]
    [InlineData("{\"agent\":{\"build\":{\"tools\":{\"skill\":false},\"permission\":{\"skill\":\"allow\"}}}}", true)]
    [InlineData("{\"agent\":{\"build\":{\"tools\":{\"skill\":false}}}}", false)]
    public void OpenCodeExplicitPermissionOverridesDeprecatedToolsAtItsOwnLevel(string json, bool enabled)
    {
        var policy = Evaluate("opencode", new([Layer(json)], []));
        Assert.Equal(enabled, policy.Enabled);
    }

    [Fact]
    public void OpenCodeNormalizesEachAgentSourceBeforeMergingAndLegacyModeWinsLast()
    {
        var lower = Layer("""{"agent":{"build":{"permission":{"skill":"deny"}}}}""");
        var later = Layer("""{"agent":{"build":{"tools":{"skill":true}}}}""");
        Assert.True(Evaluate("opencode", new([lower, later], [])).Enabled);
        var mode = Layer("""{"mode":{"build":{"permission":{"skill":"deny"}}}}""");
        Assert.False(Evaluate("opencode", new([lower, later, mode], [])).Enabled);
    }

    [Fact]
    public void OpenCodeScalarPermissionsNormalizeBeforeSourceMergesAndKeepWildcardPosition()
    {
        var initial = Layer("""{"agent":{"build":{"permission":{"skill":"allow"}}}}""");
        var scalar = Layer("""{"agent":{"build":{"permission":"deny"}}}""");
        var later = Layer("""{"agent":{"build":{"permission":{"skill":"allow"}}}}""");
        var settings = new LoadingSettings([initial, scalar, later], []);
        Assert.Equal(new[] { "skill", "*" }, settings.Merge("opencode").Get("agent", "build", "permission")!.FieldOrder);
        Assert.False(Evaluate("opencode", settings).Enabled);
    }

    [Theory]
    [InlineData("{\"mode\":\"nonsense\"}")]
    [InlineData("{\"permission\":{\"bash\":42}}")]
    [InlineData("{\"permission\":{\"bash\":{\"*\":\"yes\"}}}")]
    [InlineData("{\"tools\":{\"bash\":\"false\"}}")]
    [InlineData("{\"hidden\":\"true\"}")]
    public void InvalidUnrelatedAgentFieldsCannotEstablishSuccessfulNativeDecoding(string agent)
    {
        Assert.Null(Evaluate("opencode", new([Layer("{\"agent\":{\"build\":" + agent + "}}")], [])).Enabled);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("{\"paths\":\"folder\"}")]
    [InlineData("{\"urls\":[false]}")]
    public void InvalidOpenCodeDiscoverySettingsCannotProveNativeConfigurationDecodes(string skills)
    {
        Assert.Null(Evaluate("opencode", new([Layer("{\"skills\":" + skills + "}")], [])).Enabled);
    }

    [Fact]
    public void OversizedPermissionPatternsBecomeUnknownInsteadOfCrashingOrAllowing()
    {
        var pattern = new string('*', 4096);
        var settings = new LoadingSettings([Layer("{\"permission\":{\"" + pattern + "\":\"deny\"}}")], []);
        Assert.Null(Evaluate("opencode", settings).Enabled);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("todowrite")]
    [InlineData("webfetch")]
    [InlineData("websearch")]
    [InlineData("doom_loop")]
    public void ScalarOnlyPermissionKeysCannotAcceptResourceMaps(string tool)
    {
        Assert.Null(Evaluate("opencode", new([Layer("{\"permission\":{\"" + tool + "\":{\"*\":\"deny\"}}}")], [])).Enabled);
    }

    [Theory]
    [InlineData("{\"disable\":true}")]
    [InlineData("{\"hidden\":true}")]
    [InlineData("{\"mode\":\"subagent\"}")]
    public void AutomaticRoleSelectionCannotAssumeBuildWhenItIsIneligible(string properties)
    {
        var settings = new LoadingSettings([Layer("{\"agent\":{\"build\":" + properties + "}}")], []);
        Assert.Null(Evaluate("opencode", settings).Enabled);
        var explicitRole = Evaluate("opencode", settings, agentName: "build");
        Assert.NotNull(explicitRole.Enabled);
    }

    [Fact]
    public void IneligibleConfiguredDefaultCustomRoleDoesNotBecomeImplicitlyAvailable()
    {
        var settings = new LoadingSettings([Layer("""{"default_agent":"review","agent":{"review":{"hidden":true,"mode":"subagent"}}}""")], []);
        Assert.Null(Evaluate("opencode", settings).Enabled);
        Assert.True(Evaluate("opencode", settings, agentName: "review").Enabled);
    }

    [Fact]
    public void GeminiDisabledUnionCannotBeClearedByAHigherEmptyArrayAndNamesIgnoreCase()
    {
        var settings = new LoadingSettings([Layer("""{"skills":{"disabled":["Demo"]}}"""), Layer("""{"skills":{"disabled":[]}}""")], []);
        var policy = Evaluate("gemini-cli", settings);
        Assert.False(policy.Enabled); Assert.False(policy.Manual); Assert.False(policy.Automatic);
        Assert.Equal("activation-consent", policy.Approval);
        Assert.Equal("Demo", Assert.Single(settings.Merge("gemini-cli").Get("skills", "disabled")!.Items).Text);
    }

    [Fact]
    public void GeminiLocalAdminKeyDoesNotPretendToEstablishRemoteAdminPolicy()
    {
        var policy = Evaluate("gemini-cli", new([Layer("""{"admin":{"skills":{"enabled":false}}}""")], ["remote-admin"]));
        Assert.True(policy.Enabled);
        Assert.Contains(policy.Reasons, reason => reason.Contains("remote admin", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("gemini-cli", "{\"skills\":{\"enabled\":\"false\"}}")]
    [InlineData("gemini-cli", "{\"skills\":false}")]
    [InlineData("claude-code", "{\"permissions\":false}")]
    [InlineData("opencode", "{\"agent\":false}")]
    [InlineData("gemini-cli", "{\"skills\":{\"disabled\":[null]}}")]
    [InlineData("gemini-cli", "{\"skills\":{\"disabled\":[\"${SECRET}\"]}}")]
    [InlineData("claude-code", "{\"skillOverrides\":{\"demo\":false}}")]
    [InlineData("claude-code", "{\"permissions\":{\"deny\":[false]}}")]
    [InlineData("opencode", "{\"permission\":{\"skill\":{\"*\":false}}}")]
    [InlineData("opencode", "{\"permission\":{\"skill\":\"{env:POLICY}\"}}")]
    [InlineData("codex", "{\"skills\":{\"config\":false}}")]
    public void InvalidOrUnresolvedPolicyValuesNeverBecomeEnabledDefaults(string agent, string text)
    {
        var result = Evaluate(agent, new([Layer(text)], []));
        Assert.Null(result.Enabled); Assert.Null(result.Automatic); Assert.Equal("unknown", result.Approval);
    }

    [Fact]
    public void TrustGatedPolicyRemainsConditionalWhileExplicitlyExcludedSourcesDoNotApply()
    {
        const string disabled = """{"skills":{"enabled":false}}""";
        Assert.Null(Evaluate("gemini-cli", new([Layer(disabled, "conditional")], [])).Enabled);
        Assert.True(Evaluate("gemini-cli", new([Layer(disabled, "excluded")], [])).Enabled);
        Assert.False(Evaluate("gemini-cli", new([Layer(disabled)], [])).Enabled);
    }

    [Theory]
    [InlineData("opencode", "{\"mode\":{\"build\":{\"permission\":{\"skill\":\"deny\"}}}}")]
    [InlineData("opencode", "{\"default_agent\":\"review\"}")]
    [InlineData("claude-code", "{\"enabledPlugins\":{\"demo@market\":false}}")]
    [InlineData("claude-code", "{\"allowManagedPermissionRulesOnly\":true}")]
    [InlineData("gemini-cli", "{\"security\":{\"folderTrust\":{\"enabled\":false}}}")]
    public void ConditionalConsumedKeysCannotProduceAnEnabledDefault(string agent, string json)
    {
        var conditional = new LoadingSettings([Layer(json, "conditional")], []);
        Assert.True(conditional.HasUncertainLocalPolicy);
        Assert.Null(Evaluate(agent, conditional).Enabled);
        Assert.False(new LoadingSettings([Layer(json, "excluded")], []).HasUncertainLocalPolicy);
    }

    [Fact]
    public void BrokenConfigurationIsUnknownButMissingConfigurationIsNotAnEmptyObservedFile()
    {
        Assert.Null(Evaluate("gemini-cli", new([Layer("{broken")], [])).Enabled);
        var missing = new LoadingSettingsLayer("user", "applied", new(
            new(Path.Combine(_temporary.FullName, "absent.json"), "missing", null, "fixture", null), null));
        Assert.True(Evaluate("gemini-cli", new([missing], [])).Enabled);
        Assert.Equal("missing", Assert.Single(new LoadingSettings([missing], []).Evidence).State);
    }

    [Fact]
    public void ClaudePermissionListsCombineAndDenyWinsWithoutErasingManualEntry()
    {
        var settings = new LoadingSettings([
            Layer("""{"permissions":{"deny":["Skill(demo)"],"allow":["Skill(other)"]}}"""),
            Layer("""{"permissions":{"allow":["Skill(demo)"],"deny":[]}}""")], []);
        var policy = Evaluate("claude-code", settings);
        Assert.True(policy.Manual); Assert.False(policy.Automatic); Assert.Equal("deny", policy.Approval);
        Assert.Single(settings.Merge("claude-code").Get("permissions", "deny")!.Items);
        Assert.Equal(2, settings.Merge("claude-code").Get("permissions", "allow")!.Items.Length);
    }

    [Fact]
    public void ClaudeTypedFrontmatterKeepsManualAndAutomaticSurfacesSeparate()
    {
        var metadata = Layer("user-invocable: true\ndisable-model-invocation: true\n", extension: ".yaml").Document.Root;
        var policy = Evaluate("claude-code", new([], []), metadata: metadata);
        Assert.True(policy.Manual); Assert.False(policy.Automatic);
        var invalid = Layer("disable-model-invocation: \"true\"\n", extension: ".yaml").Document.Root;
        Assert.Null(Evaluate("claude-code", new([], []), metadata: invalid).Enabled);
    }

    [Fact]
    public void ClaudePluginVisibilityCannotBeChangedByOrdinarySkillOverrides()
    {
        var settings = new LoadingSettings([Layer("""{"skillOverrides":{"plugin:demo":"off"}}""")], []);
        Assert.False(Evaluate("claude-code", settings, "plugin:demo").Enabled);
        Assert.True(Evaluate("claude-code", settings, "plugin:demo", plugin: true).Enabled);
        Assert.Contains(Evaluate("claude-code", settings, "plugin:demo", plugin: true).Reasons,
            reason => reason.Contains("Plugin enablement", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Skill(deploy *)", "deploy")]
    [InlineData("Skill(deploy)", "apps/web:deploy")]
    [InlineData("Skill(anthropic-skills *)", "anthropic-skills:pdf")]
    public void ClaudeDocumentedArgumentAndNestedDenySelectorsApply(string selector, string name)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(new { permissions = new { deny = new[] { selector } } });
        var policy = Evaluate("claude-code", new([Layer(text)], []), name);
        Assert.Equal("deny", policy.Approval); Assert.False(policy.Automatic); Assert.True(policy.Manual);
    }

    [Fact]
    public void ClaudeConflictingVisibilityStaysUnknownAndNamespacePrefixesDoNotOvermatch()
    {
        var metadata = Layer("user-invocable: false\ndisable-model-invocation: true\n", extension: ".yaml").Document.Root;
        var policy = Evaluate("claude-code", new([Layer("""{"skillOverrides":{"demo":"on"}}""")], []), metadata: metadata);
        Assert.Null(policy.Manual); Assert.Null(policy.Automatic);
        var outside = Evaluate("claude-code", new([Layer("""{"permissions":{"deny":["Skill(anthropic *)"]}}""")], []), "anthropic-skills:pdf");
        Assert.True(outside.Automatic);
        var unresolved = Evaluate("claude-code", new([Layer("""{"permissions":{"deny":["Skill(skill:deploy)"]}}""")], []), "deploy");
        Assert.Null(unresolved.Automatic);
    }

    [Fact]
    public void OpenCodeToolWildcardRetainsItsAuthoredPosition()
    {
        var policy = Evaluate("opencode", new([Layer("""{"permission":{"skill":"allow","*":"deny"}}""")], []));
        Assert.False(policy.Enabled); Assert.Equal("deny", policy.Approval);
        policy = Evaluate("opencode", new([Layer("""{"permission":{"*":"deny","skill":"allow"}}""")], []));
        Assert.True(policy.Enabled); Assert.Equal("allow", policy.Approval);
    }

    [Fact]
    public void ClaudeAllToolDenyAndManagedLocksCannotBeWeakenedByOrdinarySources()
    {
        Assert.False(Evaluate("claude-code", new([Layer("""{"permissions":{"deny":["*"]}}""")], [])).Automatic);
        var managed = Layer("""{"allowManagedPermissionRulesOnly":true,"permissions":{"deny":["Skill(demo)"]}}""") with { Scope = "managed-file-preview" };
        var ordinary = Layer("""{"allowManagedPermissionRulesOnly":false,"permissions":{"allow":["Skill(demo)"]}}""");
        var settings = new LoadingSettings([ordinary, managed], []);
        Assert.Null(settings.Merge("claude-code").Get("permissions", "allow"));
        Assert.Equal("deny", Evaluate("claude-code", settings).Approval);
        var strict = Layer("""{"strictPluginOnlyCustomization":["skills"]}""") with { Scope = "managed-file-preview" };
        Assert.False(Evaluate("claude-code", new([strict], [])).Enabled);
        Assert.True(Evaluate("claude-code", new([strict], []), plugin: true).Enabled);
        Assert.True(Evaluate("claude-code", new([strict with { Scope = "user" }], [])).Enabled);
    }

    [Fact]
    public void OpenCodeUnknownOrDisabledSelectedAgentCannotInheritGenericAllow()
    {
        Assert.Null(Evaluate("opencode", new([], []), agentName: "explore").Enabled);
        var settings = new LoadingSettings([Layer("""{"agent":{"plan":{"disable":true}}}""")], []);
        Assert.False(Evaluate("opencode", settings, agentName: "plan").Enabled);
        Assert.True(Evaluate("opencode", settings, agentName: "build").Enabled);
    }

    [Fact]
    public void OpenCodeLaterMatchingPatternsAndAgentOverridesRetainSourceOrder()
    {
        var settings = new LoadingSettings([
            Layer("""{"permission":{"skill":{"*":"deny","internal-*":"allow"}}}"""),
            Layer("""{"permission":{"skill":{"*":"ask"}},"agent":{"plan":{"permission":{"skill":{"internal-*":"deny"}}}}}""")], []);
        Assert.Equal(new[] { "*", "internal-*" }, settings.Merge("opencode").Get("permission", "skill")!.FieldOrder);
        Assert.Equal("allow", Evaluate("opencode", settings, "internal-demo").Approval);
        var selected = Evaluate("opencode", settings, "internal-demo", agentName: "plan");
        Assert.Equal("deny", selected.Approval); Assert.False(selected.Manual); Assert.False(selected.Automatic);
    }

    [Fact]
    public void OpenCodeExplicitRequestsSurviveAutomaticGuidanceOptOut()
    {
        var metadata = Layer("metadata:\n  opencode/autoinvoke: \"false\"\n", extension: ".yaml").Document.Root;
        var policy = Evaluate("opencode", new([], []), metadata: metadata);
        Assert.True(policy.Manual); Assert.False(policy.Automatic); Assert.Equal("allow", policy.Approval);
        Assert.False(policy.Slash);
        var slashMetadata = Layer("metadata:\n  opencode/slash: \"true\"\n", extension: ".yaml").Document.Root;
        Assert.True(Evaluate("opencode", new([], []), metadata: slashMetadata).Slash);
        var disabledTool = new LoadingSettings([Layer("""{"agent":{"plan":{"tools":{"skill":false}}}}""")], []);
        Assert.False(Evaluate("opencode", disabledTool, agentName: "plan").Enabled);
        Assert.True(Evaluate("opencode", disabledTool, agentName: "build").Enabled);
    }

    [Fact]
    public void CodexImplicitPolicyDoesNotDisableExplicitInvocation()
    {
        var metadata = Layer("policy:\n  allow_implicit_invocation: false\n", extension: ".yaml").Document.Root;
        var policy = Evaluate("codex", new([], []), metadata: metadata);
        Assert.True(policy.Manual); Assert.False(policy.Automatic);
    }

    [Fact]
    public void CodexFolderAndFileOverridesRemainExplicitlyAmbiguousUntilNativeContractIsVerified()
    {
        foreach (var suffix in new[] { "", "/SKILL.md" })
        {
            var path = Path.Combine(_temporary.FullName, "demo") + suffix;
            var text = System.Text.Json.JsonSerializer.Serialize(new { skills = new { config = new[] { new { path, enabled = false } } } });
            var policy = Evaluate("codex", new([Layer(text)], []));
            Assert.Null(policy.Enabled);
            Assert.Contains(policy.Reasons, reason => reason.Contains("contracts disagree", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ContextFingerprintIncludesApplicabilityContentAndUnobservedSourcesWithoutLeakingValues()
    {
        var layer = Layer("""{"secret":"do-not-emit"}""");
        var settings = new LoadingSettings([layer], []);
        Assert.NotEqual(settings.Fingerprint, (settings with { Layers = [layer with { Applicability = "conditional" }] }).Fingerprint);
        Assert.NotEqual(settings.Fingerprint, (settings with { UnobservedSources = ["remote"] }).Fingerprint);
        Assert.DoesNotContain("do-not-emit", string.Join("", settings.Evidence));
        Assert.NotEqual(settings.Fingerprint, new LoadingSettings([Layer("""{"secret":"different"}""")], []).Fingerprint);
    }
}
