using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Loading;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Loading;

public sealed class EffectiveSkillLoaderTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("skillview-effective-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Home => Path.Combine(_temporary.FullName, "home");
    private string Project => Path.Combine(_temporary.FullName, "project");
    private string SystemDirectory => Path.Combine(_temporary.FullName, "system");
    public void Dispose() => _temporary.Delete(true);
    private string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
    }
    private InstalledSkill Skill(string path, string header = "name: demo\ndescription: Demonstration\n", params AgentMembership[] memberships)
    {
        Write(Path.Combine(path, "SKILL.md"), "---\n" + header + "---\nSkill body is never executed.\n");
        var frontmatter = FrontMatterParser.Parse("---\n" + header + "---\n").FrontMatter;
        return new()
        {
            Name = frontmatter.Name ?? Path.GetFileName(path),
            ResolvedPath = path,
            ScanRoot = Path.GetDirectoryName(path)!,
            Scope = Scope.User,
            Agents = memberships.Length > 0 ? memberships.ToImmutableArray() : [new("universal", path, false, Scope.User)],
            FrontMatter = frontmatter,
            Validity = frontmatter.Diagnostics.IsEmpty ? ValidityState.Valid : ValidityState.InvalidMetadata,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = memberships.Any(member => member.IsSymlink),
            InstalledAt = null
        };
    }
    private CapturedLoadingConfiguration Configuration(string agent, string trust = "trusted")
    {
        Directory.CreateDirectory(Home); Directory.CreateDirectory(Project); Directory.CreateDirectory(SystemDirectory);
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = Home,
            ["USERPROFILE"] = Home,
            ["CLAUDE_CONFIG_DIR"] = "",
            ["CODEX_HOME"] = "",
            ["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config"),
            ["GEMINI_CLI_HOME"] = "",
            ["GEMINI_CLI_TRUSTED_FOLDERS_PATH"] = "",
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
        return new LoadingConfigurationCapture(new(new Logger(), environment: environment, defaultWorkingDirectory: Project), SystemDirectory)
            .Capture(new(agent, ProjectDirectory: Project, ClientVersion: agent == "gemini" ? "0.46.0" : null, ProjectTrust: trust), Token);
    }
    private static InventorySnapshot Inventory(params InstalledSkill[] skills) => InventorySnapshot.Empty with
    { Skills = skills.ToImmutableArray(), CapturedAt = DateTimeOffset.UtcNow };
    private EffectiveSkillAnalysis Analyze(string agent, params InstalledSkill[] skills) =>
        EffectiveSkillLoader.Capture(Inventory(skills), Configuration(agent), Token);

    [Fact]
    public void ClaudePersonalWinsOverProjectButEveryPhysicalSubjectRemains()
    {
        var personal = Skill(Path.Combine(Home, ".claude", "skills", "demo"));
        var project = Skill(Path.Combine(Project, ".claude", "skills", "demo"));
        var analysis = Analyze("claude", project, personal);
        Assert.Equal(2, analysis.Candidates.Length);
        Assert.Equal("shadowed", analysis.Candidates.Single(row => row.Subject.Path == project.ResolvedPath).Expected.Selection);
        Assert.Equal("selected", analysis.Candidates.Single(row => row.Subject.Path == personal.ResolvedPath).Expected.Selection);
        Assert.Equal("unknown", analysis.Candidates[0].SessionState);
        Assert.Equal("unavailable", analysis.Native.State);
    }

    [Fact]
    public void UninspectableSupplementalRootQualifiesWinnerAndFrozenConsumerCoverage()
    {
        var personal = Skill(Path.Combine(Home, ".claude", "skills", "demo"));
        personal = personal with { ObservedSubject = OperationSubject.From(personal) };
        var project = Skill(Path.Combine(Project, ".claude", "skills", "demo"));
        var observation = LoadingDiscoveryCapture.Probe([Path.Combine(SystemDirectory, ".claude", "skills")], Token,
            _ => throw new UnauthorizedAccessException());
        var analysis = EffectiveSkillLoader.Capture(observation.Apply(Inventory(personal, project)), Configuration("claude"), Token);
        var highest = analysis.Candidates.Single(row => row.EntryPath == personal.ResolvedPath);
        Assert.Equal("conditional-selection", highest.Expected.Selection);
        Assert.Equal("unknown", highest.Expected.ManualInvocation);
        Assert.False(highest.Subject.ExposureCoverageComplete);
        Assert.Equal("shadowed", analysis.Candidates.Single(row => row.EntryPath == project.ResolvedPath).Expected.Selection);
    }

    [Fact]
    public void ClaudeEnterpriseUsesDocumentedDotClaudePlacement()
    {
        var enterprise = Skill(Path.Combine(SystemDirectory, ".claude", "skills", "demo"));
        var personal = Skill(Path.Combine(Home, ".claude", "skills", "demo"));
        var analysis = Analyze("claude", personal, enterprise);
        Assert.Equal("enterprise", analysis.Candidates.Single(row => row.Subject.Path == enterprise.ResolvedPath).Placement);
        Assert.Equal("shadowed", analysis.Candidates.Single(row => row.Subject.Path == personal.ResolvedPath).Expected.Selection);
    }

    [Fact]
    public void MissingPortableNameDoesNotBecomeClaudeNativeIncompatibility()
    {
        var skill = Skill(Path.Combine(Home, ".claude", "skills", "demo"), "description: Native directory fallback\n");
        Assert.NotEmpty(skill.FrontMatter.Diagnostics);
        var row = Assert.Single(Analyze("claude", skill).Candidates);
        Assert.Equal("demo", row.InvocationName);
        Assert.Equal("eligible", row.Expected.State); Assert.Equal("yes", row.Expected.ManualInvocation);
        Assert.NotEmpty(row.PortableDiagnostics);
    }

    [Fact]
    public void GeminiWorkspaceAgentsWinsOverNativeAndUserRoots()
    {
        var paths = new[] { Path.Combine(Project, ".agents", "skills", "demo"), Path.Combine(Project, ".gemini", "skills", "demo"),
            Path.Combine(Home, ".agents", "skills", "demo"), Path.Combine(Home, ".gemini", "skills", "demo") };
        var analysis = Analyze("gemini", paths.Select(path => Skill(path)).ToArray());
        Assert.Equal(4, analysis.Candidates.Length);
        Assert.Equal("selected", analysis.Candidates.Single(row => row.EntryPath == paths[0]).Expected.Selection);
        Assert.All(analysis.Candidates.Where(row => row.EntryPath != paths[0]), row => Assert.Equal("shadowed", row.Expected.Selection));
    }

    [Fact]
    public void UntrustedOrUnknownGeminiWorkspaceCannotDefinitelyShadowKnownUserSkill()
    {
        var workspace = Skill(Path.Combine(Project, ".agents", "skills", "demo"));
        var user = Skill(Path.Combine(Home, ".agents", "skills", "demo"));
        foreach (var trust in new[] { "unknown", "untrusted" })
        {
            var result = EffectiveSkillLoader.Capture(Inventory(workspace, user), Configuration("gemini", trust), Token);
            var userRow = result.Candidates.Single(row => row.EntryPath == user.ResolvedPath);
            Assert.NotEqual("shadowed", userRow.Expected.Selection);
            Assert.NotEqual("no", userRow.Expected.ManualInvocation);
        }
    }

    [Fact]
    public void GeminiUnsupportedMetadataCannotProveAHigherTierWinner()
    {
        var workspace = Skill(Path.Combine(Project, ".agents", "skills", "demo"), "name: demo\n");
        var user = Skill(Path.Combine(Home, ".agents", "skills", "demo"));
        var analysis = Analyze("gemini", workspace, user);
        Assert.Equal("unknown", analysis.Candidates.Single(row => row.EntryPath == workspace.ResolvedPath).Expected.State);
        Assert.NotEqual("shadowed", analysis.Candidates.Single(row => row.EntryPath == user.ResolvedPath).Expected.Selection);
    }

    [Theory]
    [InlineData("\uFEFF---\nname: demo\ndescription: Rejected BOM\n---\n")]
    [InlineData("---\nname: demo\ndescription: Missing dash closure\n...\n")]
    [InlineData("---\r\nname: demo\r\ndescription: Missing dash closure\r\n...\r\n")]
    public void RejectedGeminiFramingCannotShadowAValidUserCopy(string content)
    {
        var workspace = Skill(Path.Combine(Project, ".agents", "skills", "demo"));
        Write(Path.Combine(workspace.ResolvedPath, "SKILL.md"), content);
        var user = Skill(Path.Combine(Home, ".agents", "skills", "demo"));
        var analysis = Analyze("gemini", workspace, user);
        var rejected = analysis.Candidates.Single(row => row.EntryPath == workspace.ResolvedPath);
        Assert.Equal("incompatible", rejected.Expected.State); Assert.Equal("no", rejected.Expected.Discoverability);
        Assert.Equal("no", rejected.Expected.ManualInvocation);
        var selected = analysis.Candidates.Single(row => row.EntryPath == user.ResolvedPath);
        Assert.Equal("selected", selected.Expected.Selection); Assert.Equal("yes", selected.Expected.ManualInvocation);
        Assert.Equal(2, analysis.Candidates.Length);
    }

    [Fact]
    public void BoundedYamlEndDoesNotProveGeminiRejectedAnUnobservedLaterDashClosure()
    {
        var workspace = Skill(Path.Combine(Project, ".agents", "skills", "demo"));
        Write(Path.Combine(workspace.ResolvedPath, "SKILL.md"), "---\nname: demo\ndescription: Conditional framing\n...\n" + new string('x', 9000) + "\n---\n");
        var user = Skill(Path.Combine(Home, ".agents", "skills", "demo"));
        var analysis = Analyze("gemini", workspace, user);
        Assert.Equal("unknown", analysis.Candidates.Single(row => row.EntryPath == workspace.ResolvedPath).Expected.State);
        Assert.NotEqual("shadowed", analysis.Candidates.Single(row => row.EntryPath == user.ResolvedPath).Expected.Selection);
        Write(Path.Combine(workspace.ResolvedPath, "SKILL.md"), "---\nname: demo\ndescription: Conditional framing\n...\n");
        var complete = Analyze("gemini", workspace, user);
        Assert.NotEqual(analysis.Context.ContextId, complete.Context.ContextId);
        Assert.Equal("incompatible", complete.Candidates.Single(row => row.EntryPath == workspace.ResolvedPath).Expected.State);
    }

    [Fact]
    public void MalformedAbsoluteCodexOverrideRetainsEveryCandidateWithoutFailingTheAnalysis()
    {
        var first = Skill(Path.Combine(Home, ".agents", "skills", "demo"));
        var second = Skill(Path.Combine(Project, ".agents", "skills", "other"), "name: other\ndescription: Other\n");
        var path = Path.Combine(first.ResolvedPath, "invalid") + "\0suffix";
        Write(Path.Combine(Home, ".codex", "config.toml"), "[[skills.config]]\npath = " + global::System.Text.Json.JsonSerializer.Serialize(path) + "\nenabled = false\n");
        var configuration = Configuration("codex");
        Assert.Equal("observed", configuration.Settings.Layers.Single(layer => layer.Scope == "user").Document.Evidence.State);
        var result = EffectiveSkillLoader.Capture(Inventory(first, second), configuration, Token);
        Assert.Equal(2, result.Candidates.Length);
        Assert.All(result.Candidates, row => Assert.Equal("unknown", row.Expected.State));
    }

    [Fact]
    public void GeminiNamesAreSanitizedAndDeepNamespaceContainersRemainOutsideNativeDiscovery()
    {
        var skill = Skill(Path.Combine(Home, ".agents", "skills", "demo"), "name: foo:bar\ndescription: Native sanitized name\n");
        var deep = Skill(Path.Combine(Home, ".agents", "skills", "namespace", "nested"));
        var root = Skill(Path.Combine(Home, ".gemini", "skills"));
        Write(Path.Combine(Home, ".gemini", "settings.json"), """{"skills":{"disabled":["foo-bar"]}}""");
        var analysis = Analyze("gemini", skill, deep, root);
        var sanitized = analysis.Candidates.Single(row => row.EntryPath == skill.ResolvedPath);
        Assert.Equal("foo-bar", sanitized.InvocationName); Assert.Equal("disabled", sanitized.Expected.State);
        Assert.Equal("outside-documented-roots", analysis.Candidates.Single(row => row.EntryPath == deep.ResolvedPath).Placement);
        Assert.Equal("user-gemini", analysis.Candidates.Single(row => row.EntryPath == root.ResolvedPath).Placement);
    }

    [Theory]
    [InlineData("codex", "coexists")]
    [InlineData("opencode", "ambiguous-same-name")]
    public void EqualNamesDoNotInheritUniversalProjectPrecedence(string agent, string selection)
    {
        var user = Skill(Path.Combine(Home, ".agents", "skills", "demo"));
        var project = Skill(Path.Combine(Project, ".agents", "skills", "demo"));
        var analysis = Analyze(agent, user, project);
        Assert.All(analysis.Candidates, row => Assert.Equal(selection, row.Expected.Selection));
        Assert.Equal(2, analysis.Candidates.Select(row => row.Subject.InstallationId).Distinct().Count());
    }

    [Fact]
    public void OpenCodePortableMismatchDoesNotBecomeAProvenNativeRefusal()
    {
        var skill = Skill(Path.Combine(Home, ".agents", "skills", "folder"), "name: different\n");
        var row = Assert.Single(Analyze("opencode", skill).Candidates);
        Assert.Equal("yes", row.Expected.ManualInvocation);
        Assert.Equal("no", row.Expected.AutomaticInvocation);
        Assert.NotEqual("incompatible", row.Expected.State);
        Assert.NotEmpty(row.PortableDiagnostics);
    }

    [Fact]
    public void PhysicalPayloadAliasesKeepEveryExposureAndContextualEntryKey()
    {
        var payload = Path.Combine(_temporary.FullName, "payload");
        var one = Path.Combine(Home, ".agents", "skills", "one");
        var two = Path.Combine(Home, ".agents", "skills", "two");
        var skill = Skill(payload, "name: demo\ndescription: Shared payload\n", new("codex", one, true, Scope.User), new("gemini", one, true, Scope.User), new("codex", two, true, Scope.User));
        var analysis = Analyze("codex", skill);
        Assert.Equal(2, analysis.Candidates.Length);
        Assert.Single(analysis.Candidates.Select(row => row.Subject.InstallationId).Distinct());
        Assert.Equal(2, analysis.Candidates.Select(row => row.LoadingKey).Distinct().Count());
        Assert.Equal(3, analysis.Candidates.Sum(row => row.Exposures.Length));
        Assert.All(analysis.Candidates, row => Assert.Equal(3, row.Subject.Exposures.Length));
    }

    [Fact]
    public void NativeOmissionsDoNotEraseFilesystemCandidatesAndWrongContextIsRejected()
    {
        var user = Skill(Path.Combine(Home, ".agents", "skills", "demo"));
        var inventory = Inventory(user); var configuration = Configuration("gemini");
        var initial = EffectiveSkillLoader.Capture(inventory, configuration, Token);
        var native = new NativeLoadingObservation("gemini-cli", "observed", initial.Context.ContextId, "0.46.0", "fixture-validated-schema", DateTimeOffset.UtcNow, "partial", [], []);
        var observed = EffectiveSkillLoader.Capture(inventory, configuration, Token, native);
        Assert.Single(observed.Candidates); Assert.Empty(observed.Candidates[0].NativeObservations); Assert.False(observed.Complete);
        var wrong = EffectiveSkillLoader.Capture(inventory, configuration, Token, native with { ContextId = "other-project" });
        Assert.Equal("unavailable", wrong.Native.State); Assert.Single(wrong.Candidates);
    }

    [Fact]
    public void ConfigurationAndMetadataChangesInvalidateContextWithoutChangingPhysicalIdentity()
    {
        var skill = Skill(Path.Combine(Home, ".agents", "skills", "demo"));
        var first = Analyze("codex", skill);
        Write(Path.Combine(skill.ResolvedPath, "agents", "openai.yaml"), "policy:\n  allow_implicit_invocation: false\n");
        var second = Analyze("codex", skill);
        Assert.NotEqual(first.Context.ContextId, second.Context.ContextId);
        Assert.Equal(first.Candidates[0].Subject.InstallationId, second.Candidates[0].Subject.InstallationId);
        Assert.Equal("no", second.Candidates[0].Expected.AutomaticInvocation);
        Assert.Equal("yes", second.Candidates[0].Expected.ManualInvocation);
    }

    [Fact]
    public void BrokenAndUnsupportedHeadersRemainVisibleAndCannotEstablishCompleteAnalysis()
    {
        var skill = Skill(Path.Combine(Home, ".agents", "skills", "demo"));
        Write(Path.Combine(skill.ResolvedPath, "SKILL.md"), "---\nname: !unsupported demo\n---\n");
        var row = Assert.Single(Analyze("codex", skill).Candidates);
        Assert.Equal("unknown", row.Expected.State);
        File.Delete(Path.Combine(skill.ResolvedPath, "SKILL.md"));
        var broken = Assert.Single(Analyze("codex", skill).Candidates);
        Assert.Equal("broken", broken.Expected.State); Assert.Equal("no", broken.Expected.ManualInvocation);
    }

    [Fact]
    public void ConcretePluginManifestAndApplicableRegistrationOutrankBroadProtectionAndOtherProjects()
    {
        var storage = Path.Combine(Home, ".claude", "plugins");
        var payload = Path.Combine(storage, "cache", "market", "distributor", "1.0");
        Write(Path.Combine(payload, ".claude-plugin", "plugin.json"), """{"name":"actual-plugin","version":"1.0"}""");
        var skill = Skill(Path.Combine(payload, "skills", "demo"));
        Write(Path.Combine(Home, ".claude", "settings.json"), """{"enabledPlugins":{"distributor@market":true}}""");
        var broad = new ManagedSkillRegion(storage, "claude-plugin", "claude", "plugin storage");
        var other = new ManagedSkillRegion(payload, "claude-plugin", "claude", "registered plugin", "distributor@market", "1.0", Path.Combine(_temporary.FullName, "other-project"))
        { RegistrationScope = Scope.Project };
        var applicable = other with { Context = Project };
        var inventory = Inventory(skill) with { ManagedRegions = [broad, other, applicable] };
        var row = Assert.Single(EffectiveSkillLoader.Capture(inventory, Configuration("claude"), Token).Candidates);
        Assert.Equal("actual-plugin:demo", row.InvocationName);
        Assert.Equal("managed-plugin", row.Placement);
        Assert.Equal("eligible", row.Expected.State);
        Assert.NotEqual("distributor@market:demo", row.InvocationName);
        var cached = EffectiveSkillLoader.Capture(inventory with { ManagedRegions = [broad, applicable with { Kind = "plugin", RegistrationScope = null }] }, Configuration("claude"), Token);
        Assert.Equal("unknown", Assert.Single(cached.Candidates).Expected.State);
    }

    [Fact]
    public void ClaudeFrontmatterAliasDoesNotEraseADistinctDirectoryCommandAfterOneNameLoses()
    {
        var personal = Skill(Path.Combine(Home, ".claude", "skills", "demo"));
        var project = Skill(Path.Combine(Project, ".claude", "skills", "demo"), "name: another-command\ndescription: Alias\n");
        var row = Analyze("claude", personal, project).Candidates.Single(candidate => candidate.EntryPath == project.ResolvedPath);
        Assert.Equal("demo", row.InvocationName);
        Assert.Contains("another-command", row.InvocationAliases);
        Assert.Equal("unknown", row.Expected.ManualInvocation);
        Assert.Equal("directory-shadowed;alias-resolution-unknown", row.Expected.Selection);
    }

    [Fact]
    public void DisabledStateRemainsPrimaryWhenSelectionIsAlsoShadowed()
    {
        var personal = Skill(Path.Combine(Home, ".claude", "skills", "demo"));
        var project = Skill(Path.Combine(Project, ".claude", "skills", "demo"));
        Write(Path.Combine(Home, ".claude", "settings.json"), """{"skillOverrides":{"demo":"off"}}""");
        var analysis = Analyze("claude", personal, project);
        Assert.All(analysis.Candidates, candidate => Assert.Equal("disabled", candidate.Expected.State));
        Assert.Equal("shadowed", analysis.Candidates.Single(candidate => candidate.EntryPath == project.ResolvedPath).Expected.Selection);
    }

    [Fact]
    public void IncompleteHigherTierCoverageDoesNotProveTheHighestObservedCopyIsSelected()
    {
        var personal = Skill(Path.Combine(Home, ".claude", "skills", "demo"));
        var project = Skill(Path.Combine(Project, ".claude", "skills", "demo"));
        var inventory = Inventory(personal, project) with { Diagnostics = new() { InaccessiblePaths = [Path.Combine(SystemDirectory, ".claude", "skills")] } };
        var analysis = EffectiveSkillLoader.Capture(inventory, Configuration("claude"), Token);
        Assert.Equal("conditional-selection", analysis.Candidates.Single(candidate => candidate.EntryPath == personal.ResolvedPath).Expected.Selection);
        Assert.Equal("shadowed", analysis.Candidates.Single(candidate => candidate.EntryPath == project.ResolvedPath).Expected.Selection);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Contains("unobserved", StringComparison.Ordinal));
        Assert.False(analysis.Complete);
    }

    [Fact]
    public void NativeDisagreementsUseFinalSelectionAndPeersIdentifyTheWinningPath()
    {
        var personal = Skill(Path.Combine(Home, ".claude", "skills", "demo"));
        var project = Skill(Path.Combine(Project, ".claude", "skills", "demo"));
        var inventory = Inventory(personal, project); var configuration = Configuration("claude");
        var initial = EffectiveSkillLoader.Capture(inventory, configuration, Token);
        foreach (var enabled in new[] { false, true })
        {
            var native = new NativeLoadingObservation("claude-code", "observed", initial.Context.ContextId, null, "fixture-only", DateTimeOffset.UtcNow,
                "partial", [new(project.ResolvedPath, "demo", enabled, "demo", null)], []);
            var analysis = EffectiveSkillLoader.Capture(inventory, configuration, Token, native);
            var row = analysis.Candidates.Single(candidate => candidate.EntryPath == project.ResolvedPath);
            Assert.Equal(enabled, !row.Disagreements.IsEmpty);
            Assert.Contains(analysis.Candidates.Single(candidate => candidate.EntryPath == personal.ResolvedPath).LoadingKey, row.SelectionPeers);
        }
    }

    [Fact]
    public void OutsideContextRowsCannotConsumeTheBoundedPeerSample()
    {
        var unrelated = Enumerable.Range(0, 20).Select(index => Skill(Path.Combine(_temporary.FullName, "other-" + index, "demo"))).ToArray();
        var personal = Skill(Path.Combine(Home, ".claude", "skills", "demo"));
        var project = Skill(Path.Combine(Project, ".claude", "skills", "demo"));
        var analysis = Analyze("claude", [.. unrelated, personal, project]);
        var winner = analysis.Candidates.Single(candidate => candidate.EntryPath == personal.ResolvedPath);
        var shadowed = analysis.Candidates.Single(candidate => candidate.EntryPath == project.ResolvedPath);
        Assert.Equal(new[] { winner.LoadingKey }, shadowed.SelectionPeers);
        Assert.Equal("complete", shadowed.SelectionPeerCoverage);
        Assert.All(analysis.Candidates.Where(candidate => candidate.Placement == "outside-documented-roots"), candidate => Assert.Empty(candidate.SelectionPeers));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeDisabledIsNotAContradictionWhenIncompleteDiscoveryLeavesInvocationUnknown(bool competitor)
    {
        var personal = Skill(Path.Combine(Home, ".claude", "skills", "demo"));
        var skills = competitor ? new[] { personal, Skill(Path.Combine(Project, ".claude", "skills", "demo")) } : new[] { personal };
        var inventory = Inventory(skills) with { ConfiguredRootsComplete = false };
        var configuration = Configuration("claude");
        var initial = EffectiveSkillLoader.Capture(inventory, configuration, Token);
        var native = new NativeLoadingObservation("claude-code", "observed", initial.Context.ContextId, null, "fixture-only", DateTimeOffset.UtcNow,
            "partial", [new(personal.ResolvedPath, "demo", false, "demo", null)], []);
        var row = EffectiveSkillLoader.Capture(inventory, configuration, Token, native).Candidates.Single(candidate => candidate.EntryPath == personal.ResolvedPath);
        Assert.Equal("conditional-selection", row.Expected.Selection);
        Assert.Equal("unknown", row.Expected.ManualInvocation);
        Assert.Empty(row.Disagreements);
    }

    [Fact]
    public void RegistrationFactsParticipateInTheContextFingerprint()
    {
        var payload = Path.Combine(Home, ".claude", "plugins", "cache", "market", "distributor", "1.0");
        Write(Path.Combine(payload, ".claude-plugin", "plugin.json"), """{"name":"actual-plugin"}""");
        var skill = Skill(Path.Combine(payload, "skills", "demo"));
        Write(Path.Combine(Home, ".claude", "settings.json"), """{"enabledPlugins":{"distributor@market":true}}""");
        var region = new ManagedSkillRegion(payload, "claude-plugin", "claude", "plugin", "distributor@market", "1.0", Project);
        var configuration = Configuration("claude");
        var cached = EffectiveSkillLoader.Capture(Inventory(skill) with { ManagedRegions = [region] }, configuration, Token);
        var registered = EffectiveSkillLoader.Capture(Inventory(skill) with { ManagedRegions = [region with { Kind = "registered plugin", RegistrationScope = Scope.Project }] }, configuration, Token);
        Assert.NotEqual(cached.Context.ContextId, registered.Context.ContextId);
        Assert.NotEqual(cached.Candidates[0].LoadingKey, registered.Candidates[0].LoadingKey);
        Assert.Equal(cached.Candidates[0].Subject.InstallationId, registered.Candidates[0].Subject.InstallationId);
    }

    [Fact]
    public void GeminiLinkedExtensionRetainsUnprefixedNamesAndSavedWorkspaceEnablement()
    {
        var installed = Path.Combine(Home, ".gemini", "extensions", "external");
        var payload = Path.Combine(_temporary.FullName, "linked-payload");
        var extension = Skill(Path.Combine(payload, "skills", "demo"));
        Write(Path.Combine(installed, ".gemini-extension-install.json"), System.Text.Json.JsonSerializer.Serialize(new { type = "link", source = payload }));
        Write(Path.Combine(payload, "gemini-extension.json"), """{"name":"fixture-extension","version":"1.0"}""");
        var enablement = Path.Combine(Home, ".gemini", "extensions", "extension-enablement.json");
        Write(enablement, """{"fixture-extension":{"overrides":["!/*"]}}""");
        var disabled = Assert.Single(Analyze("gemini", extension).Candidates);
        Assert.Equal("gemini-extension", disabled.Placement); Assert.Equal("demo", disabled.InvocationName);
        Assert.Equal("disabled", disabled.Expected.State);
        Write(enablement, """{"fixture-extension":{"overrides":[]}}""");
        var user = Skill(Path.Combine(Home, ".agents", "skills", "demo"));
        var analysis = Analyze("gemini", extension, user);
        Assert.Equal("shadowed", analysis.Candidates.Single(row => row.EntryPath == extension.ResolvedPath).Expected.Selection);
        Assert.Equal("selected", analysis.Candidates.Single(row => row.EntryPath == user.ResolvedPath).Expected.Selection);
        Assert.Contains(analysis.Configuration, file => file.Path == Path.Combine(installed, ".gemini-extension-install.json"));
    }

    [Fact]
    public void MultipleGeminiDeliveriesRetainEveryManifestAndEnablementForOnePhysicalEntry()
    {
        var payload = Path.Combine(_temporary.FullName, "linked-payload");
        var skill = Skill(Path.Combine(payload, "skills", "demo"));
        Write(Path.Combine(payload, "gemini-extension.json"), """{"name":"fixture-extension","version":"1.0"}""");
        var configuration = Configuration("gemini") with
        {
            GeminiExtensions = [new("install-one", payload, "first", "1", false, []), new("install-two", payload, "second", "2", true, [])]
        };
        var row = Assert.Single(EffectiveSkillLoader.Capture(Inventory(skill), configuration, Token).Candidates);
        Assert.Equal(2, row.GeminiExtensions.Length);
        Assert.Equal(new bool?[] { false, true }, row.GeminiExtensions.Select(delivery => delivery.Enabled));
        Assert.Equal("unknown", row.Expected.ManualInvocation);
        var text = SkillView.Cli.CliDispatcher.RenderEffectiveText(EffectiveSkillLoader.Capture(Inventory(skill), configuration, Token));
        Assert.Contains("install-one", text); Assert.Contains("install-two", text);
    }

    [Fact]
    public void SyncedAccountCachesAndReservedOrdinaryNamesCannotBecomePersonalEligibility()
    {
        var cache = Skill(Path.Combine(Home, ".claude", "skills", "synced", "demo"));
        var reserved = Skill(Path.Combine(Home, ".claude", "skills", "anthropic-skills"));
        var analysis = Analyze("claude", cache, reserved);
        Assert.Equal("account-cache-unobserved", analysis.Candidates.Single(candidate => candidate.EntryPath == cache.ResolvedPath).Placement);
        Assert.Equal("unknown", analysis.Candidates.Single(candidate => candidate.EntryPath == cache.ResolvedPath).Expected.ManualInvocation);
        Assert.Equal("incompatible", analysis.Candidates.Single(candidate => candidate.EntryPath == reserved.ResolvedPath).Expected.State);
    }
}
