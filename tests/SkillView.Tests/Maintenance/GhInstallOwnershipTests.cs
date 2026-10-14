using System.Text.Json;
using SkillView.Cli;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class GhInstallOwnershipTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("skillview-install-owner-");
    public void Dispose() => _root.Delete(true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void XdgOwnedDestinationRemainsProtectedThroughAncestorAlias(bool missingDestination)
    {
        if (OperatingSystem.IsWindows()) return; // Portable direct-root policy covered by the next test.
        var home = Directory.CreateDirectory(Path.Combine(_root.FullName, "home")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(_root.FullName, "state", "skills"));
        var destination = Path.Combine(home, ".agents", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!missingDestination) Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(state.FullName, ".skill-lock.json"),
            "{\"version\":3,\"skills\":{\"demo\":{\"source\":\"owner/repo\",\"sourceType\":\"github\"}}}");
        var alias = Path.Combine(_root.FullName, "alias");
        Directory.CreateSymbolicLink(alias, home);
        var selected = Path.Combine(alias, ".agents", "skills");
        var logger = new Logger();
        var runner = new ProcessRunner(logger, environment: new Dictionary<string, string>
        { ["HOME"] = home, ["XDG_STATE_HOME"] = Path.GetDirectoryName(state.FullName)! });
        var context = GhInstallOwnershipGuard.Capture([selected], runner, new(Path: selected), logger, TestContext.Current.CancellationToken);
        Assert.Single(context.Claims);
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.Validate(context, [selected], ["demo"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void BrokenXdgOwnedLinkKeepsItsClaimThroughAncestorAlias()
    {
        if (OperatingSystem.IsWindows()) return;
        var home = Directory.CreateDirectory(Path.Combine(_root.FullName, "home")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(home, ".agents", "skills")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(_root.FullName, "state", "skills")).FullName;
        File.WriteAllText(Path.Combine(state, ".skill-lock.json"),
            "{\"version\":3,\"skills\":{\"demo\":{\"source\":\"owner/repo\",\"sourceType\":\"github\"}}}");
        Directory.CreateSymbolicLink(Path.Combine(root, "demo"), Path.Combine(_root.FullName, "missing"));
        var alias = Path.Combine(_root.FullName, "alias");
        Directory.CreateSymbolicLink(alias, root);
        var token = TestContext.Current.CancellationToken;
        var logger = new Logger();
        var observations = new SkillLockFileReader(logger).LoadObservations([alias], token, new(home, Path.GetDirectoryName(state)));
        var scans = new LocalSkillScanner(logger).Scan([new(alias, Scope.User, null)]);
        var enriched = LocalInventoryService.EnrichOwnership(scans, observations, token);
        Assert.Single(Assert.Single(enriched).OwnershipClaims);
        var candidates = CleanupClassifier.Classify(InventorySnapshot.Empty with { Skills = enriched }, [new(alias, Scope.User, null)]);
        Assert.True(Assert.Single(candidates).ReviewOnly);
    }

    [Fact]
    public void SharedLockReachedThroughAliasRetainsOneNonexclusiveProvider()
    {
        if (OperatingSystem.IsWindows()) return;
        var home = Directory.CreateDirectory(Path.Combine(_root.FullName, "real-home")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(home, ".agents", "skills")).FullName;
        File.WriteAllText(Path.Combine(home, ".agents", ".skill-lock.json"),
            "{\"version\":3,\"skills\":{\"demo\":{\"source\":\"owner/repo\",\"sourceType\":\"github\"}}}");
        var alias = Path.Combine(_root.FullName, "home-alias");
        Directory.CreateSymbolicLink(alias, home);
        var records = new SkillLockFileReader(new Logger()).LoadObservations([root, Path.Combine(alias, ".agents", "skills")],
            TestContext.Current.CancellationToken, new(alias));
        var claim = Assert.Single(records).Package;
        Assert.Equal("gh-vercel-global", claim.Provider);
        Assert.False(claim.IsExclusiveOwner);
    }

    [Fact]
    public void ProjectOwnerDiscoveryFollowsAliasToDocumentedContext()
    {
        if (OperatingSystem.IsWindows()) return;
        var home = Directory.CreateDirectory(Path.Combine(_root.FullName, "home")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root.FullName, "project", ".agents", "skills")).FullName;
        File.WriteAllText(Path.Combine(_root.FullName, "project", "skills-lock.json"),
            "{\"version\":1,\"skills\":{\"demo\":{\"source\":\"owner/repo\",\"sourceType\":\"github\"}}}");
        var alias = Path.Combine(_root.FullName, "alias");
        Directory.CreateSymbolicLink(alias, destination);
        var logger = new Logger();
        var context = GhInstallOwnershipGuard.Capture([alias], new(logger, environment: new Dictionary<string, string> { ["HOME"] = home }),
            new(Path: alias), logger, TestContext.Current.CancellationToken);
        Assert.Single(context.Claims);
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.Validate(context, [alias], ["demo"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProjectOwnerAndNonPrimaryRevisionFieldsRemainObservableAndProtected()
    {
        var destination = Directory.CreateDirectory(Path.Combine(_root.FullName, ".agents", "skills")).FullName;
        var path = Path.Combine(_root.FullName, "skills-lock.json");
        File.WriteAllText(path, "{\"version\":1,\"skills\":{\"demo\":{\"source\":\"owner/repo\",\"sourceType\":\"github\",\"pinnedRef\":\"v1\",\"version\":\"3.0\"}}}");
        var logger = new Logger();
        var environment = new Dictionary<string, string> { [OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME"] = _root.FullName };
        var context = GhInstallOwnershipGuard.Capture([destination], new(logger, environment: environment), new(Path: destination), logger,
            TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.Validate(context, [destination], ["demo"], TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(CliDispatcher.RenderListJson(InventorySnapshot.Empty with { OwnershipRecords = context.Claims }));
        var owner = Assert.Single(json.RootElement.GetProperty("ownerRecords").EnumerateArray());
        Assert.Equal("v1", owner.GetProperty("pinnedRef").GetString());
        Assert.Equal("3.0", owner.GetProperty("distributorVersion").GetString());
        Assert.Contains(destination, owner.GetProperty("coveredRoots").EnumerateArray().Select(root => root.GetString()));
        File.Delete(path);
        Assert.ThrowsAny<IOException>(() => GhInstallOwnershipGuard.ValidateUnchanged(context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProjectPlacementUsesRedirectedGitRootAndCwdFallback()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root.FullName, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_root.FullName, "b")).FullName;
        var logger = new Logger();
        var runner = new ProcessRunner(logger);
        var initialized = await runner.RunAsync("git", ["init", "--quiet", b], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(initialized.Succeeded, initialized.StdErr);
        var redirected = new GhSkillInstallService.Options(Agents: ["universal"], Scope: "project", WorkingDirectory: a,
            ProcessEnvironment: new Dictionary<string, string> { ["GIT_DIR"] = Path.Combine(b, ".git"), ["GIT_WORK_TREE"] = b });
        var destinations = await GhPlacementCatalog.DestinationsAsync(redirected, runner, TestContext.Current.CancellationToken);
        var observed = await runner.RunAsync("git", ["rev-parse", "--show-toplevel"], workingDirectory: a,
            environment: redirected.ProcessEnvironment, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(observed.Succeeded, observed.StdErr);
        Assert.True(PathIdentity.Equals(Path.Combine(observed.StdOut.TrimEnd('\r', '\n'), ".agents", "skills"), Assert.Single(destinations)));
        destinations = await GhPlacementCatalog.DestinationsAsync(redirected with { ProcessEnvironment = null }, runner, TestContext.Current.CancellationToken);
        Assert.True(PathIdentity.Equals(Path.Combine(a, ".agents", "skills"), Assert.Single(destinations)));
    }

    [Fact]
    public async Task OwnerDisappearingDuringRepositoryDiscoveryCannotAdmitAWriter()
    {
        if (OperatingSystem.IsWindows()) return; // Portable retained-context refusal above.
        var home = Directory.CreateDirectory(Path.Combine(_root.FullName, "home")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root.FullName, "project", ".agents", "skills")).FullName;
        var owner = Path.Combine(_root.FullName, "project", "skills-lock.json");
        File.WriteAllText(owner, "{\"version\":1,\"skills\":{\"demo\":{\"source\":\"owner/repo\",\"sourceType\":\"github\"}}}");
        var marker = Path.Combine(_root.FullName, "mutated");
        var gh = Path.Combine(_root.FullName, "gh");
        File.WriteAllText(gh, "#!/bin/sh\nfor arg do\n case \"$arg\" in demo) touch \"$SKILLVIEW_TEST_MUTATION\"; exit 0;; esac\ndone\nrm \"$SKILLVIEW_TEST_OWNER\"\nprintf '[root] demo\\tdescription\\n'\n");
        File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var logger = new Logger();
        var result = await new GhSkillInstallService(new(logger, environment: new Dictionary<string, string>
        { ["HOME"] = home, ["SKILLVIEW_TEST_OWNER"] = owner, ["SKILLVIEW_TEST_MUTATION"] = marker }), logger)
            .InstallAsync(gh, "owner/repo", "demo", new(Path: destination, Overwrite: true), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(owner)); // Discovery really ran and changed the captured context.
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task RelativeChildHomesAndProfilesUseCapturedWorkingDirectory()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root.FullName, "child")).FullName;
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = "home",
            ["USERPROFILE"] = "home",
            ["CLAUDE_CONFIG_DIR"] = ".claude",
            ["PI_CODING_AGENT_DIR"] = ".pi-profile",
            ["XDG_STATE_HOME"] = "state"
        };
        var logger = new Logger();
        var runner = new ProcessRunner(logger, environment: environment);
        var options = new GhSkillInstallService.Options(Agents: ["claude", "pi"], Scope: "user", WorkingDirectory: child);
        var destinations = await GhPlacementCatalog.DestinationsAsync(options, runner, TestContext.Current.CancellationToken);
        Assert.Contains(Path.Combine(child, ".claude", "skills"), destinations);
        Assert.Contains(Path.Combine(child, ".pi-profile", "skills"), destinations);
        var shared = Directory.CreateDirectory(Path.Combine(child, "home", ".agents", "skills")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(child, "state", "skills")).FullName;
        File.WriteAllText(Path.Combine(state, ".skill-lock.json"), "{\"version\":3,\"skills\":{\"demo\":{\"source\":\"owner/repo\",\"sourceType\":\"github\"}}}");
        var context = GhInstallOwnershipGuard.Capture([shared], runner, options, logger, TestContext.Current.CancellationToken);
        Assert.Single(context.Claims);
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.Validate(context, [shared], ["demo"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CodexAndPiProbeEverySupportedHostPlacementWithoutInventingAgentState()
    {
        var home = _root.FullName;
        var pi = Path.Combine(home, "pi-profile");
        var environment = new Dictionary<string, string>
        { ["HOME"] = home, ["USERPROFILE"] = home, ["PI_CODING_AGENT_DIR"] = pi };
        var paths = await GhPlacementCatalog.DestinationsAsync(new(Agents: ["codex", "pi"], Scope: "user"),
            new(new Logger(), environment: environment), TestContext.Current.CancellationToken);
        Assert.Contains(Path.Combine(home, ".agents", "skills"), paths);
        Assert.Contains(Path.Combine(home, ".codex", "skills"), paths);
        Assert.Contains(Path.Combine(home, ".pi", "agent", "skills"), paths);
        Assert.Contains(Path.Combine(pi, "skills"), paths);
    }
}
