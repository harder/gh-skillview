using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class ReplacementLocalReviewTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-local-review-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Skills => Path.Combine(temporary.FullName, "skills");
    private string Payload => Path.Combine(Skills, "demo");
    private string State => Path.Combine(temporary.FullName, "state");
    private const string OldTree = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", NewTree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Metadata = "---\nname: demo\ndescription: Fixture\nversion: '1.0'\nmetadata:\n  github-repo: https://github.com/harder/fixture\n  github-ref: refs/heads/main\n  github-path: skills/demo\n  github-tree-sha: " + OldTree + "\n---\n";
    private static ResolvedSkillRevision Source => new("https://github.com/harder/fixture", "refs/tags/v2", new('c', 40), NewTree, "skills/demo");
    private ReplacementPlanStore Store(Action<string>? checkpoint = null, bool sourceReviewRequired = false) => new(State, new Logger(), checkpoint,
        new ProcessRunner(new Logger(), environment: new Dictionary<string, string>
        { ["HOME"] = temporary.FullName, ["USERPROFILE"] = temporary.FullName }, defaultWorkingDirectory: temporary.FullName), sourceReviewRequired: sourceReviewRequired);
    public ReplacementLocalReviewTests()
    {
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata + "Installed instructions\n");
        File.WriteAllText(Path.Combine(Payload, "extra"), "Keep local extra");
        File.WriteAllText(Path.Combine(Payload, "resource"), "Base resource");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Payload, (UnixFileMode)448);
    }
    public void Dispose() => temporary.Delete(true);
    private InventorySnapshot Inventory()
    {
        ScanRoot[] roots = [new(Skills, Scope.Custom, null)];
        return InventorySnapshot.Empty with
        {
            Skills = new LocalSkillScanner(new Logger()).Scan(roots).ToImmutableArray(),
            ScannedRoots = roots.ToImmutableArray(),
            ConfiguredRoots = roots.ToImmutableArray()
        };
    }
    private Task<ReplacementResult> Plan()
    {
        var inventory = Inventory();
        return Store().PlanUpdateAsync(Assert.Single(inventory.Skills), inventory, Source, Stage, Base, false, Token);
        Task Stage(NativeStateDirectory directory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(directory.CanonicalPath, "SKILL.md"), Metadata.Replace(OldTree, NewTree).Replace("refs/heads/main", "refs/tags/v2").Replace("version: '1.0'", "version: '2.0'") + "Source instructions\n");
            File.WriteAllText(Path.Combine(directory.CanonicalPath, "resource"), "New source resource");
            Directory.CreateDirectory(Path.Combine(directory.CanonicalPath, "empty"));
            return Task.CompletedTask;
        }
        Task Base(NativeStateDirectory directory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(directory.CanonicalPath, "SKILL.md"), Metadata + "Base instructions\n");
            File.WriteAllText(Path.Combine(directory.CanonicalPath, "resource"), "Base resource");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SavedReviewSurvivesRestartAndClosesOldApprovalWithoutChangingInstalledObjects()
    {
        var before = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Plan();
        var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.False(review.CanMaterialize);
        Assert.Null(review.Review.Paths.Single(row => row.Path == "SKILL.md").Selection);
        Assert.Equal("installed", review.Review.Paths.Single(row => row.Path == "extra").Selection);
        Assert.Equal("source", review.Review.Paths.Single(row => row.Path == "resource").Selection);
        Assert.Equal(review.ReviewFingerprint, Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token).ReviewFingerprint);
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Throws<IOException>(() => Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), false, Token));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task ExplicitChoicesAreAppendOnlyBoundedAndNewestCorruptionDoesNotRevivePriorSelection()
    {
        var plan = await Plan();
        var first = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var chosen = Store().ChooseLocalChanges(plan.Id, first.ReviewFingerprint, [new("SKILL.md", "installed")], Inventory(), Token);
        Assert.NotEqual(first.ReviewFingerprint, chosen.ReviewFingerprint);
        Assert.Equal("installed", chosen.Review.Paths.Single(row => row.Path == "SKILL.md").Selection);
        Assert.Throws<IOException>(() => Store().ChooseLocalChanges(plan.Id, first.ReviewFingerprint, [new("SKILL.md", "source")], Inventory(), Token));
        var latest = Path.Combine(chosen.ReviewPath, "choices-01.json");
        File.WriteAllText(latest, "{\"schemaVersion\":1,");
        Assert.ThrowsAny<JsonException>(() => Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Theory]
    [InlineData("installed")]
    [InlineData("source")]
    public async Task MergedPlanPreservesLocalExtrasRootPrivacyAndRealSourceBaselineThenUndoesExactly(string side)
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip("Merged native metadata publication currently requires macOS evidence."); return; }
        var before = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Plan();
        var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var chosen = Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint, [new("SKILL.md", side)], Inventory(), Token);
        Assert.True(chosen.CanMaterialize, string.Join("; ", chosen.MetadataRefusals));
        var merged = Store().FinalizeLocalChanges(plan.Id, chosen.ReviewFingerprint, Inventory(), Token);
        Assert.Equal("merge-reviewed", merged.LocalDisposition!.Kind);
        Assert.Equal(side == "installed" ? "1.0" : "2.0", merged.LocalDisposition.ResultAuthoredVersion);
        Assert.Equal("2.0", merged.Revision!.DesiredMetadata.Version);
        var rendered = SkillView.Ui.UpdateScreen.RenderPlan(merged);
        Assert.Contains(side == "installed" ? "1.0 -> 1.0" : "1.0 -> 2.0", rendered);
        Assert.Contains("Source authored version", rendered);
        Assert.Equal(plan.PlanFingerprint, merged.LocalDisposition.SourcePlanFingerprint);
        Assert.EndsWith("merged-00", merged.CandidatePath);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(OwnershipPath.CanonicalCandidate(plan.CandidatePath), merged.LocalDisposition.SourceCandidate.Identity.CanonicalPath);
        Assert.Equal("Source instructions\n", File.ReadAllText(Path.Combine(plan.CandidatePath, "SKILL.md")).Split("---\n")[2]);
        Assert.Throws<IOException>(() => Store().Apply(merged.Id, merged.PlanFingerprint, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "merge-reviewed"));
        var applied = Store().Apply(merged.Id, merged.PlanFingerprint, Inventory(), Token, "merge-reviewed");
        Assert.True(applied.Succeeded, applied.Message);
        Assert.Equal("installed", applied.Status);
        Assert.Equal("Keep local extra", File.ReadAllText(Path.Combine(Payload, "extra")));
        Assert.Equal("New source resource", File.ReadAllText(Path.Combine(Payload, "resource")));
        Assert.True(Directory.Exists(Path.Combine(Payload, "empty")));
        Assert.Equal((UnixFileMode)448, File.GetUnixFileMode(Payload));
        var metadata = Assert.Single(Inventory().Skills).FrontMatter;
        Assert.Equal(NewTree, metadata.GithubTreeSha); Assert.Equal("refs/tags/v2", metadata.GithubRef);
        Assert.Contains(side == "installed" ? "Installed instructions" : "Source instructions", File.ReadAllText(Path.Combine(Payload, "SKILL.md")));
        var logger = new Logger();
        var runner = new ProcessRunner(logger, environment: new Dictionary<string, string>
        { ["HOME"] = temporary.FullName, ["USERPROFILE"] = temporary.FullName }, defaultWorkingDirectory: temporary.FullName);
        var catalog = new SkillView.Gh.GhSkillListAdapter(runner, logger);
        var availability = new SkillUpdateCoordinator(new(runner, logger), new(runner, logger), catalog, logger, runner);
        var coordinator = new SavedUpdateCoordinator(Store(), availability, catalog, logger);
        var inspection = coordinator.InspectUndo(applied, Inventory(), Token);
        var undone = await coordinator.UndoAsync(inspection, Inventory(), _ => Task.FromResult(Inventory()), Token);
        Assert.True(undone.Succeeded, undone.ErrorMessage); Assert.Equal("restored", undone.SavedPlan!.Status);
        var restored = PayloadSnapshot.Capture(Payload, Token);
        Assert.Equal(before.Fingerprint, restored.Fingerprint);
        Assert.Equal(before.Objects, restored.Objects);
        Assert.Equal(before.RootUnixMode, restored.RootUnixMode);
        Assert.True(NativeDirectoryMoveLease.SameObject(before.RootIdentity!.Value, restored.RootIdentity!.Value));
    }

    [Theory]
    [InlineData("retain-moved")]
    [InlineData("install-moved")]
    public async Task InterruptedMergedPublicationRecoversFromSavedEvidenceWithoutReResolving(string stop)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var plan = await Plan(); var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var chosen = Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint, [new("SKILL.md", "installed")], Inventory(), Token);
        var merged = Store().FinalizeLocalChanges(plan.Id, chosen.ReviewFingerprint, Inventory(), Token);
        var interrupted = Store(step => { if (step == stop) throw new IOException("interrupt"); })
            .Apply(plan.Id, merged.PlanFingerprint, Inventory(), Token, "merge-reviewed");
        Assert.False(interrupted.Succeeded); Assert.True(interrupted.MutationObserved);
        var inspection = Store().Inspect(plan.Id, Inventory(), Token);
        var recovered = Store().Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), false, Token);
        Assert.True(recovered.Succeeded, recovered.Message); Assert.Equal("installed", recovered.Status);
        Assert.Equal("Keep local extra", File.ReadAllText(Path.Combine(Payload, "extra")));
    }

    [Fact]
    public async Task PartialFinalizationIsRetainedAndNewAttemptCanCompleteWithoutRevivingOldApproval()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var plan = await Plan(); var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var chosen = Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint, [new("SKILL.md", "installed")], Inventory(), Token);
        var operation = Path.Combine(State, plan.Id);
        Directory.CreateDirectory(Path.Combine(operation, "merged-00"));
        File.WriteAllText(Path.Combine(operation, "merge-plan-00.json"), "{\"schemaVersion\":3,");
        Assert.Equal("ready", Store().Inspect(plan.Id, Inventory(), Token).Status);
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Equal(chosen.ReviewFingerprint, Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token).ReviewFingerprint);
        var merged = Store().FinalizeLocalChanges(plan.Id, chosen.ReviewFingerprint, Inventory(), Token);
        Assert.EndsWith("merged-01", merged.CandidatePath);
        Assert.Equal("{\"schemaVersion\":3,", File.ReadAllText(Path.Combine(operation, "merge-plan-00.json")));
        Assert.True(Store().Apply(plan.Id, merged.PlanFingerprint, Inventory(), Token, "merge-reviewed").Succeeded);
    }

    [Fact]
    public async Task ChangedInputOrImmutableManifestRefusesBeforeAnyLiveMove()
    {
        var plan = await Plan(); var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        File.WriteAllText(Path.Combine(Payload, "extra"), "Changed after review");
        Assert.Throws<IOException>(() => Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint, [new("SKILL.md", "installed")], Inventory(), Token));
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task InterruptedCaptureClosesOldApprovalAndRetainsPartialEvidence()
    {
        var before = PayloadSnapshot.Capture(Payload, Token); var plan = await Plan();
        Assert.Throws<IOException>(() => Store(step => { if (step == "local-review-created") throw new IOException("interrupt"); })
            .ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.True(Directory.Exists(Path.Combine(State, plan.Id, "local-review")));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task PreserveVerifiesLiveObjectsWithoutWritingPlanOwnerOrPreferences()
    {
        var before = PayloadSnapshot.Capture(Payload, Token); var plan = await Plan();
        var files = Directory.EnumerateFiles(State, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(State, path), File.ReadAllBytes, StringComparer.Ordinal);
        var result = Store().PreserveInstalled(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.Equal("preserved", result.Status); Assert.True(result.Succeeded); Assert.False(result.MutationObserved);
        Assert.Equal(plan.OriginalPath, result.Impact!.RetainedPayloadPath);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        var after = Directory.EnumerateFiles(State, "*", SearchOption.AllDirectories).ToArray();
        Assert.Equal(files.Count, after.Length);
        foreach (var path in after) Assert.Equal(files[Path.GetRelativePath(State, path)], File.ReadAllBytes(path));
    }
}
