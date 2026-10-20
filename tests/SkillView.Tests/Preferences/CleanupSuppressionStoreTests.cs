using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Preferences;
using Xunit;

namespace SkillView.Tests.Preferences;

public sealed class CleanupSuppressionStoreTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("skillview-preferences-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => _temp.Delete(true);
    private CleanupSuppressionStore Store(int count = 256) => new(Path.Combine(_temp.FullName, "state"), maxRecords: count);
    private InstalledSkill Skill(string directory = "demo", bool known = false)
    {
        var path = Path.Combine(_temp.FullName, "skills", directory); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), "---\nname: demo\ndescription: Fixture\n" +
            (known ? "metadata:\n  github-repo: https://github.com/fixture/source\n  github-path: skills/demo\n" : "") + "---\nAuthored instructions.\n");
        var (_, metadata, _) = FrontMatterParser.Parse(File.ReadAllText(Path.Combine(path, "SKILL.md")));
        var skill = new InstalledSkill
        {
            Name = "demo",
            ResolvedPath = PathResolver.Resolve(path)!,
            ScanRoot = PathResolver.Resolve(Path.GetDirectoryName(path)!)!,
            Scope = Scope.Custom,
            Agents = [],
            FrontMatter = metadata,
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null
        };
        return skill with { ObservedSubject = SkillIdentityObserver.Capture(skill, DateTimeOffset.UtcNow, true, Token) };
    }
    private static InventorySnapshot Inventory(params InstalledSkill[] skills) => new()
    { Skills = skills.ToImmutableArray(), ScannedRoots = skills.Select(skill => new ScanRoot(skill.ScanRoot, Scope.Custom, null)).Distinct().ToImmutableArray(), UsedGhSkillList = false, CapturedAt = DateTimeOffset.UtcNow };
    private CleanupSuppression Observe(CleanupSuppressionStore store, InstalledSkill skill)
    { long budget = 32 * 1024 * 1024; return CleanupSuppressionStore.Match(skill, store.Read([skill.ScanRoot], Token), Token, ref budget); }

    [Fact]
    public void MissingStateReadCreatesNothing()
    {
        var store = Store(); var read = store.Read([], Token);
        Assert.Empty(read.Records); Assert.Null(read.Diagnostic); Assert.False(Directory.Exists(store.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetRestartAndClearPreserveEveryPayloadObjectAndExtra(bool known)
    {
        var target = Skill(known: known); var peer = Skill("other", known);
        File.WriteAllBytes(Path.Combine(target.ResolvedPath, "extra"), [1, 2, 3]);
        target = target with { ObservedSubject = SkillIdentityObserver.Capture(target, DateTimeOffset.UtcNow, true, Token) };
        var before = PayloadSnapshot.CaptureEvidence(target.ResolvedPath, Token);
        var peerBefore = PayloadSnapshot.CaptureEvidence(peer.ResolvedPath, Token);
        var store = Store(); var inventory = Inventory(target, peer);
        Assert.True(store.Set(target, inventory, false, Token).Changed);
        Assert.True(Observe(Store(), target).External); Assert.False(Observe(store, peer).Active);
        Assert.False(store.Set(target, inventory, false, Token).Changed);
        Assert.Single(Directory.GetFiles(store.Root, "*.json"));
        Assert.True(store.Set(target, inventory, true, Token).Changed);
        Assert.False(store.Set(target, inventory, true, Token).Changed);
        Assert.False(Observe(store, target).Active);
        var after = PayloadSnapshot.CaptureEvidence(target.ResolvedPath, Token);
        Assert.Equal(before.Fingerprint, after.Fingerprint); Assert.Equal(before.Objects, after.Objects); Assert.Equal(before.RootIdentity, after.RootIdentity);
        Assert.Equal(peerBefore.Objects, PayloadSnapshot.CaptureEvidence(peer.ResolvedPath, Token).Objects);
        Assert.False(File.Exists(IgnoreMarker.MarkerPathFor(target.ResolvedPath)));
    }

    [Fact]
    public void UnknownSourceSuppressionExpiresOnInPlaceFileEdit()
    {
        var target = Skill(); var store = Store(); store.Set(target, Inventory(target), false, Token);
        File.AppendAllText(Path.Combine(target.ResolvedPath, "SKILL.md"), "New instructions\n");
        var observation = Observe(store, target);
        Assert.False(observation.External); Assert.Equal("inactive", observation.State);
        Assert.True(store.Set(target, Inventory(target), true, Token).Changed);
    }

    [Fact]
    public void KnownSourceRevisionChangesRetainPreferenceButDifferentOriginDoesNot()
    {
        var target = Skill(known: true); var store = Store(); store.Set(target, Inventory(target), false, Token);
        var revised = target with { FrontMatter = target.FrontMatter with { Version = "2", GithubTreeSha = new string('a', 40), PinnedRef = "v2" } };
        Assert.True(Observe(store, revised).External);
        var switched = target with { FrontMatter = target.FrontMatter with { Upstream = "https://github.com/other/source" } };
        Assert.False(Observe(store, switched).External);
        Assert.Throws<IOException>(() => store.Set(switched, Inventory(switched), false, Token));
    }

    [Fact]
    public void ClearingExternalNeverRemovesLegacyMarker()
    {
        var target = Skill(); IgnoreMarker.Write(target.ResolvedPath); target = target with { Ignored = true, ObservedSubject = SkillIdentityObserver.Capture(target, DateTimeOffset.UtcNow, true, Token) };
        var store = Store(); store.Set(target, Inventory(target), false, Token);
        var result = store.Set(target, Inventory(target), true, Token);
        Assert.True(result.Observation.LegacyMarker); Assert.False(result.Observation.External);
        Assert.Contains("legacy", result.Observation.Diagnostic!); Assert.True(IgnoreMarker.Exists(target.ResolvedPath));
        Assert.True(Observe(store, target).Active);
    }

    [Fact]
    public void CorruptNewestRecordNeverRevivesOlderSuppression()
    {
        var target = Skill(); var store = Store(); store.Set(target, Inventory(target), false, Token);
        store.Set(target, Inventory(target), true, Token);
        File.WriteAllText(Directory.GetFiles(store.Root, "*.json").Order(StringComparer.Ordinal).Last(), "{truncated");
        var read = store.Read([], Token); Assert.NotNull(read.Diagnostic);
        Assert.False(Observe(store, target).Active);
    }

    [Fact]
    public void CapacityReservesAFullClearBeforeAdmittingSet()
    {
        var target = Skill(); var peer = Skill("other"); var inventory = Inventory(target, peer); var store = Store(2);
        store.Set(target, inventory, false, Token);
        Assert.Throws<IOException>(() => store.Set(peer, inventory, false, Token));
        Assert.True(store.Set(target, inventory, true, Token).Changed);
        Assert.False(Observe(store, target).Active); Assert.Equal(2, Directory.GetFiles(store.Root, "*.json").Length);
    }

    [Fact]
    public void PreCanceledAdmissionCreatesNoStateAndPartialPublicationIsReported()
    {
        var target = Skill(); var store = Store(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Set(target, Inventory(target), false, canceled.Token));
        Assert.False(Directory.Exists(store.Root));
        using var midWrite = new CancellationTokenSource(); var mutation = false;
        Assert.Throws<OperationCanceledException>(() => store.Set(target, Inventory(target), false, midWrite.Token,
            () => { mutation = true; midWrite.Cancel(); }));
        Assert.True(mutation); Assert.NotNull(store.Read([], Token).Diagnostic); Assert.False(Observe(store, target).Active);
    }

    [Fact]
    public void ConcurrentWriterProducesDiagnosticAndNeverCreatesASecondRecord()
    {
        var target = Skill(); var store = Store(); store.Set(target, Inventory(target), false, Token);
        using var state = NativeStateDirectory.OpenOrCreate(store.Root, [], Token);
        using var writer = state.OpenExistingFile("writer.lock", true, Token);
        Assert.NotNull(store.Read([], Token).Diagnostic);
        Assert.ThrowsAny<IOException>(() => store.Set(target, Inventory(target), true, Token));
        Assert.Single(Directory.GetFiles(store.Root, "*.json"));
    }
    [Fact]
    public void ClearingEnrichedExternalObservationDoesNotInventALegacyMarker()
    {
        var target = Skill(); var store = Store(); store.Set(target, Inventory(target), false, Token);
        var enriched = target with { Ignored = true, CleanupSuppression = Observe(store, target) };
        var cleared = store.Set(enriched, Inventory(enriched), true, Token);
        Assert.False(cleared.Observation.LegacyMarker); Assert.False(cleared.Observation.Active);
        Assert.Null(cleared.Observation.Diagnostic);
        Assert.False(store.Set(enriched, Inventory(enriched), true, Token).Observation.LegacyMarker);
    }

    [Fact]
    public void FailedUnknownCaptureExhaustsSharedInventoryAllowance()
    {
        var target = Skill(); var store = Store(); store.Set(target, Inventory(target), false, Token);
        long allowance = 1;
        var observation = CleanupSuppressionStore.Match(target, store.Read([], Token), Token, ref allowance);
        Assert.False(observation.External); Assert.Equal("inactive", observation.State); Assert.Equal(0, allowance);
    }

    [Fact]
    public void OwnerRevisionChangesRetainPreferenceButContextChangesExpireIt()
    {
        var target = Skill(known: true) with
        {
            Package = new("fixture/source", "github", null, null, null)
            { Provider = "fixture", ContextRoot = _temp.FullName, Selector = "demo" }
        };
        var store = Store(); store.Set(target, Inventory(target), false, Token);
        Assert.True(Observe(store, target with { Package = target.Package! with { PackageVersion = "2", PinnedRef = "v2" } }).External);
        Assert.False(Observe(store, target with { Package = target.Package! with { ContextRoot = Path.Combine(_temp.FullName, "another") } }).External);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkflowRefusesChangedOwnerOrUnknownNativeObject(bool replaceObject)
    {
        var reviewed = Skill(known: !replaceObject);
        reviewed = reviewed with { ObservedSubject = SkillIdentityObserver.Capture(reviewed, DateTimeOffset.UtcNow, true, Token) };
        InstalledSkill current;
        if (replaceObject)
        {
            Directory.Move(reviewed.ResolvedPath, reviewed.ResolvedPath + "-old");
            current = Skill();
        }
        else current = reviewed with { Package = new("fixture/source", "github", null, null, null) { Provider = "new-owner" } };
        current = current with { ObservedSubject = SkillIdentityObserver.Capture(current, DateTimeOffset.UtcNow, true, Token) };
        var store = Store(); var mutations = 0;
        var result = await CleanupSuppressionWorkflow.SetAsync(
            [new(CleanupClassifier.CandidateKind.Untracked, reviewed.ResolvedPath, "fixture", reviewed)], store,
            _ => Task.FromResult(Inventory(current)), () => mutations++, () => mutations++, Token);
        Assert.Equal(1, result.Failed); Assert.Equal(0, result.Changed); Assert.Equal(0, mutations);
        Assert.False(Directory.Exists(store.Root));
    }

    [Fact]
    public async Task WorkflowRefusesAmbiguousRefreshedPathAndContinuesWithIndependentSelection()
    {
        var reviewed = Skill(known: true);
        var otherContext = reviewed with
        {
            Package = new("fixture/source", "github", null, null, null)
            { Provider = "different-owner", ContextRoot = Path.Combine(_temp.FullName, "other-context") }
        };
        var independent = Skill("independent", known: true);
        var beforeAmbiguous = PayloadSnapshot.CaptureEvidence(reviewed.ResolvedPath, Token);
        var beforeIndependent = PayloadSnapshot.CaptureEvidence(independent.ResolvedPath, Token);
        var store = Store(); var mutations = 0; var changed = 0;
        var result = await CleanupSuppressionWorkflow.SetAsync(
            [new(CleanupClassifier.CandidateKind.Untracked, reviewed.ResolvedPath, "ambiguous fixture", reviewed),
             new(CleanupClassifier.CandidateKind.Untracked, independent.ResolvedPath, "independent fixture", independent)],
            store, _ => Task.FromResult(Inventory(reviewed, otherContext, independent)),
            () => mutations++, () => changed++, Token);

        Assert.Equal(1, result.Failed); Assert.Equal(1, result.Changed); Assert.Equal(0, result.Skipped);
        Assert.False(result.Canceled); Assert.Equal(1, mutations); Assert.Equal(1, changed);
        Assert.Contains("ambiguous", Assert.Single(result.Errors), StringComparison.OrdinalIgnoreCase);
        var record = Assert.Single(store.Read([], Token).Records);
        Assert.True(PathIdentity.Equals(independent.ResolvedPath, record.Binding.Path));
        Assert.False(Observe(store, reviewed).Active); Assert.False(Observe(store, otherContext).Active);
        Assert.True(Observe(store, independent).External);
        Assert.Equal(beforeAmbiguous.Objects, PayloadSnapshot.CaptureEvidence(reviewed.ResolvedPath, Token).Objects);
        Assert.Equal(beforeIndependent.Objects, PayloadSnapshot.CaptureEvidence(independent.ResolvedPath, Token).Objects);
    }

    [Fact]
    public async Task WorkflowReportsUnsupportedRowsAndPartialCanceledMutation()
    {
        var target = Skill(known: true); var store = Store(); using var cancellation = new CancellationTokenSource();
        var mutated = false; var changed = 0;
        var result = await CleanupSuppressionWorkflow.SetAsync(
            [new(CleanupClassifier.CandidateKind.EmptyDirectory, Path.Combine(_temp.FullName, "empty"), "fixture", null),
             new(CleanupClassifier.CandidateKind.Untracked, target.ResolvedPath, "fixture", target)], store,
            _ => Task.FromResult(Inventory(target)), () => { mutated = true; cancellation.Cancel(); }, () => changed++, cancellation.Token);
        Assert.Equal(1, result.Skipped); Assert.True(result.Canceled); Assert.Equal(0, result.Changed);
        Assert.True(mutated); Assert.Equal(0, changed); Assert.NotNull(store.Read([], Token).Diagnostic);
    }

    [Fact]
    public void UnknownObjectReplacedAfterInventoryCannotBeSuppressed()
    {
        var target = Skill(); Directory.Move(target.ResolvedPath, target.ResolvedPath + "-old"); Skill();
        var store = Store();
        Assert.Throws<IOException>(() => store.Set(target, Inventory(target), false, Token));
        Assert.False(Directory.Exists(store.Root));
    }

    [Fact]
    public void OwnerOnlySourceMustMatchLiveMetadataAtReadAndWrite()
    {
        var target = Skill() with
        {
            Package = new("fixture/source", "github", null, null, null)
            { Provider = "fixture", SkillPath = "skills/demo" }
        };
        Assert.NotNull(target.SourceIdentity.Key);
        var store = Store(); store.Set(target, Inventory(target), false, Token);
        Assert.True(Observe(store, target).External);
        File.WriteAllText(Path.Combine(target.ResolvedPath, "SKILL.md"),
            "---\nname: demo\ndescription: Changed source\nmetadata:\n  github-repo: https://github.com/other/source\n  github-path: skills/demo\n---\nNew instructions.\n");
        Assert.False(Observe(store, target).External);
        var freshStore = new CleanupSuppressionStore(Path.Combine(_temp.FullName, "fresh-state"));
        Assert.Throws<IOException>(() => freshStore.Set(target, Inventory(target), false, Token));
        Assert.False(Directory.Exists(freshStore.Root));
    }

    [Fact]
    public void MissingStateHomeIsADiagnosticAndDoesNotSwallowCancellation()
    {
        var runner = new SkillView.Subprocess.ProcessRunner(new SkillView.Logging.Logger(),
            environment: new Dictionary<string, string> { ["HOME"] = "", ["USERPROFILE"] = "" });
        var read = CleanupSuppressionStore.ReadFor(runner, [], Token);
        Assert.NotNull(read.Diagnostic); Assert.Empty(read.Records);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CleanupSuppressionStore.ReadFor(runner, [], cancellation.Token));
    }

    [Fact]
    public void IncompleteOwnerEvidenceCannotSetOrActivateButCanClearSuppression()
    {
        var target = Skill(known: true); var store = Store(); store.Set(target, Inventory(target), false, Token);
        var incomplete = target with { OwnershipDiagnostics = ["Owner registry unreadable"] };
        Assert.False(Observe(store, incomplete).External);
        var freshStore = new CleanupSuppressionStore(Path.Combine(_temp.FullName, "fresh-state"));
        Assert.Throws<IOException>(() => freshStore.Set(incomplete, Inventory(incomplete), false, Token));
        Assert.False(Directory.Exists(freshStore.Root));
        Assert.True(store.Set(incomplete, Inventory(incomplete), true, Token).Changed);
        Assert.False(Observe(store, target).External);
    }

}
