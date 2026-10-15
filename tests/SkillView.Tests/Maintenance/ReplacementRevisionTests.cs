using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class ReplacementRevisionTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-revision-plan-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Skills => Path.Combine(temporary.FullName, "skills");
    private string Payload => Path.Combine(Skills, "demo");
    private string State => Path.Combine(temporary.FullName, "state");
    private const string OriginalTree = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", NewTree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Metadata = "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/harder/fixture\n  github-ref: refs/heads/main\n  github-path: skills/demo\n  github-tree-sha: " + OriginalTree + "\n---\n";
    private static ResolvedSkillRevision Source => new("https://github.com/harder/fixture", "refs/tags/v2", new('c', 40), NewTree, "skills/demo");
    private static string CandidateMetadata => Metadata.Replace(OriginalTree, NewTree).Replace("refs/heads/main", "refs/tags/v2");
    private ReplacementPlanStore Store(Action<string>? checkpoint = null) => new(State, new Logger(), checkpoint,
        new ProcessRunner(new Logger(), environment: new Dictionary<string, string>
        {
            ["HOME"] = temporary.FullName,
            ["USERPROFILE"] = temporary.FullName,
            ["XDG_STATE_HOME"] = Path.Combine(temporary.FullName, "xdg-state")
        }, defaultWorkingDirectory: temporary.FullName));
    public ReplacementRevisionTests()
    {
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata + "Local changes\n");
        File.WriteAllText(Path.Combine(Payload, "extra"), "Keep original extra");
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
    private static Task Stage(NativeStateDirectory directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        File.WriteAllText(Path.Combine(directory.CanonicalPath, "SKILL.md"), CandidateMetadata.Replace("description: Fixture", "description: New source description\nversion: '2.0'") + "New source\n");
        File.WriteAllText(Path.Combine(directory.CanonicalPath, "script"), "New resource");
        return Task.CompletedTask;
    }
    private static Task Base(NativeStateDirectory directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        File.WriteAllText(Path.Combine(directory.CanonicalPath, "SKILL.md"), Metadata + "Original source\n");
        return Task.CompletedTask;
    }
    private Task<ReplacementResult> Plan(ReplacementPlanStore? store = null, bool unpin = false)
    {
        var inventory = Inventory();
        return (store ?? Store()).PlanUpdateAsync(Assert.Single(inventory.Skills), inventory, Source, Stage, Base, unpin, Token);
    }

    [Fact]
    public async Task SavedUpdateReviewsLocalChangesPromotesExactCandidateAndUndoesToOriginal()
    {
        var original = PayloadSnapshot.Capture(Payload, Token);
        var planned = await Plan();
        Assert.NotNull(planned.Revision); Assert.Equal(Source, planned.Revision.Source);
        Assert.Equal("2.0", planned.Revision.DesiredMetadata.Version);
        Assert.Equal("New source description", planned.Revision.DesiredMetadata.Description);
        Assert.Equal(NewTree, planned.Revision.DesiredMetadata.Metadata["github-tree-sha"]);
        Assert.Equal(new[] { "SKILL.md", "extra" }, planned.Revision.LocalChanges);
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        var candidate = PayloadSnapshot.Capture(planned.CandidatePath, Token);
        var applied = Store().Apply(planned.Id, planned.PlanFingerprint, Inventory(), Token);
        Assert.True(applied.Succeeded, applied.Message); Assert.Equal("installed", applied.Status);
        Assert.Equal(candidate.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(planned.RetainedPath, Token).Fingerprint);
        var metadata = Assert.Single(Inventory().Skills).FrontMatter;
        Assert.Equal(NewTree, metadata.GithubTreeSha); Assert.Equal("refs/tags/v2", metadata.GithubRef);
        Assert.False(metadata.Pinned); Assert.Null(metadata.PinnedRef);
        var inspect = Store().Inspect(planned.Id, Inventory(), Token);
        var undone = Store().Recover(planned.Id, inspect.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(candidate.Fingerprint, PayloadSnapshot.Capture(undone.DisplacedPath, Token).Fingerprint);
    }

    [Fact]
    public async Task RoutinePinIsRefusedBeforeStagingAndExplicitUnpinIsReviewed()
    {
        var path = Path.Combine(Payload, "SKILL.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("  github-path:", "  github-pinned: old-pin\n  github-path:"));
        await Assert.ThrowsAsync<IOException>(() => Plan());
        Assert.False(Directory.Exists(State));
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        Task PinnedBase(NativeStateDirectory directory, CancellationToken token)
        {
            File.WriteAllText(Path.Combine(directory.CanonicalPath, "SKILL.md"), Metadata.Replace("  github-path:", "  github-pinned: old-pin\n  github-path:") + "Original source\n");
            return Task.CompletedTask;
        }
        var planned = await Store().PlanUpdateAsync(target, inventory, Source, Stage, PinnedBase, true, Token);
        Assert.Equal("old-pin", planned.Subject.Metadata.PinnedRef);
        Assert.Null(planned.Revision!.DesiredMetadata.PinnedRef);
        Assert.False(planned.Revision.DesiredMetadata.Metadata.ContainsKey("github-pinned"));
        var result = Store().Apply(planned.Id, planned.PlanFingerprint, Inventory(), Token);
        Assert.True(result.Succeeded, result.Message); Assert.False(Assert.Single(Inventory().Skills).Pinned);
        var inspect = Store().Inspect(planned.Id, Inventory(), Token);
        Assert.True(Store().Recover(planned.Id, inspect.ReviewFingerprint, Inventory(), true, Token).Succeeded);
        Assert.Equal("old-pin", Assert.Single(Inventory().Skills).FrontMatter.PinnedRef);
    }

    [Theory]
    [InlineData("retain-moved")]
    [InlineData("install-moved")]
    public async Task InterruptedNewRevisionRecoversWithoutResolvingSourceAgain(string stop)
    {
        var planned = await Plan();
        var interrupted = Store(step => { if (step == stop) throw new IOException("interrupt"); })
            .Apply(planned.Id, planned.PlanFingerprint, Inventory(), Token);
        Assert.False(interrupted.Succeeded); Assert.True(interrupted.MutationObserved);
        var inspect = Store().Inspect(planned.Id, Inventory(), Token);
        var recovered = Store().Recover(planned.Id, inspect.ReviewFingerprint, Inventory(), false, Token);
        Assert.True(recovered.Succeeded, recovered.Message); Assert.Equal("installed", recovered.Status);
        Assert.Equal(NewTree, Assert.Single(Inventory().Skills).TreeSha);
    }

    [Fact]
    public async Task ModifiedRecordedBaseRefusesBeforePayloadMoves()
    {
        var planned = await Plan(); var original = PayloadSnapshot.Capture(Payload, Token);
        var baseline = Path.Combine(State, planned.Id, "recorded-base", "SKILL.md");
        File.AppendAllText(baseline, "changed");
        Assert.Throws<IOException>(() => Store().Apply(planned.Id, planned.PlanFingerprint, Inventory(), Token));
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.False(Directory.Exists(planned.RetainedPath));
    }

    [Fact]
    public async Task FailedBaselineStageReportsRetainedEvidenceAndLeavesInstallationUnchanged()
    {
        var inventory = Inventory(); var original = PayloadSnapshot.Capture(Payload, Token);
        var error = await Assert.ThrowsAsync<IOException>(() => Store().PlanUpdateAsync(Assert.Single(inventory.Skills), inventory,
            Source, Stage, (_, _) => throw new IOException("Source unavailable"), false, Token));
        var retained = Assert.Single(Directory.EnumerateDirectories(State));
        Assert.Contains(retained, error.Message); Assert.Contains("Recorded-base staging failed", error.Message);
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.True(Directory.Exists(Path.Combine(retained, "candidate")));
        Assert.False(File.Exists(Path.Combine(retained, "plan.json")));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("path")]
    [InlineData("commit")]
    public async Task InconsistentResolvedEvidenceRefusesBeforeStageCreation(string kind)
    {
        var inventory = Inventory(); var source = kind switch
        {
            "source" => Source with { Repository = "https://github.com/other/fixture" },
            "path" => Source with { Subpath = "skills/other" },
            _ => Source with { Commit = "main" }
        };
        await Assert.ThrowsAsync<IOException>(() => Store().PlanUpdateAsync(Assert.Single(inventory.Skills), inventory, source, Stage, Base, false, Token));
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public async Task ChangedAuthoredNameRequiresMigrationAndLeavesOriginalUntouched()
    {
        var inventory = Inventory(); var original = PayloadSnapshot.Capture(Payload, Token);
        Task Renamed(NativeStateDirectory directory, CancellationToken token)
        {
            File.WriteAllText(Path.Combine(directory.CanonicalPath, "SKILL.md"), CandidateMetadata.Replace("name: demo", "name: other"));
            return Task.CompletedTask;
        }
        await Assert.ThrowsAsync<IOException>(() => Store().PlanUpdateAsync(Assert.Single(inventory.Skills), inventory, Source, Renamed, Base, false, Token));
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
    }
}
