using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Gh;
using SkillView.Cli;
using SkillView.Diagnostics;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class ManagedDiscoveryInventoryTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-managed-discovery-");
    private string Home => Path.Combine(temporary.FullName, "home");
    private string Codex => Path.Combine(Home, ".codex");
    private string Claude => Path.Combine(Home, ".claude");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly Logger logger = new();
    public void Dispose() => temporary.Delete(true);
    private string DirectoryAt(string path) => Directory.CreateDirectory(Path.Combine(temporary.FullName, path)).FullName;
    private string Skill(string path, string name)
    {
        var folder = DirectoryAt(path);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: Managed fixture\n---\nInstructions\n");
        return folder;
    }
    private string Plugin(string agent, string version, string? declaration = null)
    {
        var root = DirectoryAt($"home/.{agent}/plugins/cache/fixture-market/fixture-plugin/{version}");
        var manifest = Directory.CreateDirectory(Path.Combine(root, agent == "codex" ? ".codex-plugin" : ".claude-plugin")).FullName;
        File.WriteAllText(Path.Combine(manifest, "plugin.json"), JsonSerializer.Serialize(new { name = "fixture-plugin", version, skills = declaration ?? "./skills/" }));
        return root;
    }
    private ProcessRunner Runner(IReadOnlyDictionary<string, string>? extra = null)
    {
        Directory.CreateDirectory(Home);
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = Home,
            ["USERPROFILE"] = Home,
            ["PATH"] = "",
            ["CODEX_HOME"] = Codex,
            ["CLAUDE_CONFIG_DIR"] = Claude,
            ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = Path.Combine(Claude, "plugins"),
            ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
            ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
            ["PI_CODING_AGENT_DIR"] = Path.Combine(Home, ".pi", "agent"),
            ["XDG_STATE_HOME"] = Path.Combine(Home, "state")
        };
        foreach (var pair in extra ?? ImmutableDictionary<string, string>.Empty) environment[pair.Key] = pair.Value;
        return new(logger, environment: environment, defaultWorkingDirectory: temporary.FullName);
    }
    private Task<InventorySnapshot> Capture(IReadOnlyList<string>? roots = null, ProcessRunner? runner = null, string? ghPayload = null,
        IReadOnlyList<ScanRootResolver.AgentProfileRoot>? profiles = null)
    {
        var adapter = new GhSkillListAdapter((executable, arguments, token) => Task.FromResult(new ProcessResult(executable, arguments, 0, ghPayload ?? "[]", "", TimeSpan.Zero)), logger);
        return new LocalInventoryService(new(), new(logger), adapter, logger, runner ?? Runner())
            .CaptureAsync("fixture", new(roots ?? [], false, Profiles: profiles), Token);
    }

    [Fact]
    public async Task DefaultInventoryObservesBuiltinsAndEveryCachedVersionWithReadOnlyOwners()
    {
        Skill("home/.agents/skills/control", "control");
        Skill("home/.codex/skills/.system/builtin", "builtin");
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var root = Plugin("codex", version);
            Skill(Path.Combine(root, "skills", "demo"), "demo");
        }
        var claude = Plugin("claude", "3.0.0");
        Skill(Path.Combine(claude, "skills", "demo"), "demo");
        var inventory = await Capture();
        Assert.Equal(5, inventory.Skills.Length);
        Assert.False(inventory.IsPartial);
        Assert.Empty(Assert.Single(inventory.Skills, s => s.Name == "control").AllOwnershipClaims);
        foreach (var skill in inventory.Skills.Where(s => s.Name != "control"))
        {
            var owner = Assert.Single(skill.AllOwnershipClaims);
            Assert.True(owner.ReadOnly);
            Assert.True(owner.IsExclusiveOwner);
            Assert.Null(owner.LockFilePath);
            Assert.Null(skill.SourceIdentity.Origin);
            Assert.All(skill.Agents, agent => Assert.Null(agent.Enabled));
        }
        Assert.Equal(["1.0.0", "2.0.0", "3.0.0"], inventory.Skills.Where(s => s.Name == "demo").Select(s => s.Package!.PackageVersion).Order());
        Assert.All(inventory.Skills, skill => Assert.Equal(ValidityState.Valid, skill.Validity));
        using var json = JsonDocument.Parse(CliDispatcher.RenderListJson(inventory));
        Assert.NotEmpty(json.RootElement.GetProperty("managedRegions").EnumerateArray());
        var packaged = json.RootElement.GetProperty("skills").EnumerateArray().First(skill => skill.GetProperty("name").GetString() == "demo");
        Assert.True(packaged.GetProperty("ownerRecords")[0].GetProperty("readOnly").GetBoolean());
        Assert.Equal(JsonValueKind.Null, packaged.GetProperty("agents")[0].GetProperty("enabled").ValueKind);
        var detail = InstalledScreen.RenderDetail(inventory.Skills.First(skill => skill.Name == "demo"));
        Assert.Contains("Distributor version", detail);
        Assert.Contains("Read-only; use the owning agent", detail);
        Assert.DoesNotContain("active session state unknown", detail);
        Assert.Contains("active session state unknown", InstalledScreen.RenderDetail(inventory.Skills.First(skill => skill.Name == "demo"), showDetails: true));
        var report = new EnvironmentReport
        {
            GhPath = "/fixture/gh",
            GhVersionRaw = "gh version 2.102.0",
            GhVersion = new(2, 102, 0),
            GhMeetsMinimum = true,
            Auth = GhAuthStatus.Unknown,
            GhSkillAvailable = true,
            LogDirectory = null
        };
        var doctor = DoctorScreen.Render(report, inventory);
        Assert.Contains("4 managed skill installation(s)", doctor);
        Assert.Contains("codex-builtin", doctor);
        Assert.Contains("claude-plugin", doctor);
        Assert.Contains("Read-only resources; enabled and active session state are unknown", doctor);
    }

    [Fact]
    public void DirectInstallInsideManagedVersionResourcesIsRefusedWithoutALockfile()
    {
        var version = Plugin("codex", "1.0.0");
        var destination = Directory.CreateDirectory(Path.Combine(version, "resources", "skills")).FullName;
        var runner = Runner();
        var options = new GhSkillInstallService.Options(Path: destination);
        var error = Assert.Throws<IOException>(() =>
        {
            var context = GhInstallOwnershipGuard.Capture([destination], runner, options, logger, Token);
            GhInstallOwnershipGuard.Validate(context, [destination], ["demo"], Token);
        });
        Assert.Contains("managed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task DeclaredSingleSkillIsObservedWithoutInventingMalformedResourceSkills()
    {
        var root = Plugin("codex", "1.0.0", "./single");
        var payload = Skill(Path.Combine(root, "single"), "single");
        Directory.CreateDirectory(Path.Combine(payload, "scripts"));
        Directory.CreateDirectory(Path.Combine(payload, "references"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        var snapshot = await Capture();
        Assert.True(OwnershipPath.Same(payload, Assert.Single(snapshot.Skills).ResolvedPath));
        Assert.Equal(ValidityState.Valid, snapshot.Skills[0].Validity);
        Assert.True(Assert.Single(snapshot.Skills[0].AllOwnershipClaims).ReadOnly);
        Assert.False(snapshot.IsPartial);
    }

    [Fact]
    public void AliasedOrdinaryRootRequiresSelectorsAndRefusesOnlyItsManagedChild()
    {
        var root = DirectoryAt("ordinary");
        var plugin = DirectoryAt("ordinary/managed-child");
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), "{\"name\":\"fixture-plugin\"}");
        var alias = Path.Combine(temporary.FullName, "alias");
        Directory.CreateSymbolicLink(alias, root);
        var context = GhInstallOwnershipGuard.Capture([alias], Runner(), new(Path: alias), logger, Token);
        Assert.True(context.NeedsManagedSelectors([alias]));
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.Validate(context, [alias], null, Token));
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.Validate(context, [alias], ["managed-child"], Token));
        GhInstallOwnershipGuard.Validate(context, [alias], ["ordinary-child"], Token);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root), path => Path.GetFileName(path) != "managed-child");
    }

    [Fact]
    public async Task AliasedPluginRootRetainsCustomExposureAndDoesNotScanResources()
    {
        var root = Plugin("codex", "1.0.0");
        Skill(Path.Combine(root, "skills", "demo"), "demo");
        Directory.CreateDirectory(Path.Combine(root, "assets", "empty"));
        var alias = Path.Combine(temporary.FullName, "plugin-alias");
        Directory.CreateSymbolicLink(alias, root);
        var inventory = await Capture([alias]);
        var skill = Assert.Single(inventory.Skills);
        Assert.Equal(ValidityState.Valid, skill.Validity);
        Assert.Contains(skill.Agents, agent => agent.Scope == Scope.Custom && PathIdentity.IsInside(agent.Path, alias));
        Assert.False(inventory.IsPartial);
    }

    [Fact]
    public async Task DualPlatformPluginUsesEachProvidersManifest()
    {
        var root = DirectoryAt("dual-plugin");
        File.WriteAllText(Path.Combine(root, "plugin.json"), "{\"name\":\"dual\",\"skills\":\"./codex-only\"}");
        var claude = Directory.CreateDirectory(Path.Combine(root, ".claude-plugin")).FullName;
        File.WriteAllText(Path.Combine(claude, "plugin.json"), "{\"name\":\"dual\",\"skills\":\"./claude-only\"}");
        Skill(Path.Combine(root, "codex-only"), "codex-only");
        Skill(Path.Combine(root, "claude-only"), "claude-only");
        var inventory = await Capture([root]);
        Assert.Equal(["claude-only", "codex-only"], inventory.Skills.Select(skill => skill.Name).Order());
        Assert.DoesNotContain(Assert.Single(inventory.Skills, skill => skill.Name == "claude-only").Agents, agent => agent.AgentId == "codex");
        Assert.DoesNotContain(Assert.Single(inventory.Skills, skill => skill.Name == "codex-only").Agents, agent => AgentIdentity.Matches(agent.AgentId, "claude"));
        Assert.False(inventory.IsPartial);
        Assert.Contains(inventory.ScannedRoots, scan => scan.AgentHint == "claude" && scan.Path.EndsWith("claude-only", StringComparison.Ordinal));
        Assert.Contains(inventory.ScannedRoots, scan => scan.AgentHint == "codex" && scan.Path.EndsWith("codex-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OmittedExplicitPluginTopologyMakesInventoryPartialAndRefusesWrites()
    {
        var paths = Enumerable.Range(0, 33).Select(index => DirectoryAt("explicit/" + index)).ToArray();
        Skill(Path.Combine(paths[^1], "skills", "demo"), "demo");
        var runner = Runner(new Dictionary<string, string> { ["CLAUDE_CODE_PLUGIN_DIRS"] = string.Join(Path.PathSeparator, paths) });
        var inventory = await Capture(runner: runner);
        Assert.True(inventory.IsPartial);
        var destination = Path.Combine(paths[^1], "skills");
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.Capture([destination], runner, new(Path: destination), logger, Token));
        Assert.Throws<IOException>(() => ManagedSkillDiscovery.RefusePaths([Path.Combine(temporary.FullName, "unrelated")], Token, runner));
        Assert.True(File.Exists(Path.Combine(destination, "demo", "SKILL.md")));
    }

    [Fact]
    public async Task ExplicitMissingDefaultDeclarationIsPartialButOptionalDefaultIsNot()
    {
        Plugin("codex", "1.0.0", "./skills");
        Assert.True((await Capture()).IsPartial);
        var manifest = Path.Combine(Codex, "plugins", "cache", "fixture-market", "fixture-plugin", "1.0.0", ".codex-plugin", "plugin.json");
        File.WriteAllText(manifest, "{\"name\":\"fixture-plugin\"}");
        Assert.False((await Capture()).IsPartial);
    }

    [Fact]
    public void RemovalUsesCapturedCanonicalAddressWhenReviewedAncestorAliasMoves()
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var plugin = DirectoryAt("local-plugin");
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), "{\"name\":\"fixture\"}");
        var original = Skill(Path.Combine(plugin, "skills", "demo"), "demo");
        var replacement = Skill("ordinary/skills/demo", "demo");
        var alias = Path.Combine(temporary.FullName, "moving-alias");
        Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(original)!);
        var selected = Path.Combine(alias, "demo");
        var skill = new InstalledSkill
        {
            Name = "demo",
            ResolvedPath = selected,
            ScanRoot = alias,
            Scope = Scope.Custom,
            Agents = [new("unknown", selected, false)],
            FrontMatter = SkillFrontMatter.Empty,
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
            OwnershipRunner = Runner()
        };
        string? captured = null;
        var validation = RemoveValidator.ValidateWithBackendAvailabilityForTests(skill,
            [new(alias, Scope.Custom, null)], [skill], false, true, address =>
            {
                captured = address;
                Directory.Delete(alias);
                Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(replacement)!);
            });
        Assert.True(OwnershipPath.Same(captured!, original));
        Assert.Contains(validation.Errors, error => error.Kind == RemoveValidator.ErrorKind.OwnerRecordNeedsPlan);
        Assert.False(validation.Allowed);
        Assert.True(File.Exists(Path.Combine(original, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(replacement, "SKILL.md")));
    }

    [Fact]
    public async Task CleanupDoesNotTreatCacheVersionsOrPluginResourcesAsResidue()
    {
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var plugin = Plugin("codex", version);
            Skill(Path.Combine(plugin, "skills", "demo"), "demo");
            Directory.CreateDirectory(Path.Combine(plugin, "skills", "assets"));
        }
        var inventory = await Capture();
        Assert.Empty(CleanupClassifier.ClassifyWithCancellation(inventory, inventory.ScannedRoots, null, Token));
    }

    [Fact]
    public async Task RetargetedStorageAliasRetainsOriginalStatePlacementExclusion()
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var original = DirectoryAt("original-store");
        var replacement = DirectoryAt("replacement-store");
        var alias = Path.Combine(temporary.FullName, "storage-alias");
        Directory.CreateSymbolicLink(alias, original);
        var runner = Runner(new Dictionary<string, string> { ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = alias });
        var payload = Skill("ordinary/skills/control", "control");
        var inventory = await Capture([Path.GetDirectoryName(payload)!], runner);
        Assert.True(inventory.IsPartial);
        Directory.Delete(alias);
        Directory.CreateSymbolicLink(alias, replacement);
        var state = Path.Combine(original, "state");
        Assert.Throws<IOException>(() => NativeStateDirectory.CheckExcluded(
            OwnershipPath.CanonicalCandidate(state)!, inventory.ProtectedRoots.Select(root => root.Path).ToArray()));
        Assert.Throws<IOException>(() => new QuarantineStore(state, logger, runner: runner)
            .Preview(Assert.Single(inventory.Skills), inventory, Token));
        Assert.False(Directory.Exists(state));
        Assert.True(File.Exists(Path.Combine(payload, "SKILL.md")));
    }

    [Fact]
    public async Task ClaudeRegistryRetainsEveryInstallationContextWithoutClaimingLoading()
    {
        var plugin = Plugin("claude", "1.0.0");
        Skill(Path.Combine(plugin, "skills", "demo"), "demo");
        var projects = new[] { DirectoryAt("project-one"), DirectoryAt("project-two") };
        var records = projects.Select(project => new { scope = "project", projectPath = project, installPath = plugin, version = "1.0.0" }).ToArray();
        File.WriteAllText(Path.Combine(Claude, "plugins", "installed_plugins.json"), JsonSerializer.Serialize(new
        { version = 2, plugins = new Dictionary<string, object> { ["fixture-plugin@fixture-market"] = records } }));
        var ghPayload = JsonSerializer.Serialize(new[] { new { skillName = "demo", path = Path.Combine(plugin, "skills", "demo"), agentHosts = new[] { "claude-code" }, scope = "user" } });
        var inventory = await Capture([Path.Combine(plugin, "skills")], ghPayload: ghPayload);
        var skill = Assert.Single(inventory.Skills);
        Assert.False(inventory.IsPartial);
        foreach (var project in projects)
        {
            Assert.Contains(skill.Agents, agent => agent.Scope == Scope.Project && agent.Context == project && agent.Enabled is null);
            Assert.Contains(skill.AllOwnershipClaims, owner => owner.ContextRoot == project && owner.ReadOnly && owner.LockFilePath is null);
        }
        Assert.Equal(3, skill.AllOwnershipClaims.Count());
        Assert.Contains(skill.AllOwnershipClaims, owner => owner.ContextRoot == plugin && owner.ManagedRegion?.RegistrationPath is null);
        Assert.DoesNotContain(skill.Agents, agent => agent.Context is null);
        using var json = JsonDocument.Parse(CliDispatcher.RenderListJson(inventory));
        var contexts = json.RootElement.GetProperty("skills")[0].GetProperty("agents").EnumerateArray()
            .Where(agent => agent.GetProperty("scope").GetString() == "Project").Select(agent => agent.GetProperty("context").GetString());
        Assert.Equal(projects.Order(), contexts.Distinct().Order());
    }

    [Fact]
    public async Task ExternalManifestlessRegisteredPluginIsProtectedWithoutBeingCrawled()
    {
        var plugin = DirectoryAt("external-plugin");
        Skill(Path.Combine(plugin, "skills", "demo"), "demo");
        Directory.CreateDirectory(Path.Combine(Claude, "plugins"));
        File.WriteAllText(Path.Combine(Claude, "plugins", "installed_plugins.json"), JsonSerializer.Serialize(new
        {
            version = 2,
            plugins = new Dictionary<string, object>
            { ["fixture-plugin@fixture-market"] = new[] { new { scope = "user", installPath = plugin, version = "1.0.0" } } }
        }));
        var runner = Runner();
        var hidden = await Capture(runner: runner);
        Assert.True(hidden.IsPartial);
        Assert.Empty(hidden.Skills);
        Assert.Throws<IOException>(() => ManagedSkillDiscovery.RefusePaths([Path.Combine(plugin, "resources")], Token, runner));
        var explicitInventory = await Capture([plugin], runner);
        Assert.False(explicitInventory.IsPartial);
        Assert.Equal("demo", Assert.Single(explicitInventory.Skills).Name);
        Assert.Equal(2, explicitInventory.Skills[0].AllOwnershipClaims.Count());
        Assert.All(explicitInventory.Skills[0].AllOwnershipClaims, owner => Assert.True(owner.ReadOnly));
        Assert.Equal(new[] { plugin, Path.Combine(Claude, "plugins") }.Order(), explicitInventory.Skills[0].AllOwnershipClaims.Select(owner => owner.ContextRoot).Order());
    }

    [Theory]
    [InlineData("{", "malformed")]
    [InlineData("{\"name\":\"one\",\"name\":\"two\"}", "duplicate")]
    [InlineData("{\"name\":\"fixture\",\"skills\":{}}", "unsupported")]
    [InlineData("{\"name\":\"fixture\",\"skills\":\"./../outside\"}", "escaping")]
    public async Task UnreadablePluginMetadataKeepsItsBoundaryAndExactBuiltinCoverage(string manifest, string kind)
    {
        var plugin = Plugin("codex", "1.0.0");
        File.WriteAllText(Path.Combine(plugin, ".codex-plugin", "plugin.json"), manifest);
        Skill("home/.codex/skills/.system/builtin", "builtin");
        var inventory = await Capture();
        Assert.True(inventory.IsPartial, kind);
        Assert.Contains(inventory.Diagnostics.Locations, location => location.Path.EndsWith(".system", StringComparison.Ordinal) && location.State == "managed");
        Assert.Throws<IOException>(() => ManagedSkillDiscovery.RefusePaths([Path.Combine(plugin, "new-resource")], Token, Runner()));
        var unrelated = DirectoryAt("ordinary-destination");
        var context = GhInstallOwnershipGuard.Capture([unrelated], Runner(), new(Path: unrelated), logger, Token);
        GhInstallOwnershipGuard.Validate(context, [unrelated], ["new-skill"], Token);
    }

    [Fact]
    public async Task RelativeCodexHomeUsesRunnerCwdAndRetainsDefaultBuiltinRoot()
    {
        Skill("home/.codex/skills/.system/default-builtin", "default-builtin");
        Skill("alternate-codex/skills/.system/alternate-builtin", "alternate-builtin");
        var runner = Runner(new Dictionary<string, string> { ["CODEX_HOME"] = "alternate-codex" });
        var inventory = await Capture(runner: runner);
        Assert.Equal(2, inventory.Skills.Length);
        Assert.False(inventory.IsPartial);
        Assert.All(inventory.Skills, skill => Assert.Equal("codex-builtin", Assert.Single(skill.AllOwnershipClaims).Provider));
        Assert.Throws<IOException>(() => ManagedSkillDiscovery.RefusePaths([Path.Combine(temporary.FullName, "alternate-codex", "skills", ".system", "future")], Token, runner));
    }

    [Fact]
    public void InstallCannotReplaceNamespaceContainingManagedPlugin()
    {
        var destination = DirectoryAt("ordinary-destination");
        var nested = Directory.CreateDirectory(Path.Combine(destination, "namespace", "plugin")).FullName;
        File.WriteAllText(Path.Combine(nested, "plugin.json"), "{\"name\":\"fixture\"}");
        var context = GhInstallOwnershipGuard.Capture([destination], Runner(), new(Path: destination), logger, Token);
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.Validate(context, [destination], ["namespace"], Token));
        GhInstallOwnershipGuard.Validate(context, [destination], ["unrelated"], Token);
        Assert.True(File.Exists(Path.Combine(nested, "plugin.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredExternalTraversalRequiresExplicitPhysicalScopeAndDoesNotExpandIt(bool linkedCache)
    {
        var plugin = DirectoryAt("external-plugin");
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{\"name\":\"fixture\",\"skills\":\"./private\"}");
        var selected = Skill(Path.Combine(plugin, "skills", "one"), "one");
        Skill(Path.Combine(plugin, "skills", "two"), "two");
        Skill(Path.Combine(plugin, "private", "three"), "three");
        Directory.CreateDirectory(Path.Combine(Claude, "plugins"));
        var install = plugin;
        if (linkedCache)
        {
            var parent = Directory.CreateDirectory(Path.Combine(Claude, "plugins", "cache", "market", "fixture")).FullName;
            install = Path.Combine(parent, "1.0.0");
            Directory.CreateSymbolicLink(install, plugin);
        }
        File.WriteAllText(Path.Combine(Claude, "plugins", "installed_plugins.json"), JsonSerializer.Serialize(new
        { version = 2, plugins = new Dictionary<string, object> { ["fixture@market"] = new[] { new { scope = "user", installPath = install, version = "1.0.0" } } } }));
        var unseen = await Capture();
        Assert.Empty(unseen.Skills);
        Assert.True(unseen.IsPartial);
        Assert.Throws<IOException>(() => ManagedSkillDiscovery.RefusePaths([Path.Combine(plugin, "private")], Token, Runner()));
        var narrow = await Capture([selected]);
        Assert.Equal("one", Assert.Single(narrow.Skills).Name);
        Assert.DoesNotContain(narrow.ScannedRoots, root => Path.GetFileName(root.Path) is "private" or "two");
        Assert.DoesNotContain(Assert.Single(narrow.Providers, provider => provider.Provider == "managed").CoveredRoots,
            path => OwnershipPath.Same(path, plugin));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutableAllSetBesideManagedChildIsRefusedBeforeAnySourceProcess(bool local)
    {
        var destination = DirectoryAt("ordinary-destination");
        var child = Directory.CreateDirectory(Path.Combine(destination, "managed-child")).FullName;
        File.WriteAllText(Path.Combine(child, "plugin.json"), "{\"name\":\"fixture\"}");
        var result = await new GhSkillInstallService(Runner(), logger).InstallAsync(
            Path.Combine(temporary.FullName, "must-not-launch"), "fixture/source", null,
            new(Path: destination, All: true, FromLocal: local), Token);
        Assert.False(result.Succeeded);
        Assert.Contains("immutable staged", result.ErrorMessage);
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ManagedChildAppearingDuringSourceListingRefusesFinalAllInvocation()
    {
        if (OperatingSystem.IsWindows()) return; // Portable initial refusal is covered above and by native proofs.
        var destination = DirectoryAt("ordinary-destination");
        Directory.CreateDirectory(Path.Combine(Home, ".agents"));
        File.WriteAllText(Path.Combine(Home, ".agents", ".skill-lock.json"),
            "{\"version\":3,\"skills\":{\"existing\":{\"source\":\"original/repo\",\"sourceType\":\"github\"}}}");
        var script = Path.Combine(temporary.FullName, "gh-fixture");
        File.WriteAllText(script, "#!/bin/sh\nfor arg do\n case \"$arg\" in --all) : > \"$SKILLVIEW_MUTATED\"; exit 0;; esac\ndone\n/bin/mkdir -p \"$SKILLVIEW_DESTINATION/appeared\"\nprintf '{\"name\":\"fixture\"}' > \"$SKILLVIEW_DESTINATION/appeared/plugin.json\"\nprintf 'new-skill\\tFixture\\n'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var marker = Path.Combine(temporary.FullName, "mutated");
        var runner = Runner(new Dictionary<string, string> { ["SKILLVIEW_DESTINATION"] = destination, ["SKILLVIEW_MUTATED"] = marker });
        var result = await new GhSkillInstallService(runner, logger).InstallAsync(script, "fixture/source", null,
            new(Path: destination, All: true), Token);
        Assert.False(result.Succeeded);
        Assert.Contains("immutable staged", result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(destination, "appeared", "plugin.json")));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ManagedLatestPlanRefusesBeforeResolvingOrLaunchingRemoteSource()
    {
        var plugin = Plugin("codex", "1.0.0");
        Skill(Path.Combine(plugin, "skills", "demo"), "demo");
        var inventory = await Capture();
        var error = await Assert.ThrowsAsync<IOException>(() => new ReplacementPlanStore(
            Path.Combine(temporary.FullName, "state"), logger, runner: Runner()).PlanLatestAsync(
                Path.Combine(temporary.FullName, "must-not-launch"), Assert.Single(inventory.Skills), inventory, false, Token));
        Assert.Contains("managed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(temporary.FullName, "state")));
    }

    private void RegisterExternal(string path)
    {
        Directory.CreateDirectory(Path.Combine(Claude, "plugins"));
        File.WriteAllText(Path.Combine(Claude, "plugins", "installed_plugins.json"), JsonSerializer.Serialize(new
        { version = 2, plugins = new Dictionary<string, object> { ["fixture@market"] = new[] { new { scope = "user", installPath = path, version = "1.0.0" } } } }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalRechecksExternalRegistryAdoptionAtExecution(bool emptyDirectory)
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var payload = emptyDirectory ? DirectoryAt("ordinary/empty") : Skill("ordinary/demo", "demo");
        var runner = Runner();
        var inventory = await Capture([Path.GetDirectoryName(payload)!], runner);
        var validation = emptyDirectory
            ? RemoveValidator.ValidateEmptyDirectory(payload, inventory.ProtectedRoots.ToArray(), Token, runner)
            : RemoveValidator.Validate(Assert.Single(inventory.Skills), inventory.ProtectedRoots.ToArray(), inventory.Skills, Token);
        Assert.True(validation.Allowed, string.Join("; ", validation.Errors));
        RegisterExternal(payload);
        var progress = new List<RemoveService.RemoveProgress>();
        var result = new RemoveService(logger).Remove(validation, cancellationToken: Token,
            progress: new InlineProgress<RemoveService.RemoveProgress>(progress.Add));
        Assert.False(result.Succeeded);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(0, result.DirectoriesDeleted);
        Assert.True(Directory.Exists(payload));
        Assert.Contains(result.Errors, error => error.Contains("managed", StringComparison.OrdinalIgnoreCase));
        Assert.True(progress[^1].IsCompleted);
    }

    [Theory]
    [InlineData("backup", false)]
    [InlineData("quarantine", false)]
    [InlineData("replacement", false)]
    [InlineData("backup", true)]
    [InlineData("quarantine", true)]
    [InlineData("replacement", true)]
    public async Task StaleInventoryCannotAuthorizeStateInsideNewExternalManagedRegistration(string operation, bool afterPayloadRead)
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var payload = Skill("ordinary/demo", "demo");
        if (operation == "replacement")
            File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/harder/fixture\n  github-ref: refs/heads/main\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n---\nBody\n");
        var runner = Runner();
        var inventory = await Capture([Path.GetDirectoryName(payload)!], runner);
        var target = Assert.Single(inventory.Skills);
        var adopted = DirectoryAt("new-managed-store");
        var state = Path.Combine(adopted, "state");
        Action? beforeOpen = afterPayloadRead ? () => RegisterExternal(adopted) : null;
        if (!afterPayloadRead) RegisterExternal(adopted);
        IOException error;
        if (operation == "backup")
            error = Assert.Throws<IOException>(() => new MaintenanceBackupStore(state, _ => { },
                beforeStateOpenForTests: beforeOpen, ownershipRunner: runner).Capture(target, Token));
        else if (operation == "quarantine")
        {
            var store = new QuarantineStore(state, logger, runner: runner, beforeStateOpenForTests: beforeOpen);
            if (afterPayloadRead)
            {
                var preview = store.Preview(target, inventory, Token);
                error = Assert.Throws<IOException>(() => store.Apply(target, inventory, preview.ReviewFingerprint, true, Token));
            }
            else error = Assert.Throws<IOException>(() => store.Preview(target, inventory, Token));
        }
        else
            error = await Assert.ThrowsAsync<IOException>(() => new ReplacementPlanStore(state, logger, runner: runner, beforeStateOpenForTests: beforeOpen)
                .PlanAsync(target, inventory, (_, _) => throw new InvalidOperationException("Staging must not run"), Token));
        Assert.Contains("managed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(state));
        Assert.True(File.Exists(Path.Combine(payload, "SKILL.md")));
    }

    [Fact]
    public void PrivateStagingOverridesHostManagedHomesAndExplicitDirectories()
    {
        var codex = DirectoryAt("external-codex");
        Skill(Path.Combine(codex, "skills", ".system", "host-builtin"), "host-builtin");
        var external = DirectoryAt("external-plugin");
        Skill(Path.Combine(external, "skills", "host-plugin"), "host-plugin");
        var runner = Runner(new Dictionary<string, string>
        {
            ["CODEX_HOME"] = codex,
            ["CLAUDE_CODE_PLUGIN_DIRS"] = external,
            ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = external,
            ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = external
        });
        var isolated = SkillUpdateCoordinator.CreateStagingEnvironment(Path.Combine(temporary.FullName, "private-stage"));
        var observed = ManagedSkillDiscovery.Capture(runner, [], Token, isolated);
        Assert.Empty(observed.Roots);
        Assert.True(observed.Complete);
        Assert.DoesNotContain(observed.Regions, region => region.Covers(external) || region.Covers(codex));
    }

    [Fact]
    public async Task ExplicitCrossAgentProfileRetainsItsAgentAndBoundsExternalManagedDiscovery()
    {
        var plugin = DirectoryAt("external-claude-plugin");
        var selected = Skill(Path.Combine(plugin, "skills", "selected"), "selected");
        Skill(Path.Combine(plugin, "skills", "unselected"), "unselected");
        RegisterExternal(plugin);
        var inventory = await Capture(profiles: [new("codex", selected, "explicit-codex")]);
        var skill = Assert.Single(inventory.Skills);
        Assert.Equal("selected", skill.Name);
        Assert.False(inventory.IsPartial);
        Assert.Contains(skill.Agents, agent => AgentIdentity.Matches(agent.AgentId, "codex") && agent.Profile == "explicit-codex");
        Assert.DoesNotContain(skill.Agents, agent => AgentIdentity.Matches(agent.AgentId, "claude") && agent.Profile == "explicit-codex");
        Assert.Equal(2, skill.AllOwnershipClaims.Count());
        Assert.All(skill.AllOwnershipClaims, owner => Assert.Equal("claude-plugin", owner.Provider));
    }

    [Fact]
    public async Task EmbeddedPluginRetainsBothCrossAgentProfilesAndAliasSpellings()
    {
        var plugin = DirectoryAt("embedded-claude-plugin");
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{\"name\":\"fixture\"}");
        Skill(Path.Combine(plugin, "skills", "demo"), "demo");
        var alias = Path.Combine(temporary.FullName, "profile-alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(plugin, "skills"));
        var inventory = await Capture(profiles: [new("codex", Path.Combine(plugin, "skills"), "one"), new("gemini-cli", alias, "two")]);
        var skill = Assert.Single(inventory.Skills);
        Assert.False(inventory.IsPartial);
        Assert.Contains(skill.Agents, agent => AgentIdentity.Matches(agent.AgentId, "codex") && agent.Profile == "one");
        Assert.Contains(skill.Agents, agent => AgentIdentity.Matches(agent.AgentId, "gemini-cli") && agent.Profile == "two" && PathIdentity.IsInside(agent.Path, alias));
        Assert.DoesNotContain(skill.Agents, agent => AgentIdentity.Matches(agent.AgentId, "claude") && agent.Profile is not null);
        Assert.Equal("local-claude-plugin", Assert.Single(skill.AllOwnershipClaims).Provider);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InaccessibleOrdinaryOrManagedAncestorReportsPartialWithoutHidingAccessibleControl(bool managedAncestor)
    {
        if (OperatingSystem.IsWindows()) return;
        var control = Skill("home/.agents/skills/control", "control");
        var denied = DirectoryAt("denied");
        var inaccessibleCodex = DirectoryAt("denied/codex");
        Skill("denied/codex/skills/.system/builtin", "builtin");
        var runner = Runner(managedAncestor ? new Dictionary<string, string> { ["CODEX_HOME"] = inaccessibleCodex } : null);
        var mode = File.GetUnixFileMode(denied);
        File.SetUnixFileMode(denied, UnixFileMode.None);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.GetAttributes(inaccessibleCodex));
            var inventory = await Capture(managedAncestor ? [] : [denied], runner);
            Assert.True(inventory.IsPartial);
            Assert.Equal(control, Assert.Single(inventory.Skills).ResolvedPath);
            Assert.Empty(inventory.Skills[0].AllOwnershipClaims);
            var cleanup = CleanupClassifier.ClassifyWithCancellation(inventory, inventory.ScannedRoots, null, Token);
            Assert.DoesNotContain(cleanup, candidate => candidate.Kind is CleanupClassifier.CandidateKind.Untracked
                or CleanupClassifier.CandidateKind.SourceOrphaned or CleanupClassifier.CandidateKind.Duplicate);
            if (managedAncestor)
            {
                Assert.Contains(inventory.ManagedRegions, region => PathIdentity.IsInside(region.Path, inaccessibleCodex));
                Assert.Contains(inventory.Providers, provider => provider.Provider == "managed" && !provider.Succeeded);
            }
            else
            {
                Assert.DoesNotContain(inventory.ManagedRegions, region => PathIdentity.Equals(region.Path, denied));
                Assert.Contains(inventory.Diagnostics.Locations, location => PathIdentity.Equals(location.Path, denied) && location.State == "inaccessible");
            }
            Assert.Throws<IOException>(() => ManagedSkillDiscovery.RefusePaths([managedAncestor ? control : denied], Token, runner));
        }
        finally { File.SetUnixFileMode(denied, mode); }
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("synced")]
    public async Task LinkedInitialPluginContainersAreProtectedWithoutImplicitExternalReads(string container)
    {
        var external = DirectoryAt("external");
        var plugin = container == "cache" ? DirectoryAt("external/market/plugin/1.0.0") : DirectoryAt("external/plugin");
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin/plugin.json"), "{\"name\":\"plugin\"}");
        var selected = Skill(Path.Combine(plugin, "skills/selected"), "selected");
        Skill(Path.Combine(plugin, "skills/unselected"), "unselected");
        Directory.CreateDirectory(Path.Combine(Claude, "plugins"));
        Directory.CreateSymbolicLink(Path.Combine(Claude, "plugins", container), external);
        var inventory = await Capture();
        Assert.Empty(inventory.Skills);
        Assert.True(inventory.IsPartial);
        Assert.Contains(inventory.ManagedRegions, region => region.ObservedCanonicalPath is { } address
            && PathIdentity.Equals(address, OwnershipPath.CanonicalCandidate(external)!));
        Assert.Throws<IOException>(() => ManagedSkillDiscovery.RefusePaths([Path.Combine(plugin, "resources")], Token, Runner()));
        var manifestless = Path.Combine(external, "private-resources");
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.Capture([manifestless], Runner(),
            new GhSkillInstallService.Options(Path: manifestless), logger, Token));
        var explicitInventory = await Capture([selected]);
        Assert.Equal("selected", Assert.Single(explicitInventory.Skills).Name);
        Assert.All(explicitInventory.Skills[0].AllOwnershipClaims, owner => Assert.True(owner.ReadOnly));
        Assert.DoesNotContain(explicitInventory.ScannedRoots, root => PathIdentity.Equals(root.Path, Path.Combine(plugin, "skills")));
    }

    [Fact]
    public async Task NarrowUnregisteredPluginScopeDoesNotReadSiblingSkills()
    {
        var plugin = DirectoryAt("local-plugin");
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin/plugin.json"), "{\"name\":\"plugin\",\"skills\":[\"./other\"]}");
        var selected = Skill(Path.Combine(plugin, "skills/selected"), "selected");
        Skill(Path.Combine(plugin, "skills/unselected"), "unselected");
        Skill(Path.Combine(plugin, "other/private"), "private");
        var inventory = await Capture([selected]);
        Assert.Equal("selected", Assert.Single(inventory.Skills).Name);
        Assert.DoesNotContain(inventory.ScannedRoots, root => PathIdentity.Equals(root.Path, Path.Combine(plugin, "skills"))
            || PathIdentity.Equals(root.Path, Path.Combine(plugin, "other")));
    }

    [Fact]
    public async Task OrdinaryRemovalCannotContainAKnownManagedPlugin()
    {
        var ordinary = Skill("ordinary/demo", "demo");
        var plugin = DirectoryAt("ordinary/demo/resources/plugin");
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin/plugin.json"), "{\"name\":\"plugin\"}");
        Skill(Path.Combine(plugin, "skills/bundled"), "bundled");
        var inventory = await Capture([Path.GetDirectoryName(ordinary)!, plugin]);
        var skill = Assert.Single(inventory.Skills, skill => skill.Name == "demo");
        var validation = RemoveValidator.Validate(skill, inventory.ProtectedRoots.ToArray(), inventory.Skills, Token);
        Assert.False(validation.Allowed);
        Assert.Contains(validation.Errors, error => error.Kind == RemoveValidator.ErrorKind.OwnerRecordNeedsPlan);
    }

    [Fact]
    public async Task ManagedPluginInsideNamespaceDoesNotInventMalformedContainerSkill()
    {
        var root = DirectoryAt("ordinary");
        var plugin = DirectoryAt("ordinary/namespace/plugin");
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin/plugin.json"), "{\"name\":\"plugin\"}");
        Skill(Path.Combine(plugin, "skills/demo"), "demo");
        var inventory = await Capture([root]);
        Assert.Equal("demo", Assert.Single(inventory.Skills).Name);
        Assert.All(inventory.Skills, skill => Assert.Equal(ValidityState.Valid, skill.Validity));
    }

    [Fact]
    public async Task NarrowProfileInsideCachedContainerKeepsOnlyItsSelectedExposureAndOriginalOwner()
    {
        var plugin = Plugin("claude", "1.0.0");
        var selected = Skill(Path.Combine(plugin, "skills/selected"), "selected");
        Skill(Path.Combine(plugin, "skills/unselected"), "unselected");
        var inventory = await Capture(profiles: [new("codex", selected, "narrow")]);
        var skill = Assert.Single(inventory.Skills, skill => skill.Name == "selected");
        Assert.Contains(skill.Agents, agent => AgentIdentity.Matches(agent.AgentId, "codex") && agent.Profile == "narrow");
        Assert.DoesNotContain(Assert.Single(inventory.Skills, skill => skill.Name == "unselected").Agents, agent => agent.Profile == "narrow");
        Assert.Equal("claude-plugin", Assert.Single(skill.AllOwnershipClaims).Provider);
        Assert.False(inventory.IsPartial);
    }

    [Fact]
    public async Task NestedSyncedLinkRetainsManifestlessExternalBoundaryInInventoryAndAdmission()
    {
        var external = DirectoryAt("external-manifestless");
        var selected = Skill(Path.Combine(external, "skills/selected"), "selected");
        Skill(Path.Combine(external, "skills/unselected"), "unselected");
        var parent = Directory.CreateDirectory(Path.Combine(Claude, "plugins/synced/market")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(parent, "plugin"), external);
        var inventory = await Capture();
        Assert.True(inventory.IsPartial);
        Assert.Empty(inventory.Skills);
        var destination = Path.Combine(external, "resources");
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.Capture([destination], Runner(),
            new GhSkillInstallService.Options(Path: destination), logger, Token));
        var explicitInventory = await Capture([selected]);
        var skill = Assert.Single(explicitInventory.Skills);
        Assert.Equal("selected", skill.Name);
        Assert.NotEmpty(skill.AllOwnershipClaims);
        Assert.All(skill.AllOwnershipClaims, owner => Assert.True(owner.ReadOnly));
        Assert.DoesNotContain(explicitInventory.ScannedRoots, root => PathIdentity.Equals(root.Path, Path.Combine(external, "skills")));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
