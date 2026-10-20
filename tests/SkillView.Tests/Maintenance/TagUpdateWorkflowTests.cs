using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class TagUpdateWorkflowTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("skillview-tag-update-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Skills => Path.Combine(root.FullName, "skills");
    private string Other => Path.Combine(root.FullName, "other");
    private string Payload => Path.Combine(Skills, "demo");
    private string Gh => Path.Combine(root.FullName, "gh-fixture");
    private string Calls => Path.Combine(root.FullName, "calls");
    private readonly TuiServices services;
    private readonly string originalTree;
    private readonly string selectedTree;
    private const string Commit = "cccccccccccccccccccccccccccccccccccccccc";

    public TagUpdateWorkflowTests()
    {
        Directory.CreateDirectory(Payload);
        Directory.CreateDirectory(Path.Combine(Other, "demo"));
        var original = Source("Original source"); var selected = Source("Chosen older source");
        var responses = new Dictionary<string, string>();
        originalTree = AddTree(original); selectedTree = AddTree(selected);
        var tracking = new SkillFrontMatter
        {
            Name = "demo",
            Upstream = "https://github.com/fixture/repo",
            GithubRef = "refs/tags/newer",
            GithubPath = "",
            GithubTreeSha = originalTree
        };
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), RecordedTrackingWriter.Bind(original, tracking) + "Local note\n");
        File.WriteAllText(Path.Combine(Payload, "extra.txt"), "Retain this local file");
        File.WriteAllText(Path.Combine(Other, "demo", "SKILL.md"), "---\nname: demo\ndescription: Independent\n---\nKeep\n");
        responses["repos/fixture/repo/git/ref/tags/older"] =
            "HTTP/1.1 200 OK\n\n" + JsonSerializer.Serialize(new { @ref = "refs/tags/older", @object = new { sha = Commit, type = "commit" } });
        responses[$"repos/fixture/repo/git/commits/{Commit}"] =
            "HTTP/1.1 200 OK\n\n" + JsonSerializer.Serialize(new { sha = Commit, tree = new { sha = selectedTree } });
        var script = new StringBuilder("#!/bin/sh\nfor endpoint do :; done\n");
        script.AppendLine("printf '%s\\n' \"$endpoint\" >> " + Quote(Calls));
        script.AppendLine("case \"$endpoint\" in");
        var index = 0;
        foreach (var (endpoint, response) in responses)
        {
            var file = Path.Combine(root.FullName, "response-" + index++);
            File.WriteAllText(file, response);
            script.AppendLine(Quote(endpoint) + ") /bin/cat " + Quote(file) + ";; ");
        }
        script.AppendLine("*) printf 'Unexpected source request\\n' >&2; exit 9;;\nesac");
        File.WriteAllText(Gh, script.ToString());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Gh, (UnixFileMode)448);
        var environment = new Dictionary<string, string>(SkillUpdateCoordinator.CreateStagingEnvironment(Path.Combine(root.FullName, "child")))
        { ["SKILLVIEW_STATE_HOME"] = Path.Combine(root.FullName, "state") };
        foreach (var name in System.Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)))
            environment[name] = null!;
        environment["GIT_CONFIG_COUNT"] = "0";
        services = TuiServices.Build(new Logger(), processEnvironment: environment, workingDirectory: root.FullName);

        string AddTree(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var blob = GhRecordedTreeStager.GitObjectHash("blob", bytes);
            using var entry = new MemoryStream();
            entry.Write(Encoding.ASCII.GetBytes("100644 SKILL.md\0")); entry.Write(Convert.FromHexString(blob));
            var tree = GhRecordedTreeStager.GitObjectHash("tree", entry.ToArray());
            responses[$"repos/fixture/repo/git/trees/{tree}?recursive=1"] = JsonSerializer.Serialize(new
            { sha = tree, truncated = false, tree = new[] { new { path = "SKILL.md", type = "blob", mode = "100644", sha = blob, size = bytes.Length } } });
            responses[$"repos/fixture/repo/git/blobs/{blob}"] = Convert.ToBase64String(bytes);
            return tree;
        }
    }

    private static string Source(string body) => "---\nname: demo\ndescription: Fixture\n---\n" + body + "\n";
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private InventorySnapshot Inventory()
    {
        ScanRoot[] roots = [new(Skills, Scope.Custom, null), new(Other, Scope.Custom, null)];
        return InventorySnapshot.Empty with
        {
            Skills = services.Scanner.Scan(roots).ToImmutableArray(),
            ScannedRoots = [.. roots],
            ConfiguredRoots = [.. roots]
        };
    }
    private InstalledSkill Target(InventorySnapshot inventory) => Assert.Single(inventory.Skills, skill => PathIdentity.Equals(skill.ResolvedPath, Payload));
    public void Dispose() => root.Delete(true);

    [Fact]
    public async Task ChosenTagUsesSavedReviewAndApplyUndoWithoutResolvingAgain()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("POSIX gh protocol fixture; shared saved-update native contracts cover Windows publication."); return; }
        var before = PayloadSnapshot.Capture(Payload, Token);
        var other = File.ReadAllBytes(Path.Combine(Other, "demo", "SKILL.md"));
        var inventory = Inventory();
        var prepared = await services.SavedUpdates.PrepareAsync(Gh, new(Targets: [Target(inventory)], Tag: "older"), inventory, Token);
        Assert.True(prepared.Succeeded, prepared.ErrorMessage);
        var plan = Assert.IsType<ReplacementResult>(prepared.SavedPlan);
        Assert.Equal("refs/tags/older", plan.Revision!.Source.Ref);
        Assert.Equal(Commit, plan.Revision.Source.Commit);
        Assert.Equal(selectedTree, plan.Revision.Source.Tree);
        Assert.Equal(originalTree, plan.Subject.Metadata.GithubTreeSha);
        Assert.Contains("extra.txt", plan.Revision.LocalChanges);
        Assert.NotNull(plan.SourceReview);
        Assert.Contains(prepared.Notices, notice => notice.Text.Contains("later ordinary Update follows latest", StringComparison.Ordinal));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.Throws<IOException>(() => services.SavedUpdatePlans.Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        services.SavedUpdatePlans.AcknowledgeSourceReview(plan.Id, plan.PlanFingerprint, plan.SourceReview.Fingerprint, Inventory(), Token);
        var requests = File.ReadAllBytes(Calls);
        // No executable remains: apply and undo must consume only the saved evidence.
        File.Delete(Gh);
        var applied = services.SavedUpdatePlans.Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.True(applied.Succeeded, applied.Message);
        Assert.Equal(selectedTree, Target(Inventory()).TreeSha);
        Assert.False(Target(Inventory()).Pinned);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(plan.RetainedPath, Token).Fingerprint);
        Assert.Equal(other, File.ReadAllBytes(Path.Combine(Other, "demo", "SKILL.md")));
        var inspection = services.SavedUpdatePlans.Inspect(plan.Id, Inventory(), Token);
        Assert.True(services.SavedUpdatePlans.Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), true, Token).Succeeded);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(requests, File.ReadAllBytes(Calls));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("dry-run")]
    [InlineData("unpin")]
    [InlineData("multiple")]
    public async Task InvalidTagSelectionRefusesBeforeAnySourceRequest(string invalid)
    {
        var inventory = Inventory();
        var result = await services.SavedUpdates.PrepareAsync(Gh, new(Tag: "older", Targets: invalid == "multiple" ? inventory.Skills : [Target(inventory)],
            All: invalid == "all", DryRun: invalid == "dry-run", Unpin: invalid == "unpin"), inventory, Token);
        Assert.False(result.Succeeded);
        Assert.Null(result.SavedPlan);
        Assert.False(File.Exists(Calls));
    }

    [Fact]
    public async Task ExistingPinRefusesTagPlanningAndDirectGhAdapters()
    {
        var inventory = Inventory();
        var target = Target(inventory) with { FrontMatter = Target(inventory).FrontMatter with { Pinned = true, PinnedRef = "newer" } };
        var result = await services.SavedUpdates.PrepareAsync(Gh, new(Tag: "older", Targets: [target]), inventory, Token);
        Assert.Equal("pinned", Assert.Single(result.Outcomes).Status);
        await Assert.ThrowsAsync<IOException>(() => services.SavedUpdatePlans.PlanTagAsync(Gh, target, inventory, "older", Token));
        Assert.False((await services.UpdateService.UpdateAsync(Gh, new(Tag: "older", Targets: [target]), Token)).Succeeded);
        Assert.False(File.Exists(Calls));
    }

    [Theory]
    [InlineData("update-plan", "--tag", "../main")]
    [InlineData("update-plan", "--tag=")]
    [InlineData("update-plan", "--tag", "older", "--unpin")]
    [InlineData("update-plan", "--tag", "older", "--tag", "another")]
    [InlineData("restore-plan", "--tag", "older")]
    [InlineData("update-apply", "--tag", "older")]
    public async Task CliRefusesInvalidOrMisplacedTagBeforeInventory(params string[] arguments)
    {
        await SkillView.Tests.Cli.CliConsoleCapture.Gate.WaitAsync(Token);
        var savedOut = Console.Out; var savedError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            var result = await CliDispatcher.RunAsync(ArgParser.Parse("skillview", [.. arguments, "--path", Payload]), services, Token);
            Assert.Equal(ExitCodes.InvalidUsage, result);
            Assert.False(string.IsNullOrWhiteSpace(error.ToString()));
            Assert.False(File.Exists(Calls));
        }
        finally
        {
            Console.SetOut(savedOut); Console.SetError(savedError);
            SkillView.Tests.Cli.CliConsoleCapture.Gate.Release();
        }
    }
}
