using SkillView.Bootstrapping;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class DiscoveryTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("skillview-discovery-");
    public void Dispose() => _temporary.Delete(true);
    private string DirectoryAt(string path) => Directory.CreateDirectory(Path.Combine(_temporary.FullName, path)).FullName;
    private string SkillAt(string path, string name)
    {
        var directory = DirectoryAt(path);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: Test\n---\nInstructions\n");
        return directory;
    }

    [Fact]
    public void NamespacesAreContainersAndPayloadChildrenRemainSeparate()
    {
        var root = DirectoryAt("skills");
        SkillAt("skills/author/demo", "demo");
        SkillAt("skills/author/other", "other");
        var locations = new List<DiscoveryLocation>();
        var result = new LocalSkillScanner(new Logger()).ScanWithCancellation([new(root, Scope.Custom, null)], null,
            TestContext.Current.CancellationToken, locations: locations);
        Assert.Equal(["demo", "other"], result.Select(skill => skill.Name));
        Assert.All(result, skill => Assert.Equal(ValidityState.Valid, skill.Validity));
        Assert.Contains(locations, location => location.State == "container" && Path.GetFileName(location.Path) == "author");
    }

    [Fact]
    public void RootCatalogCoversEveryGhPlacementWithoutInventingSharedAgentConsumers()
    {
        var home = DirectoryAt("home");
        var project = DirectoryAt("project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        foreach (var entry in GhPlacementCatalog.Entries)
        {
            Directory.CreateDirectory(Path.Combine(home, entry.UserDirectory));
            Directory.CreateDirectory(Path.Combine(project, entry.ProjectDirectory));
        }
        var roots = new ScanRootResolver().Resolve(new(project, home, []));
        foreach (var entry in GhPlacementCatalog.Entries)
        {
            Assert.Contains(roots, root => PathIdentity.Equals(root.Path, Path.Combine(home, entry.UserDirectory)));
            Assert.Contains(roots, root => PathIdentity.Equals(root.Path, Path.Combine(project, entry.ProjectDirectory)));
        }
        Assert.Equal(GhPlacementCatalog.Entries.Select(entry => entry.Id).Order(), InstallAgentCatalog.GhIds.Order());
        Assert.All(roots.Where(root => PathIdentity.Equals(root.Path, Path.Combine(home, ".agents", "skills"))),
            root => Assert.Equal("agents", root.AgentHint));
    }

    [Fact]
    public void MonorepoAncestorsAndWorkspaceCollectionsRetainTheirContext()
    {
        var home = DirectoryAt("home");
        var project = DirectoryAt("project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var current = DirectoryAt("project/packages/application");
        var intermediate = DirectoryAt("project/packages/.claude/skills");
        var top = DirectoryAt("project/.gemini/skills");
        var other = DirectoryAt("other");
        var otherSkills = DirectoryAt("other/.factory/skills");
        var roots = new ScanRootResolver().Resolve(new(current, home, [], WorkspaceDirectories: [other]));
        Assert.Contains(roots, root => OwnershipPath.Same(root.Path, intermediate) && root.Context is { } context && OwnershipPath.Same(context, Path.Combine(project, "packages")));
        Assert.Contains(roots, root => OwnershipPath.Same(root.Path, top) && root.Context is { } context && OwnershipPath.Same(context, project));
        Assert.Contains(roots, root => OwnershipPath.Same(root.Path, otherSkills) && root.Context is { } context && OwnershipPath.Same(context, other));
    }

    [Fact]
    public void ExplicitProfilesAndRootAliasesPreserveExposuresOfOnePhysicalPayload()
    {
        var root = DirectoryAt("profile");
        SkillAt("profile/demo", "demo");
        var other = root;
        if (!OperatingSystem.IsWindows())
        {
            other = Path.Combine(_temporary.FullName, "alias");
            Directory.CreateSymbolicLink(other, root);
        }
        var roots = new ScanRootResolver().Resolve(new(_temporary.FullName, DirectoryAt("home"), [],
            Profiles: [new("claude", root, "one"), new("claude", other, "two")]));
        var skill = Assert.Single(new LocalSkillScanner(new Logger()).Scan(roots));
        Assert.Equal(["one", "two"], skill.Agents.Select(agent => agent.Profile).Order());
        Assert.All(skill.Agents, agent => { Assert.Null(agent.Enabled); Assert.Contains("unknown", agent.LoadingRule); });
        var detail = InstalledScreen.RenderDetail(skill);
        Assert.Contains("Profile: one", detail, StringComparison.Ordinal);
        Assert.Contains("Profile: two", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesystemAndGhAliasesMergeIntoOnePhysicalInstallation()
    {
        var root = DirectoryAt("skills");
        var physical = SkillAt("skills/demo", "demo");
        var alias = Path.Combine(_temporary.FullName, "alias");
        Directory.CreateSymbolicLink(alias, root);
        var scanned = new LocalSkillScanner(new Logger()).Scan([new(alias, Scope.User, "claude")]);
        var merged = LocalInventoryService.Merge(scanned, [new() { Name = "demo", Path = physical, Agent = "claude" }]);
        Assert.Equal(Provenance.Both, Assert.Single(merged).Provenance);
        Assert.Contains(merged[0].Agents, agent => agent.Path == physical);
        Assert.Contains(merged[0].Agents, agent => agent.Path == Path.Combine(alias, "demo"));
    }

    [Fact]
    public async Task RedirectedGitProjectContributesFilesystemRootsWithoutAnExplicitWorkspace()
    {
        var project = DirectoryAt("redirected");
        var logger = new Logger();
        var runner = new ProcessRunner(logger, environment: new Dictionary<string, string>
        { ["GIT_DIR"] = Path.Combine(project, ".git"), ["GIT_WORK_TREE"] = project });
        var initialized = await new ProcessRunner(logger).RunAsync("git", ["init", "--quiet", project],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(initialized.Succeeded, initialized.StdErr);
        var root = DirectoryAt("redirected/.claude/skills");
        var active = await GhPlacementCatalog.ProjectRootAsync(runner, new(), _temporary.FullName, TestContext.Current.CancellationToken);
        var roots = new ScanRootResolver().Resolve(new(_temporary.FullName, DirectoryAt("home"), [], GhProjectDirectory: active));
        Assert.Contains(roots, candidate => OwnershipPath.Same(candidate.Path, root));
    }

    [Fact]
    public void NestedWorkspaceAliasStillObservesItsPhysicalWorktreeAncestors()
    {
        if (OperatingSystem.IsWindows()) return; // Cross-platform alias merge above exercises Windows.
        var project = DirectoryAt("project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var target = DirectoryAt("project/packages/app");
        var skills = DirectoryAt("project/.claude/skills");
        var alias = Path.Combine(_temporary.FullName, "alias");
        Directory.CreateSymbolicLink(alias, target);
        var roots = new ScanRootResolver().Resolve(new(DirectoryAt("home"), DirectoryAt("home"), [], WorkspaceDirectories: [alias]));
        Assert.Contains(roots, root => OwnershipPath.Same(root.Path, skills));
    }

    [Fact]
    public void MissingExplicitContextsAreIncompleteAndCannotFallBackToTheirParents()
    {
        var parent = DirectoryAt("project");
        Directory.CreateDirectory(Path.Combine(parent, ".git"));
        DirectoryAt("project/.claude/skills");
        var missing = Path.Combine(parent, "missing");
        var diagnostics = new List<string>();
        var roots = new ScanRootResolver().ResolveWithCancellation(new(DirectoryAt("home"), DirectoryAt("home"),
            [missing], WorkspaceDirectories: [missing], Profiles: [new("claude", missing, "missing")]),
            TestContext.Current.CancellationToken, diagnostics);
        Assert.Empty(roots);
        Assert.Contains(missing, diagnostics);
    }

    [Fact]
    public void LastAcceptedWorkspaceAndLegacyProjectRootsAreObserved()
    {
        var home = DirectoryAt("home");
        var workspaces = Enumerable.Range(0, 32).Select(i => DirectoryAt("workspaces/" + i)).ToArray();
        var last = workspaces[^1];
        Directory.CreateDirectory(Path.Combine(last, ".github", "skills"));
        var incomplete = new List<string>();
        var roots = new ScanRootResolver().ResolveWithCancellation(new(home, home, [], WorkspaceDirectories: workspaces),
            TestContext.Current.CancellationToken, incomplete);
        Assert.Contains(roots, root => root.Context == last && root.AgentHint == "github");
        Assert.Empty(incomplete);
    }

    [Fact]
    public void BrokenExposureKeepsEveryProfileAndAliasContext()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = DirectoryAt("profile");
        Directory.CreateSymbolicLink(Path.Combine(root, "broken"), Path.Combine(_temporary.FullName, "missing"));
        var alias = Path.Combine(_temporary.FullName, "alias");
        Directory.CreateSymbolicLink(alias, root);
        var roots = new[] { new ScanRoot(root, Scope.User, "claude") { Profile = "one", Context = "first" },
            new ScanRoot(alias, Scope.User, "claude") { Profile = "two", Context = "second" } };
        var skill = Assert.Single(new LocalSkillScanner(new Logger()).Scan(roots));
        Assert.Equal(ValidityState.BrokenSymlink, skill.Validity);
        Assert.Equal(["one", "two"], skill.Agents.Select(agent => agent.Profile));
        Assert.Equal(["first", "second"], skill.Agents.Select(agent => agent.Context));
    }

    [Fact]
    public void GlobalEntryAndNamespaceDepthBoundsRetainIncompleteCoverage()
    {
        var root = DirectoryAt("skills");
        var incomplete = new List<string>();
        var locations = new List<DiscoveryLocation>();
        var scanner = new LocalSkillScanner(new Logger(), null,
            path => Enumerable.Range(0, LocalSkillScanner.MaxScanEntries + 5).Select(i => Path.Combine(path, "file" + i)).GetEnumerator());
        Assert.Empty(scanner.ScanWithCancellation([new(root, Scope.Custom, null)], null,
            TestContext.Current.CancellationToken, incomplete, locations));
        Assert.NotEmpty(incomplete);
        Assert.Contains(locations, location => location.State == "budget-exceeded");
        incomplete.Clear(); locations.Clear();
        var deep = string.Join('/', Enumerable.Repeat("namespace", LocalSkillScanner.MaxScanDepth + 2));
        SkillAt("skills/" + deep + "/demo", "demo");
        Assert.Empty(new LocalSkillScanner(new Logger()).ScanWithCancellation([new(root, Scope.Custom, null)], null,
            TestContext.Current.CancellationToken, incomplete, locations));
        Assert.NotEmpty(incomplete);
    }

    [Fact]
    public void SuccessfulReadsBeforeIoFailureStillConsumeTheAggregateBudget()
    {
        var root = DirectoryAt("skills");
        const int shortRead = 32 * 1024;
        var consumed = 0;
        var count = LocalSkillScanner.MaxMetadataBytes / shortRead + 3;
        for (var i = 0; i < count; i++) File.WriteAllText(Path.Combine(DirectoryAt("skills/demo" + i), "SKILL.md"), "");
        var incomplete = new List<string>();
        var scanner = new LocalSkillScanner(new Logger(), null, openMetadataForTests: _ =>
            new FailingReadStream(bytes => consumed += bytes, shortRead));
        scanner.ScanWithCancellation([new(root, Scope.Custom, null)], null, TestContext.Current.CancellationToken, incomplete);
        Assert.Equal(LocalSkillScanner.MaxMetadataBytes + 1, consumed);
        Assert.NotEmpty(incomplete);
    }

    private sealed class FailingReadStream(Action<int> observed, int firstRead) : MemoryStream
    {
        private bool _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read) throw new IOException("Failure after a partial read");
            _read = true;
            var bytes = Math.Min(firstRead, count);
            Array.Fill(buffer, (byte)'x', offset, bytes);
            observed(bytes);
            return bytes;
        }
    }

    [Fact]
    public void AggregateMetadataReadBudgetStopsBeforeUnboundedPrefixRetention()
    {
        var root = DirectoryAt("skills");
        var content = new string('x', LocalSkillScanner.MaxFrontMatterPrefixBytes);
        var count = LocalSkillScanner.MaxMetadataBytes / LocalSkillScanner.MaxFrontMatterPrefixBytes + 3;
        for (var i = 0; i < count; i++)
            File.WriteAllText(Path.Combine(DirectoryAt("skills/demo" + i), "SKILL.md"), content);
        var incomplete = new List<string>();
        var scanned = new LocalSkillScanner(new Logger()).ScanWithCancellation([new(root, Scope.Custom, null)], null,
            TestContext.Current.CancellationToken, incomplete);
        Assert.NotEmpty(incomplete);
        Assert.Equal(count - 2, scanned.Length);
    }

    [Fact]
    public void LinkedContainersAndHiddenLocationsReportUnobservedContent()
    {
        var root = DirectoryAt("skills");
        var outside = DirectoryAt("outside");
        SkillAt("outside/demo", "demo");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
        SkillAt("skills/.hidden/secret", "secret");
        var incomplete = new List<string>();
        var locations = new List<DiscoveryLocation>();
        Assert.Empty(new LocalSkillScanner(new Logger()).ScanWithCancellation([new(root, Scope.Custom, null)], null,
            TestContext.Current.CancellationToken, incomplete, locations));
        Assert.Contains(locations, location => location.State == "unsupported");
        Assert.Contains(locations, location => location.State == "excluded");
        Assert.NotEmpty(incomplete);
    }

    [Fact]
    public void DanglingDirectoryLinksRemainBrokenCandidatesBesideLiveLinkedContainers()
    {
        var root = DirectoryAt("skills");
        var outside = DirectoryAt("outside");
        SkillAt("outside/demo", "demo");
        var broken = Path.Combine(root, "broken");
        var container = Path.Combine(root, "container");
        Directory.CreateSymbolicLink(broken, Path.Combine(_temporary.FullName, "missing"));
        Directory.CreateSymbolicLink(container, outside);
        var incomplete = new List<string>();
        var locations = new List<DiscoveryLocation>();
        var skill = Assert.Single(new LocalSkillScanner(new Logger()).ScanWithCancellation(
            [new(root, Scope.Custom, null)], null, TestContext.Current.CancellationToken, incomplete, locations));
        Assert.Equal(ValidityState.BrokenSymlink, skill.Validity);
        Assert.Equal(broken, skill.ResolvedPath);
        Assert.DoesNotContain(incomplete, path => PathIdentity.Equals(path, broken));
        Assert.Contains(incomplete, path => PathIdentity.Equals(path, container));
        Assert.Contains(locations, location => location.Path == container && location.State == "unsupported");
    }

    [Fact]
    public async Task SpecialSkillMetadataCannotBlockInventory()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = DirectoryAt("skills");
        var candidate = DirectoryAt("skills/demo");
        var result = await new ProcessRunner(new Logger()).RunAsync("mkfifo", [Path.Combine(candidate, "SKILL.md")],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.StdErr);
        var scan = await Task.Run(() => new LocalSkillScanner(new Logger()).Scan([new(root, Scope.Custom, null)]))
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(ValidityState.UnparsableFrontMatter, Assert.Single(scan).Validity);
    }

    [Fact]
    public void WorkspaceAndProfileArgumentsPreserveWindowsPathsAndRejectIncompleteSpecifications()
    {
        var options = ArgParser.Parse("skillview", ["--workspace", "workspace", "--profile", @"claude:work=C:\profiles\skills", "list", "--json"]);
        Assert.Equal(["workspace"], options.WorkspaceDirectories);
        var profile = Assert.Single(options.Profiles);
        Assert.Equal("work", profile.Name);
        Assert.Equal(@"C:\profiles\skills", profile.SkillsDirectory);
        Assert.Throws<ArgumentException>(() => ArgParser.Parse("skillview", ["--profile", "claude"]));
        Assert.Throws<ArgumentException>(() => ArgParser.Parse("skillview", ["--profile", "unknown=directory"]));
        Assert.Throws<ArgumentException>(() => ArgParser.Parse("skillview", ["--profile", "claude:=directory"]));
    }
}
