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

    [Theory]
    [InlineData("installed")]
    [InlineData("source")]
    public async Task MergedPlanPreservesLocalExtrasRootPrivacyAndRealSourceBaselineThenUndoesExactly(string side)
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip("Merged native metadata publication currently requires macOS evidence."); return; }
        var before = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Plan();
        HistoricalMergeFixture.Create(State, plan, Token, side);
        var merged = Store().Inspect(plan.Id, Inventory(), Token);
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
        var plan = await Plan();
        HistoricalMergeFixture.Create(State, plan, Token);
        var merged = Store().Inspect(plan.Id, Inventory(), Token);
        var interrupted = Store(step => { if (step == stop) throw new IOException("interrupt"); })
            .Apply(plan.Id, merged.PlanFingerprint, Inventory(), Token, "merge-reviewed");
        Assert.False(interrupted.Succeeded); Assert.True(interrupted.MutationObserved);
        var inspection = Store().Inspect(plan.Id, Inventory(), Token);
        var recovered = Store().Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), false, Token);
        Assert.True(recovered.Succeeded, recovered.Message); Assert.Equal("installed", recovered.Status);
        Assert.Equal("Keep local extra", File.ReadAllText(Path.Combine(Payload, "extra")));
    }

    [Fact]
    public async Task HistoricalReviewCapturesCanonicalCandidateThroughObservedAncestorAlias()
    {
        var plan = await Plan();
        var original = PayloadSnapshot.Capture(Payload, Token);
        var savedBytes = File.ReadAllBytes(plan.PlanPath);
        var saved = JsonSerializer.Deserialize(savedBytes, ReplacementJsonContext.Default.ReplacementPlan)!;
        var alias = Path.Combine(temporary.FullName, "ancestor-alias");
        Directory.CreateSymbolicLink(alias, temporary.FullName);
        try
        {
            var aliasedState = Path.Combine(alias, "state");
            var observed = plan with
            {
                CandidatePath = Path.Combine(aliasedState, plan.Id, "candidate"),
                PlanPath = Path.Combine(aliasedState, plan.Id, "plan.json")
            };
            var canonicalOperation = Path.GetDirectoryName(saved.Candidate.Identity.CanonicalPath)!;
            Assert.NotEqual(observed.CandidatePath, saved.Candidate.Identity.CanonicalPath);
            // The native guard must still reject mixing canonical roots with
            // observed aliases, even when both names reach the same object.
            Assert.Throws<IOException>(() =>
            {
                using var lease = NativeDirectoryMoveLease.Open(canonicalOperation, observed.CandidatePath, Token);
            });
            var review = HistoricalMergeFixture.CaptureReview(aliasedState, observed, Token);
            Assert.Equal(saved.Candidate.Identity.CanonicalPath, review.Source.Snapshot.Path);
            Assert.Equal(saved.Candidate.Payload, review.Source.Snapshot.Fingerprint);
            Assert.Equal(Path.Combine(aliasedState, plan.Id, "candidate"), observed.CandidatePath);
            Assert.Equal(savedBytes, File.ReadAllBytes(observed.PlanPath));
            Assert.Equal(original.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
            Assert.False(Directory.Exists(plan.RetainedPath));
        }
        finally { Directory.Delete(alias); }
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
