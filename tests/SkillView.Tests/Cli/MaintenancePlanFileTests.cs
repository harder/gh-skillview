using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class MaintenancePlanFileTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("skillview-plan-reference-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Skills => Path.Combine(root.FullName, "skills");
    private string Payload => Path.Combine(Skills, "demo");
    private string ReferencePath => Path.Combine(root.FullName, "review.json");
    private readonly TuiServices services;
    private const string Metadata = "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/fixture/never-fetch\n  github-ref: refs/heads/main\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n---\n";

    public MaintenancePlanFileTests()
    {
        Directory.CreateDirectory(Payload);
        Directory.CreateDirectory(Path.Combine(root.FullName, "other"));
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata + "Original instructions\n");
        var home = Directory.CreateDirectory(Path.Combine(root.FullName, "home")).FullName;
        var environment = System.Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.StartsWith("GIT_", StringComparison.Ordinal)).ToDictionary(name => name, _ => "");
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
            ["APPDATA"] = Path.Combine(home, "appdata"),
            ["LOCALAPPDATA"] = Path.Combine(home, "local"),
            ["SKILLVIEW_STATE_HOME"] = Path.Combine(home, "maintenance"),
            ["CODEX_HOME"] = Path.Combine(home, "codex"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude"),
            ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = Path.Combine(home, "plugins"),
            ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
            ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
            ["PI_CODING_AGENT_DIR"] = Path.Combine(home, "pi"),
            ["GIT_DIR"] = Path.Combine(root.FullName, "private.git"),
            ["GIT_WORK_TREE"] = root.FullName,
            ["GIT_COMMON_DIR"] = Path.Combine(root.FullName, "private.git"),
            ["GIT_CONFIG_COUNT"] = "0",
            ["TMPDIR"] = root.FullName
        }) environment[name] = value;
        services = TuiServices.Build(new Logger(), logDirectory: Path.Combine(home, "logs"), processEnvironment: environment,
            workingDirectory: root.FullName);
    }

    private async Task<ReplacementResult> Prepare()
    {
        // Fixture-only immutable staging; no remote source or skill code runs.
        var inventory = await services.InventoryService.CaptureAsync(services.GhLocator.FindOnPath(), new([Skills, Path.Combine(root.FullName, "other")], false), Token);
        var target = Assert.Single(inventory.Skills, skill => skill.ResolvedPath == Payload);
        var plan = await services.SavedUpdatePlans.PlanAsync(target, inventory, (destination, _) =>
        {
            File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), Metadata + "Saved candidate instructions\n");
            File.WriteAllText(Path.Combine(destination.CanonicalPath, "resource.txt"), "Saved resource\n");
            return Task.CompletedTask;
        }, Token);
        // Reproduce the retired schema-1 locator from the persisted manifest.
        // Product planners no longer create or export references.
        using var manifest = JsonDocument.Parse(File.ReadAllText(plan.PlanPath));
        var reference = new MaintenancePlanReference(1, plan.Id, "restore-recorded", plan.PlanFingerprint,
            manifest.RootElement.GetProperty("contextFingerprint").GetString()!, plan.Subject.InstallationId, plan.OriginalPath);
        File.WriteAllText(ReferencePath, JsonSerializer.Serialize(reference, MaintenancePlanReferenceJsonContext.Default.MaintenancePlanReference));
        return plan;
    }

    [Fact]
    public async Task LegacyReferenceGuidancePreservesSpecialistInspectionApplyAndUndo()
    {
        var original = File.ReadAllBytes(Path.Combine(Payload, "SKILL.md"));
        var plan = await Prepare();
        var referenceBytes = File.ReadAllBytes(ReferencePath);
        var before = PayloadSnapshot.Capture(Payload, Token);
        var inspection = await Run("inspect", "--plan-file", ReferencePath, "--json");
        Assert.Equal(ExitCodes.Success, inspection.Code);
        using (var json = JsonDocument.Parse(inspection.Output))
        {
            var output = json.RootElement;
            Assert.Equal("guidance-only", output.GetProperty("status").GetString());
            Assert.Equal(plan.Id, output.GetProperty("reference").GetProperty("id").GetString());
            Assert.Equal("restore-recorded", output.GetProperty("reference").GetProperty("purpose").GetString());
            Assert.False(output.GetProperty("storeInspected").GetBoolean());
            Assert.False(output.GetProperty("mutationObserved").GetBoolean());
            Assert.Equal($"skillview restore-inspect --id {plan.Id}", output.GetProperty("inspectCommand").GetString());
            Assert.Contains($"restore-recover --id {plan.Id}", output.GetProperty("recoverCommand").GetString());
            Assert.Contains($"restore-undo --id {plan.Id}", output.GetProperty("undoCommand").GetString());
        }
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.False(Directory.Exists(plan.RetainedPath));
        var noSourceReview = await Run("restore-apply", "--id", plan.Id, "--yes", "--disposition", "retain-original", "--expect", plan.PlanFingerprint, "--json");
        Assert.Equal(ExitCodes.UserError, noSourceReview.Code);
        Assert.Contains("source-review acknowledgement", noSourceReview.Output);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Payload, "SKILL.md")));
        var sourceReview = Assert.IsType<SourceReviewEvidence>(plan.SourceReview);
        var acknowledged = await Run("restore-ack-source", "--id", plan.Id, "--expect", plan.PlanFingerprint,
            "--review", sourceReview.Fingerprint, "--yes", "--json");
        Assert.Equal(ExitCodes.Success, acknowledged.Code);
        var applied = await Run("restore-apply", "--id", plan.Id, "--yes", "--disposition", "retain-original", "--expect", plan.PlanFingerprint, "--json");
        Assert.Equal(ExitCodes.Success, applied.Code);
        Assert.Contains("Saved candidate instructions", File.ReadAllText(Path.Combine(Payload, "SKILL.md")));
        var verified = await Run("restore-inspect", "--id", plan.Id, "--json");
        Assert.Equal(ExitCodes.Success, verified.Code);
        using var observed = JsonDocument.Parse(verified.Output);
        Assert.Equal("installed", observed.RootElement.GetProperty("operation").GetProperty("status").GetString());
        Assert.Equal("verified-saved-locations", observed.RootElement.GetProperty("diskVerification").GetString());
        var currentReview = observed.RootElement.GetProperty("operation").GetProperty("reviewFingerprint").GetString()!;
        var undo = await Run("restore-undo", "--id", plan.Id, "--yes", "--expect", currentReview, "--json");
        Assert.Equal(ExitCodes.Success, undo.Code);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Payload, "SKILL.md")));
        Assert.Equal(referenceBytes, File.ReadAllBytes(ReferencePath));
    }

    [Theory]
    [InlineData("update-revision", "update")]
    [InlineData("restore-recorded", "restore")]
    [InlineData("unpin-only", "unpin")]
    [InlineData("local-reimport", "reimport")]
    [InlineData("switch-local-source", "source-switch")]
    public async Task UnverifiedLocatorReportsExactPurposeAndGuidanceWithoutCreatingState(string purpose, string prefix)
    {
        var reference = new MaintenancePlanReference(1, new('a', 32), purpose, new('b', 64), new('c', 64), new('d', 64), Payload);
        // Old responses and bare reference objects are both supported.
        File.WriteAllText(ReferencePath, "{\"planReference\":" + JsonSerializer.Serialize(reference,
            MaintenancePlanReferenceJsonContext.Default.MaintenancePlanReference) + ",\"operation\":{\"untrusted\":true}}");
        var inspected = await Run("inspect", "--plan-file", ReferencePath, "--json");
        Assert.Equal(ExitCodes.Success, inspected.Code);
        using var json = JsonDocument.Parse(inspected.Output);
        Assert.False(json.RootElement.GetProperty("storeInspected").GetBoolean());
        Assert.Equal(purpose, json.RootElement.GetProperty("reference").GetProperty("purpose").GetString());
        Assert.Equal($"skillview {prefix}-inspect --id {reference.Id}", json.RootElement.GetProperty("inspectCommand").GetString());
        Assert.False(Directory.Exists(Path.Combine(root.FullName, "home", "maintenance")));
    }

    [Fact]
    public async Task ExactInstallationInspectionDoesNotSelectASameNameSibling()
    {
        var sibling = Directory.CreateDirectory(Path.Combine(root.FullName, "other", "demo")).FullName;
        File.WriteAllText(Path.Combine(sibling, "SKILL.md"), Metadata);
        var result = await Run("inspect", "--path", Payload, "--json");
        Assert.Equal(ExitCodes.Success, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Equal(Payload, entry.GetProperty("subject").GetProperty("path").GetString()));
        var missing = await Run("inspect", "--path", Path.Combine(Skills, "missing"), "--json");
        Assert.Equal(ExitCodes.NoMatches, missing.Code);
        Assert.Contains("no sibling was selected", missing.Output);
    }

    [Fact]
    public async Task BoundedNativeReferenceRejectsLinksMalformedDuplicatesAndPayloadAuthority()
    {
        var plan = await Prepare();
        var text = File.ReadAllText(ReferencePath);
        File.WriteAllText(ReferencePath, text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"));
        Assert.Throws<IOException>(() => MaintenancePlanReference.Read(ReferencePath, Token));
        File.WriteAllText(ReferencePath, text[..^1] + ",\"candidatePath\":\"/untrusted\"}");
        Assert.Throws<JsonException>(() => MaintenancePlanReference.Read(ReferencePath, Token));
        File.WriteAllText(ReferencePath, new string(' ', MaintenancePlanReference.MaxBytes + 1));
        Assert.Throws<IOException>(() => MaintenancePlanReference.Read(ReferencePath, Token));
        File.WriteAllText(ReferencePath, "[]");
        Assert.Throws<IOException>(() => MaintenancePlanReference.Read(ReferencePath, Token));
        File.Delete(ReferencePath);
        File.CreateSymbolicLink(ReferencePath, plan.PlanPath);
        Assert.ThrowsAny<IOException>(() => MaintenancePlanReference.Read(ReferencePath, Token));
    }

    [Theory]
    [InlineData("inspect --plan-file missing --json --json")]
    [InlineData("inspect --plan-file missing --id override --json")]
    [InlineData("inspect --plan-file missing --yes --json")]
    [InlineData("inspect --plan-file --json")]
    public async Task UnsupportedAliasesAndDuplicateArgumentsHaveVersionedRefusalWithoutState(string arguments)
    {
        var result = await Run(arguments.Split(' '));
        Assert.Equal(ExitCodes.InvalidUsage, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("refused", json.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("reason").GetString()));
        Assert.False(json.RootElement.GetProperty("mutationObserved").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(root.FullName, "home", "maintenance")));
    }

    private async Task<(int Code, string Output)> Run(params string[] args)
    {
        await CliConsoleCapture.Gate.WaitAsync(Token);
        var originalOut = Console.Out; var originalErr = Console.Error;
        using var output = new StringWriter(); using var errors = new StringWriter();
        Console.SetOut(output); Console.SetError(errors);
        try
        {
            var code = await CliDispatcher.RunAsync(ArgParser.Parse("skillview", ["--scan-root", Skills, "--scan-root", Path.Combine(root.FullName, "other"), .. args]), services, Token);
            Assert.True(output.ToString().Length > 0, errors.ToString());
            return (code, output.ToString());
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalErr); CliConsoleCapture.Gate.Release(); }
    }

    public void Dispose() => root.Delete(true);
}
