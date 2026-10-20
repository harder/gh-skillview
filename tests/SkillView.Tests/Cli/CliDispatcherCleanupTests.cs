using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class CliDispatcherCleanupTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("skillview-cli-cleanup-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => _temporary.Delete(true);

    [Fact]
    public async Task PreviewReportsRealScannerEmptyDirectoryAndBothFilterFormsWithoutPayloadOrStateWrites()
    {
        var root = CreateDirectory("skills");
        var empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
        var nonempty = Directory.CreateDirectory(Path.Combine(root, "nonempty")).FullName;
        var keep = Path.Combine(nonempty, "keep.txt");
        File.WriteAllText(keep, "Keep these authored bytes.");
        var scanner = new LocalSkillScanner(new Logger());
        Assert.All(scanner.Scan([new(root, Scope.Custom, null)]), skill =>
            Assert.Equal(ValidityState.MissingSkillMd, skill.Validity));
        var observedBefore = SecureRemovalBackend.TryObserveIdentity(empty, out var before, out _);

        var (code, output, _) = await RunCliAsync(root, "cleanup", "--json");
        Assert.Equal(ExitCodes.Success, code);
        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.GetProperty("apply").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("applied", out _));
        var entry = Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray(),
            row => PathIdentity.Equals(empty, row.GetProperty("path").GetString()!));
        Assert.Equal("EmptyDirectory", entry.GetProperty("kind").GetString());
        Assert.False(entry.GetProperty("reviewOnly").GetBoolean());

        foreach (var filter in new[] { new[] { "--candidates=Malformed" }, new[] { "--candidates", "Malformed" } })
        {
            var (filteredCode, filteredOutput, _) = await RunCliAsync(root, ["cleanup", .. filter, "--json"]);
            Assert.Equal(ExitCodes.Success, filteredCode);
            using var filtered = JsonDocument.Parse(filteredOutput);
            var malformed = Assert.Single(filtered.RootElement.GetProperty("entries").EnumerateArray());
            Assert.True(PathIdentity.Equals(nonempty, malformed.GetProperty("path").GetString()!));
            Assert.Equal("Malformed", malformed.GetProperty("kind").GetString());
        }
        Assert.True(Directory.Exists(empty)); Assert.Empty(Directory.EnumerateFileSystemEntries(empty));
        Assert.Equal("Keep these authored bytes.", File.ReadAllText(keep));
        if (observedBefore)
        {
            Assert.True(SecureRemovalBackend.TryObserveIdentity(empty, out var after, out _));
            Assert.Equal(before, after);
        }
        Assert.False(Directory.Exists(Path.Combine(_temporary.FullName, "home", "maintenance-state")));
        Assert.False(Directory.Exists(Path.Combine(_temporary.FullName, "home", "preferences")));
    }

    [Fact]
    public async Task ApplyRequiresYesAndDeletesOnlyEligibleEmptyDirectoryWhileNonemptyMalformedStays()
    {
        if (!SecureRemovalBackend.IsSupported)
        { Assert.Skip("Real cleanup application requires the supported native removal backend."); return; }
        var root = CreateDirectory("skills");
        var empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
        var nonempty = Directory.CreateDirectory(Path.Combine(root, "nonempty")).FullName;
        var keep = Path.Combine(nonempty, "keep.txt"); File.WriteAllText(keep, "Do not remove.");

        var (unapprovedCode, _, error) = await RunCliAsync(root, "cleanup", "--apply", "--json");
        Assert.Equal(ExitCodes.UserError, unapprovedCode); Assert.Contains("requires --yes", error);
        Assert.True(Directory.Exists(empty)); Assert.Equal("Do not remove.", File.ReadAllText(keep));

        var (code, output, _) = await RunCliAsync(root, "cleanup", "--apply", "--yes", "--json");
        Assert.Equal(ExitCodes.UserError, code); // The protected malformed row is explicitly refused.
        using var document = JsonDocument.Parse(output);
        Assert.True(document.RootElement.GetProperty("apply").GetBoolean());
        var applied = document.RootElement.GetProperty("applied").EnumerateArray().ToArray();
        var removed = Assert.Single(applied, row => PathIdentity.Equals(empty, row.GetProperty("path").GetString()!));
        Assert.True(removed.GetProperty("succeeded").GetBoolean());
        Assert.Equal(0, removed.GetProperty("filesDeleted").GetInt32());
        Assert.Equal(1, removed.GetProperty("directoriesDeleted").GetInt32());
        var protectedRow = Assert.Single(applied, row => PathIdentity.Equals(nonempty, row.GetProperty("path").GetString()!));
        Assert.False(protectedRow.GetProperty("succeeded").GetBoolean());
        Assert.False(Directory.Exists(empty)); Assert.Equal("Do not remove.", File.ReadAllText(keep));
        Assert.Single(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task CliApplyRemovesEmptyNameCollisionAndRetainsTheRealSkill()
    {
        if (!SecureRemovalBackend.IsSupported) { Assert.Skip("Native removal backend required."); return; }
        var firstRoot = CreateDirectory("first"); var secondRoot = CreateDirectory("second");
        var empty = Directory.CreateDirectory(Path.Combine(firstRoot, "foo")).FullName;
        var real = Directory.CreateDirectory(Path.Combine(secondRoot, "foo")).FullName;
        var metadata = Path.Combine(real, "SKILL.md");
        const string bytes = "---\nname: foo\ndescription: retained skill\n---\nKeep.\n";
        File.WriteAllText(metadata, bytes);
        var roots = new[] { new ScanRoot(firstRoot, Scope.Custom, null), new ScanRoot(secondRoot, Scope.Custom, null) };
        var snapshot = InventorySnapshot.Empty with { Skills = new LocalSkillScanner(new Logger()).Scan(roots), ScannedRoots = roots.ToImmutableArray() };
        var candidates = CleanupClassifier.Classify(snapshot, roots);
        Assert.False(Assert.Single(candidates, candidate => candidate.Kind == CleanupClassifier.CandidateKind.EmptyDirectory).ReviewOnly);
        var services = Services();
        var result = await CliDispatcher.ApplyCleanupAsync(candidates, snapshot, services.RemoveService, services.Runner, Token);
        Assert.Contains(result.Applied, item => item.C.Kind == CleanupClassifier.CandidateKind.EmptyDirectory && item.R.Succeeded);
        Assert.False(Directory.Exists(empty));
        Assert.Equal(bytes, File.ReadAllText(metadata));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliApplyThroughAliasedRootHandlesCanonicalAndLexicalRecords(bool canonicalRecord)
    {
        if (OperatingSystem.IsWindows() || !SecureRemovalBackend.IsSupported)
        { Assert.Skip("Unix alias fixture and native removal required."); return; }
        var actual = CreateDirectory("actual");
        var empty = Directory.CreateDirectory(Path.Combine(actual, "empty")).FullName;
        var alias = Path.Combine(_temporary.FullName, "alias"); Directory.CreateSymbolicLink(alias, actual);
        var roots = new[] { new ScanRoot(alias, Scope.Custom, null) };
        var observed = Assert.Single(new LocalSkillScanner(new Logger()).Scan(roots));
        if (canonicalRecord) observed = observed with { ResolvedPath = OwnershipPath.EntryAddress(empty)! };
        var snapshot = InventorySnapshot.Empty with { Skills = [observed], ScannedRoots = roots.ToImmutableArray() };
        var candidate = Assert.Single(CleanupClassifier.Classify(snapshot, roots));
        Assert.Equal(Path.Combine(alias, "empty"), candidate.Path);
        Assert.False(candidate.ReviewOnly);
        var services = Services();
        var result = await CliDispatcher.ApplyCleanupAsync([candidate], snapshot, services.RemoveService, services.Runner, Token);
        Assert.True(Assert.Single(result.Applied).R.Succeeded);
        Assert.False(Directory.Exists(empty));
    }

    [Fact]
    public async Task CliApplyRefusesTrackedExposedAndConflictedEmptyDirectories()
    {
        var root = CreateDirectory("protected-skills");
        foreach (var name in new[] { "tracked", "exposed", "conflicted" })
            Directory.CreateDirectory(Path.Combine(root, name));
        var roots = new[] { new ScanRoot(root, Scope.Custom, null) };
        var scanned = new LocalSkillScanner(new Logger()).Scan(roots);
        var skills = scanned.Select(skill => Path.GetFileName(skill.ResolvedPath) switch
        {
            "tracked" => skill with { Provenance = Provenance.Both },
            "exposed" => skill with { Agents = skill.Agents.Add(new("codex", Path.Combine(_temporary.FullName, "consumer", "exposed"), true)) },
            "conflicted" => skill with { MetadataConflicts = ["recorded source conflict"] },
            _ => throw new InvalidOperationException("Unexpected fixture path")
        }).ToImmutableArray();
        var snapshot = InventorySnapshot.Empty with { Skills = skills, ScannedRoots = roots.ToImmutableArray() };
        var candidates = CleanupClassifier.Classify(snapshot, roots);
        Assert.Equal(3, candidates.Length);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(CleanupClassifier.CandidateKind.EmptyDirectory, candidate.Kind);
            Assert.True(candidate.ReviewOnly);
        });
        var services = Services();
        var execution = await CliDispatcher.ApplyCleanupAsync(candidates, snapshot, services.RemoveService, services.Runner, Token);
        using var json = JsonDocument.Parse(CliDispatcher.RenderCleanupJson(candidates, execution.Applied,
            CliDispatcher.ParseCleanupArgs(["--apply", "--yes", "--json"])));
        Assert.All(json.RootElement.GetProperty("entries").EnumerateArray(), entry => Assert.True(entry.GetProperty("reviewOnly").GetBoolean()));
        Assert.Equal(3, execution.Applied.Count);
        Assert.All(execution.Applied, item =>
        {
            Assert.False(item.R.Succeeded); Assert.Equal(0, item.R.FilesDeleted); Assert.Equal(0, item.R.DirectoriesDeleted);
            Assert.True(Directory.Exists(item.C.Path)); Assert.Empty(Directory.EnumerateFileSystemEntries(item.C.Path));
        });
    }

    private string CreateDirectory(string relative) => Directory.CreateDirectory(Path.Combine(_temporary.FullName, relative)).FullName;

    private TuiServices Services()
    {
        var home = CreateDirectory("home");
        return TuiServices.Build(new Logger(), processEnvironment: new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["XDG_DATA_HOME"] = Path.Combine(home, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(home, "cache"),
            ["APPDATA"] = Path.Combine(home, "appdata"),
            ["LOCALAPPDATA"] = Path.Combine(home, "localappdata"),
            ["GH_CONFIG_DIR"] = Path.Combine(home, "gh"),
            ["CODEX_HOME"] = Path.Combine(home, "codex"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude"),
            ["PI_CODING_AGENT_DIR"] = Path.Combine(home, "pi"),
            ["SKILLVIEW_STATE_HOME"] = Path.Combine(home, "maintenance-state"),
            ["GIT_DIR"] = Path.Combine(_temporary.FullName, ".git"),
            ["GIT_WORK_TREE"] = _temporary.FullName,
            ["GIT_COMMON_DIR"] = Path.Combine(_temporary.FullName, ".git"),
            ["GIT_CONFIG_COUNT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null"
        }, workingDirectory: _temporary.FullName);
    }

    private async Task<(int Code, string Output, string Error)> RunCliAsync(string root, params string[] arguments)
    {
        await CliConsoleCapture.Gate.WaitAsync(Token);
        var originalOut = Console.Out; var originalError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            var code = await CliDispatcher.RunAsync(ArgParser.Parse("skillview", ["--scan-root", root, .. arguments]), Services(), Token);
            return (code, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); CliConsoleCapture.Gate.Release(); }
    }
}
