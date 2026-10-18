using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Tests.Cli;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Inventory;

/// Filesystem -> reconciled inventory -> CLI contracts, without a native manager.
public sealed class ContextualOwnershipCliTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-contextual-owners-");
    private readonly Logger logger = new(LogLevel.Debug);
    private readonly List<string> customRoots = [];
    private readonly TuiServices services;
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Home => Path.Combine(temporary.FullName, "home");
    private string State => Path.Combine(temporary.FullName, "xdg-state");
    private string UserRoot => Path.Combine(Home, ".agents", "skills");
    private string One => Path.Combine(temporary.FullName, "one");
    private string Two => Path.Combine(temporary.FullName, "two");
    private string OneRoot => Path.Combine(One, ".agents", "skills");
    private string TwoRoot => Path.Combine(Two, ".claude", "skills");
    private string GlobalLock => Path.Combine(Home, ".agents", SkillLockFileReader.FileName);
    private string XdgLock => Path.Combine(State, "skills", SkillLockFileReader.FileName);
    private const string Tree = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public ContextualOwnershipCliTests()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(temporary.FullName, "cwd")).FullName;
        foreach (var directory in new[] { cwd, One, Two }) Directory.CreateDirectory(Path.Combine(directory, ".git"));
        Directory.CreateDirectory(Home);
        // Explicit child values neutralize inherited Git redirects/configuration.
        // Empty PATH also prevents Git discovery. Never mutate the parent process.
        var environment = System.Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(key => key, _ => "", StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["HOME"] = Home,
            ["USERPROFILE"] = Home,
            ["PATH"] = "",
            ["XDG_STATE_HOME"] = State,
            ["XDG_CONFIG_HOME"] = Path.Combine(Home, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(Home, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(Home, "cache"),
            ["APPDATA"] = Path.Combine(Home, "appdata"),
            ["LOCALAPPDATA"] = Path.Combine(Home, "localappdata"),
            ["SKILLVIEW_STATE_HOME"] = Path.Combine(Home, "maintenance-state"),
            ["CODEX_HOME"] = Path.Combine(Home, ".codex"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(Home, ".claude"),
            ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = Path.Combine(Home, ".claude", "plugins"),
            ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
            ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
            ["PI_CODING_AGENT_DIR"] = Path.Combine(Home, ".pi", "agent"),
            ["GIT_DIR"] = Path.Combine(cwd, ".git"),
            ["GIT_WORK_TREE"] = cwd,
            ["GIT_COMMON_DIR"] = Path.Combine(cwd, ".git"),
            ["GIT_CONFIG_COUNT"] = "0"
        }) environment[key] = value;
        var runner = new ProcessRunner(logger, environment: environment, defaultWorkingDirectory: cwd);
        // GhBinaryLocator normally observes parent PATH/GH_PATH. Override its
        // discovery delegates too, so even a configured invoking gh cannot run.
        var locator = new GhBinaryLocator(runner, logger, () => "", _ => false);
        var auth = new GhAuthService(runner, logger);
        var list = new GhSkillListAdapter((_, _, _) => throw new InvalidOperationException("No catalog child is permitted."), logger);
        var scanner = new LocalSkillScanner(logger);
        var resolver = new ScanRootResolver();
        services = new()
        {
            Logger = logger,
            Runner = runner,
            GhLocator = locator,
            AuthService = auth,
            EnvironmentProbe = new EnvironmentProbe(locator, auth, runner, logger, temporary.FullName),
            SearchService = new(runner, logger),
            PreviewService = new(runner, logger),
            InstallService = new(runner, logger),
            UpdateService = new(runner, logger),
            ListAdapter = list,
            ScanRootResolver = resolver,
            Scanner = scanner,
            InventoryService = new(resolver, scanner, list, logger, runner),
            RemoveService = new(logger),
            FileLogSink = null,
            LogDirectory = temporary.FullName
        };
    }

    public void Dispose() => temporary.Delete(true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThreeSameNameSourcesKeepExactUserAndProjectOwnersInActualCli(bool xdg)
    {
        var paths = ThreeSources(xdg);
        var before = Files();
        var snapshot = await Capture();
        Assert.Equal(3, snapshot.Skills.Length);
        Assert.Equal(3, snapshot.Skills.Select(skill => skill.InstallationId).Distinct().Count());
        var result = await RunCli("list", "--allow-hidden-dirs", "--json");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var json = JsonDocument.Parse(result.Stdout);
        var rows = json.RootElement.GetProperty("skills").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.False(json.RootElement.GetProperty("usedGhSkillList").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("providers").EnumerateArray(), provider =>
            provider.GetProperty("provider").GetString() == "gh-default" && !provider.GetProperty("succeeded").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("providers").EnumerateArray(), provider =>
            provider.GetProperty("provider").GetString() == "lockfiles" && provider.GetProperty("succeeded").GetBoolean());
        foreach (var (path, source, scope, record, provider, context) in new[]
        {
            (paths[0], "team/user", Scope.User, xdg ? XdgLock : GlobalLock, xdg ? "vercel-global-xdg" : "gh-vercel-global", Home),
            (paths[1], "team/one", Scope.Project, Path.Combine(One, SkillLockFileReader.ProjectFileName), "vercel-project", One),
            (paths[2], "team/two", Scope.Project, Path.Combine(Two, SkillLockFileReader.ProjectFileName), "vercel-project", Two)
        })
        {
            var skill = Assert.Single(snapshot.Skills, skill => OwnershipPath.Same(skill.ResolvedPath, path));
            var row = Assert.Single(rows, row => OwnershipPath.Same(row.GetProperty("resolvedPath").GetString()!, path));
            Assert.Equal("demo", row.GetProperty("name").GetString());
            Assert.Equal(skill.InstallationId, row.GetProperty("installationId").GetString());
            Assert.Equal(scope.ToString(), row.GetProperty("scope").GetString());
            Assert.Equal("https://github.com/" + source, row.GetProperty("sourceIdentity").GetProperty("origin").GetString());
            Assert.False(row.GetProperty("sourceIdentity").GetProperty("conflicted").GetBoolean());
            Assert.Empty(row.GetProperty("diagnostics").EnumerateArray());
            var owner = Assert.Single(row.GetProperty("ownerRecords").EnumerateArray());
            Assert.Equal(source, owner.GetProperty("source").GetString());
            Assert.Equal(record, owner.GetProperty("recordPath").GetString());
            Assert.Equal(context, owner.GetProperty("contextRoot").GetString());
            Assert.Equal(provider, owner.GetProperty("provider").GetString());
            Assert.Equal("copy", owner.GetProperty("installationMode").GetString());
            Assert.False(owner.GetProperty("readOnly").GetBoolean());
            Assert.Contains(row.GetProperty("agents").EnumerateArray(), exposure =>
                OwnershipPath.Same(exposure.GetProperty("path").GetString()!, path)
                && exposure.GetProperty("scope").GetString() == scope.ToString());
        }
        Assert.Equal(3, rows.Select(row => row.GetProperty("sourceIdentity").GetProperty("id").GetString()).Distinct().Count());
        var ambiguous = await RunCli("remove", "demo", "--yes", "--json");
        Assert.Equal(ExitCodes.UserError, ambiguous.ExitCode);
        Assert.Contains("3 skills match", ambiguous.Stderr, StringComparison.Ordinal);
        Assert.Empty(ambiguous.Stdout);
        AssertUnchanged(before);
    }

    [Fact]
    public async Task PackageAndRepositoryGroupsFromInventoryExcludeSameNameOtherSourcesAndRefuseDeletion()
    {
        var paths = ThreeSources(false);
        var sibling = Install(UserRoot, "companion", "team/user");
        var repositoryPeer = Install(OneRoot, "repository-peer", "team/user");
        WriteCurrent(GlobalLock, false, ("demo", "team/user"), ("companion", "team/user"));
        WriteCurrent(Path.Combine(One, SkillLockFileReader.ProjectFileName), true,
            ("demo", "team/one"), ("repository-peer", "team/user"));
        var before = Files();
        var snapshot = await Capture();
        var selected = Assert.Single(snapshot.Skills, skill => OwnershipPath.Same(skill.ResolvedPath, paths[0]));
        var targets = RemoveTargetResolver.BuildTargets(selected, snapshot);
        var package = Assert.Single(targets, target => target.Kind == RemoveTargetKind.PackageGroup);
        var repository = Assert.Single(targets, target => target.Kind == RemoveTargetKind.RepoGroup);
        AssertPaths([paths[0], sibling], package.Skills.Select(skill => skill.ResolvedPath));
        AssertPaths([paths[0], sibling, repositoryPeer], repository.Skills.Select(skill => skill.ResolvedPath));
        foreach (var group in new[] { package, repository })
        {
            Assert.DoesNotContain(group.Skills, skill => OwnershipPath.Same(skill.ResolvedPath, paths[1]) || OwnershipPath.Same(skill.ResolvedPath, paths[2]));
            var evaluation = RemoveTargetResolver.Evaluate(group, snapshot);
            Assert.False(evaluation.CanExecute);
            Assert.All(evaluation.Items, item => Assert.Contains(item.Validation.Errors,
                error => error.Kind == RemoveValidator.ErrorKind.OwnerRecordNeedsPlan));
        }
        foreach (var skill in repository.Skills)
            await AssertCliRefusal(skill, execute: true);
        AssertUnchanged(before);
    }

    [Fact]
    public async Task ConflictingGlobalAndXdgClaimsSurviveCliAndBothRemovalModes()
    {
        var path = Install(UserRoot, "demo", "team/user");
        WriteCurrent(GlobalLock, false, ("demo", "team/user"));
        WriteCurrent(XdgLock, false, ("demo", "team/other"));
        var before = Files();
        var skill = Assert.Single((await Capture()).Skills);
        Assert.Equal(2, skill.AllOwnershipClaims.Count());
        using var json = JsonDocument.Parse((await RunCli("list", "--allow-hidden-dirs", "--json")).Stdout);
        var row = Assert.Single(json.RootElement.GetProperty("skills").EnumerateArray());
        Assert.True(OwnershipPath.Same(row.GetProperty("resolvedPath").GetString()!, path));
        Assert.Equal(["team/other", "team/user"], row.GetProperty("ownerRecords").EnumerateArray()
            .Select(owner => owner.GetProperty("source").GetString()).Order());
        Assert.True(row.GetProperty("sourceIdentity").GetProperty("conflicted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("sourceIdentity").GetProperty("id").ValueKind);
        Assert.Contains(row.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetString()!.Contains("contradicts tracking", StringComparison.Ordinal));
        await AssertCliRefusal(skill, execute: false);
        await AssertCliRefusal(skill, execute: true);
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData("global", 1)]
    [InlineData("global", 2)]
    [InlineData("global", 99)]
    [InlineData("xdg", 1)]
    [InlineData("xdg", 2)]
    [InlineData("xdg", 99)]
    [InlineData("project", 2)]
    [InlineData("project", 99)]
    public async Task UnsupportedDeclaredVersionsRemainReadOnlyDiagnosticsInCli(string location, int version)
    {
        var root = location == "project" ? OneRoot : UserRoot;
        var record = location switch { "global" => GlobalLock, "xdg" => XdgLock, _ => Path.Combine(One, SkillLockFileReader.ProjectFileName) };
        Install(root, "demo");
        WriteDeclaredVersion(record, version);
        var before = Files();
        var snapshot = await Capture();
        var skill = Assert.Single(snapshot.Skills);
        var result = await RunCli("list", "--allow-hidden-dirs", "--json");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var json = JsonDocument.Parse(result.Stdout);
        var row = Assert.Single(json.RootElement.GetProperty("skills").EnumerateArray());
        var owner = Assert.Single(row.GetProperty("ownerRecords").EnumerateArray());
        Assert.Equal(version, owner.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(record, owner.GetProperty("recordPath").GetString());
        Assert.True(owner.GetProperty("readOnly").GetBoolean());
        Assert.Contains("Unsupported", owner.GetProperty("diagnostic").GetString(), StringComparison.Ordinal);
        Assert.Contains(row.GetProperty("diagnostics").EnumerateArray(), value => value.GetString()!.Contains("Unsupported", StringComparison.Ordinal));
        Assert.Contains(json.RootElement.GetProperty("providers").EnumerateArray(), provider =>
            provider.GetProperty("provider").GetString() == "lockfiles" && !provider.GetProperty("succeeded").GetBoolean());
        Assert.True(json.RootElement.GetProperty("partial").GetBoolean());
        AssertAbsentRevisionEvidence(row, owner);
        await AssertCliRefusal(skill, execute: false);
        await AssertCliRefusal(skill, execute: true);
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyRootParentDeclaredVersionsRetainOnlyPresentFieldsAndCannotAuthorizeRemoval(int version)
    {
        var root = Path.Combine(temporary.FullName, "legacy", "skills");
        customRoots.Add(root);
        Install(root, "demo");
        var record = Path.Combine(Path.GetDirectoryName(root)!, SkillLockFileReader.FileName);
        WriteDeclaredVersion(record, version);
        var before = Files();
        var skill = Assert.Single((await Capture()).Skills);
        using var json = JsonDocument.Parse((await RunCli("list", "--allow-hidden-dirs", "--json")).Stdout);
        var row = Assert.Single(json.RootElement.GetProperty("skills").EnumerateArray());
        var owner = Assert.Single(row.GetProperty("ownerRecords").EnumerateArray());
        Assert.Equal("legacy-lock", owner.GetProperty("provider").GetString());
        Assert.Equal(record, owner.GetProperty("recordPath").GetString());
        Assert.Equal(version, owner.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("team/legacy", owner.GetProperty("source").GetString());
        Assert.False(owner.GetProperty("readOnly").GetBoolean());
        Assert.Equal("https://github.com/team/legacy", row.GetProperty("sourceIdentity").GetProperty("origin").GetString());
        AssertAbsentRevisionEvidence(row, owner);
        await AssertCliRefusal(skill, execute: true);
        AssertUnchanged(before);
    }

    private string[] ThreeSources(bool xdg)
    {
        var paths = new[] { Install(UserRoot, "demo", "team/user"), Install(OneRoot, "demo", "team/one"), Install(TwoRoot, "demo", "team/two") };
        WriteCurrent(xdg ? XdgLock : GlobalLock, false, ("demo", "team/user"));
        WriteCurrent(Path.Combine(One, SkillLockFileReader.ProjectFileName), true, ("demo", "team/one"));
        WriteCurrent(Path.Combine(Two, SkillLockFileReader.ProjectFileName), true, ("demo", "team/two"));
        return paths;
    }

    private static string Install(string root, string name, string? source = null)
    {
        var path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var tracking = source is null ? "" : $"metadata:\n  github-repo: https://github.com/{source}\n  github-ref: refs/heads/main\n  github-path: skills/{name}\n  github-tree-sha: {Tree}\n";
        File.WriteAllText(Path.Combine(path, "SKILL.md"), $"---\nname: {name}\ndescription: Contextual owner fixture\n{tracking}---\nInstructions\n");
        File.WriteAllText(Path.Combine(path, "resource.txt"), "Keep this complete payload: " + source);
        return path;
    }

    private static void WriteCurrent(string path, bool project, params (string Name, string Source)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var skills = entries.ToDictionary(entry => entry.Name, entry =>
        {
            var record = new Dictionary<string, object>
            {
                ["source"] = entry.Source,
                ["sourceType"] = "github",
                ["sourceUrl"] = "https://github.com/" + entry.Source,
                ["skillPath"] = "skills/" + entry.Name,
                ["ref"] = "refs/heads/main",
                ["installationMode"] = "copy"
            };
            record[project ? "computedHash" : "skillFolderHash"] = project ? new string('c', 64) : Tree;
            return record;
        });
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = project ? 1 : 3, skills }));
    }

    private static void WriteDeclaredVersion(string path, int version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Deliberately only common, present fields. These are declared-version
        // compatibility fixtures, not claims that an old manager ran or that
        // its absent revision/hash fields have modern semantics.
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version,
            skills = new Dictionary<string, object>
            { ["demo"] = new { source = "team/legacy", sourceType = "github", sourceUrl = "https://github.com/team/legacy" } }
        }));
    }

    private Task<InventorySnapshot> Capture() => services.InventoryService.CaptureAsync(null,
        new(customRoots, true, WorkspaceDirectories: [One, Two]), Token);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCli(params string[] command)
    {
        await CliConsoleCapture.Gate.WaitAsync(Token);
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var args = new List<string> { "--workspace", One, "--workspace", Two };
            foreach (var root in customRoots) args.AddRange(["--scan-root", root]);
            args.AddRange(command);
            var exit = await CliDispatcher.RunAsync(ArgParser.Parse("skillview", [.. args]), services, Token);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            CliConsoleCapture.Gate.Release();
        }
    }

    private async Task AssertCliRefusal(InstalledSkill skill, bool execute)
    {
        var args = new List<string> { "remove", "--install-id", skill.InstallationId, "--json" };
        if (execute) args.Add("--yes");
        var result = await RunCli([.. args]);
        using var json = JsonDocument.Parse(result.Stdout);
        var row = json.RootElement;
        Assert.False(row.GetProperty("allowed").GetBoolean());
        Assert.False(row.GetProperty("succeeded").GetBoolean());
        var errors = row.GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("kind").GetString()).ToArray();
        Assert.Contains(nameof(RemoveValidator.ErrorKind.OwnerRecordNeedsPlan), errors);
        Assert.Equal(errors.Contains(nameof(RemoveValidator.ErrorKind.FilesystemIdentityUnavailable)) ? ExitCodes.EnvironmentError : ExitCodes.UserError, result.ExitCode);
        Assert.Equal(0, row.GetProperty("filesDeleted").GetInt32());
        Assert.Equal(0, row.GetProperty("directoriesDeleted").GetInt32());
        Assert.Equal(0, row.GetProperty("runtimeErrorCount").GetInt32());
        Assert.Empty(row.GetProperty("runtimeErrors").EnumerateArray());
        Assert.Equal(skill.InstallationId, row.GetProperty("impact").GetProperty("subject").GetProperty("installationId").GetString());
        Assert.True(OwnershipPath.Same(row.GetProperty("resolvedPath").GetString()!, skill.ResolvedPath));
    }

    private static void AssertAbsentRevisionEvidence(JsonElement row, JsonElement owner)
    {
        foreach (var field in new[] { "repositoryRef", "sourcePath", "contentHash", "hashKind", "installationMode" })
            Assert.Equal(JsonValueKind.Null, owner.GetProperty(field).ValueKind);
        foreach (var field in new[] { "authoredVersion", "repositoryRef", "recordedTree", "ownerRecordedHash", "resolvedCommit" })
            Assert.Equal(JsonValueKind.Null, row.GetProperty("revision").GetProperty(field).ValueKind);
    }

    private static void AssertPaths(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.Select(PathIdentity.NormalizeKey).Order(), actual.Select(PathIdentity.NormalizeKey).Order());

    private ImmutableDictionary<string, string> Files() => Directory.GetFiles(temporary.FullName, "*", SearchOption.AllDirectories)
        .ToImmutableDictionary(path => path, path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));

    private void AssertUnchanged(ImmutableDictionary<string, string> before)
    {
        Assert.Equal(before.OrderBy(pair => pair.Key), Files().OrderBy(pair => pair.Key));
        Assert.False(Directory.Exists(Path.Combine(Home, "maintenance-state")));
        Assert.DoesNotContain(logger.Snapshot(), entry => entry.Category == "subprocess");
    }
}
