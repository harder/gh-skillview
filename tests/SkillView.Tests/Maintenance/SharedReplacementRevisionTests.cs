using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class SharedReplacementRevisionTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-shared-revision-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Home => Path.Combine(temporary.FullName, "home");
    private string Skills => Path.Combine(Home, ".agents", "skills");
    private string Payload => Path.Combine(Skills, "demo");
    private string AgentRoot => Path.Combine(Home, ".claude", "skills");
    private string Exposure => Path.Combine(AgentRoot, "demo");
    private string Registry => Path.Combine(Home, ".agents", ".skill-lock.json");
    private string State => Path.Combine(temporary.FullName, "state");
    private const string OldTree = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", NewTree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Metadata = "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/harder/fixture\n  github-ref: refs/heads/main\n  github-path: skills/demo\n  github-tree-sha: " + OldTree + "\n---\n";
    private const string OriginalRegistry = "{\n \"version\":3,\n \"extension\":{\"keep\":true},\n \"skills\":{\n \"demo\": {\"source\":\"harder/fixture\",\"sourceType\":\"github\",\"sourceUrl\":\"https://github.com/harder/fixture.git\",\"skillPath\":\"skills/demo\",\"skillFolderHash\":\"" + OldTree + "\",\"installedAt\":\"2020-01-01T00:00:00Z\",\"updatedAt\":\"2020-01-01T00:00:00Z\",\"selectedExtension\":{\"keep\":true}},\n \"other\": {\"source\":\"other/repo\",\"sourceType\":\"github\",\"extension\":[1,2]}\n },\n \"dismissed\":{\"retain\":true}\n}\n";
    private static ResolvedSkillRevision Source => new("https://github.com/harder/fixture", "refs/tags/v2", new('c', 40), NewTree, "skills/demo");
    public SharedReplacementRevisionTests()
    {
        Directory.CreateDirectory(Payload); Directory.CreateDirectory(AgentRoot);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata + "Local instructions\n");
        File.WriteAllText(Path.Combine(Payload, "extra"), "Keep local extra");
        Directory.CreateSymbolicLink(Exposure, Payload);
        File.WriteAllText(Registry, OriginalRegistry);
    }
    public void Dispose() => temporary.Delete(true);
    private ReplacementPlanStore Store(Action<string>? checkpoint = null, string? stateRoot = null) => new(stateRoot ?? State, new Logger(), checkpoint,
        new ProcessRunner(new Logger(), environment: new Dictionary<string, string>
        {
            ["HOME"] = Home,
            ["USERPROFILE"] = Home,
            ["XDG_STATE_HOME"] = Path.Combine(Home, "xdg")
        }, defaultWorkingDirectory: temporary.FullName));
    private InventorySnapshot Inventory()
    {
        ScanRoot[] roots = [new(Skills, Scope.User, null), new(AgentRoot, Scope.User, "claude")];
        var scanned = new LocalSkillScanner(new Logger()).Scan(roots).ToImmutableArray();
        var diagnostics = new List<LockReadDiagnostic>();
        var claims = new SkillLockFileReader(new Logger()).LoadObservations(roots.Select(root => root.Path), Token, new(Home, Path.Combine(Home, "xdg")), diagnostics);
        return InventorySnapshot.Empty with
        {
            Skills = LocalInventoryService.EnrichOwnership(scanned, claims, Token),
            OwnershipRecords = claims.Select(claim => claim.Package).ToImmutableArray(),
            OwnershipReadDiagnostics = diagnostics.ToImmutableArray(),
            Providers = [new("lockfiles", diagnostics.Count == 0, roots.Select(root => root.Path).ToImmutableArray(), DateTimeOffset.UtcNow)],
            ConfiguredRoots = roots.ToImmutableArray(),
            ScannedRoots = roots.ToImmutableArray()
        };
    }
    private static Task Stage(NativeStateDirectory directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        File.WriteAllText(Path.Combine(directory.CanonicalPath, "SKILL.md"), Metadata.Replace(OldTree, NewTree).Replace("refs/heads/main", "refs/tags/v2") + "New source\n");
        return Task.CompletedTask;
    }
    private static Task Base(NativeStateDirectory directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        File.WriteAllText(Path.Combine(directory.CanonicalPath, "SKILL.md"), Metadata + "Original source\n");
        return Task.CompletedTask;
    }
    private Task<ReplacementResult> Plan(ReplacementPlanStore? store = null)
    {
        var inventory = Inventory();
        return (store ?? Store()).PlanUpdateAsync(Assert.Single(inventory.Skills), inventory, Source, Stage, Base, false, Token);
    }
    private byte[] Selected() => GhRegistryEditor.CaptureEntry(File.ReadAllBytes(Registry), "demo", Token).Bytes;

    private SavedUpdateCoordinator Coordinator(ReplacementPlanStore store)
    {
        var logger = new Logger(); var runner = new ProcessRunner(logger);
        var catalog = new SkillView.Gh.GhSkillListAdapter(runner, logger);
        var availability = new SkillUpdateCoordinator(new(runner, logger), new(runner, logger), catalog, logger, runner);
        return new(store, availability, catalog, logger);
    }

    [Fact]
    public async Task ProductApplyCancellationAfterMoveStillReconcilesWithIndependentTokenAndRetainsPlan()
    {
        var plan = await Plan(); var reconciled = 0;
        using var cancellation = new SkillView.Threading.CancellationSource(Token, _ => { });
        var store = Store(step => { if (step == "retain-moved") cancellation.TryCancel(); });
        var result = await Coordinator(store).ApplyAsync(plan, "retain-original", Inventory(), token =>
        {
            Assert.False(token.IsCancellationRequested); reconciled++;
            return Task.FromResult(Inventory());
        }, cancellation.Token);
        Assert.True(result.MutationAttempted); Assert.True(result.Cancelled);
        Assert.Equal(130, result.ExitCode); Assert.Equal(1, reconciled);
        Assert.Equal(plan.Id, result.SavedPlan!.Id);
        Assert.Equal("needs-recovery", result.SavedPlan.Status);
        Assert.Equal("recovery-required", Assert.Single(result.Outcomes).Status);
        Assert.Contains(plan.Id, Assert.Single(result.Outcomes).Message);
        Assert.Contains(plan.PlanPath, Assert.Single(result.Outcomes).Message);
        Assert.True(Directory.Exists(plan.RetainedPath)); Assert.False(Directory.Exists(Payload));
        Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductApplyRequiresApprovalAndRejectsPreCanceledAdmission(bool canceled)
    {
        var plan = await Plan(); var before = PayloadSnapshot.Capture(Payload, Token);
        var reconciled = 0;
        var result = await Coordinator(Store()).ApplyAsync(plan, canceled ? "retain-original" : "force", Inventory(), _ =>
        { reconciled++; return Task.FromResult(Inventory()); }, canceled ? new CancellationToken(true) : Token);
        Assert.False(result.Succeeded); Assert.False(result.MutationAttempted); Assert.Equal(0, reconciled);
        Assert.Equal(canceled ? 130 : 2, result.ExitCode);
        Assert.Equal(before.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task ProductApplyReconciliationFailurePreservesVerifiedApplyAndRecoveryEvidence()
    {
        var plan = await Plan();
        var result = await Coordinator(Store()).ApplyAsync(plan, "retain-original", Inventory(),
            _ => throw new IOException("Provider failed after apply"), Token);
        Assert.False(result.Succeeded); Assert.True(result.ReconciliationPartial);
        Assert.True(result.MutationAttempted); Assert.Equal(2, result.ExitCode);
        Assert.Equal("installed", result.SavedPlan!.Status); Assert.Equal(plan.Id, result.SavedPlan.Id);
        Assert.Equal("changed", Assert.Single(result.Outcomes).Status);
        Assert.Contains("Provider failed", Assert.Single(result.Notices).Text);
        Assert.Contains("Provider failed", result.ErrorMessage);
        Assert.Equal(NewTree, Assert.Single(Inventory().Skills).TreeSha);
    }

    [Fact]
    public async Task ProductApplyMalformedSavedPlanReturnsRefusalWithRecoverableReference()
    {
        var plan = await Plan(); var before = PayloadSnapshot.Capture(Payload, Token); var reconciled = 0;
        File.WriteAllText(plan.PlanPath, "{malformed");
        var result = await Coordinator(Store()).ApplyAsync(plan, "retain-original", Inventory(), _ =>
        { reconciled++; return Task.FromResult(Inventory()); }, Token);
        Assert.False(result.Succeeded); Assert.Equal(2, result.ExitCode); Assert.Equal(1, reconciled);
        Assert.Equal(plan.Id, result.SavedPlan!.Id);
        Assert.Contains(plan.PlanPath, Assert.Single(result.Outcomes).Message);
        Assert.Equal("refused", Assert.Single(result.Outcomes).Status);
        Assert.Equal(before.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }

    [Fact]
    public async Task ProductPreparationPreservesPinsAndUntrackedOutcomesWithoutCreatingPlans()
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        var pinned = target with { ResolvedPath = Path.Combine(Skills, "pinned"), FrontMatter = target.FrontMatter with { Pinned = true } };
        var local = target with { FrontMatter = SkillFrontMatter.Empty };
        var result = await Coordinator(Store()).PrepareAsync("unused-gh",
            new(Targets: [pinned, local], Force: true), inventory, Token);
        Assert.Equal(new[] { "pinned", "untracked" }, result.Outcomes.Select(outcome => outcome.Status));
        Assert.Null(result.SavedPlan); Assert.False(result.MutationAttempted);
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public async Task ProductPreparationRefusesIncompleteInventoryBeforePrivateStateOrNetwork()
    {
        var inventory = Inventory();
        var result = await Coordinator(Store()).PrepareAsync("unused-gh",
            new(Targets: inventory.Skills), inventory with { ConfiguredRootsComplete = false }, Token);
        Assert.False(result.Succeeded); Assert.Null(result.SavedPlan);
        Assert.False(Directory.Exists(State)); Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
    }

    [Fact]
    public async Task SharedApplyAndUndoPreserveExtensionsFreshUnrelatedRecordsAndExposure()
    {
        var original = PayloadSnapshot.Capture(Payload, Token);
        var beforeEntry = Selected();
        var plan = await Plan();
        Assert.NotNull(plan.Revision!.Owner); Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
        Assert.Equal(2, plan.Subject.Agents.Length);
        File.WriteAllText(Registry, File.ReadAllText(Registry).Replace("[1,2]", "[1,2,3]"));
        var locked = new List<string>();
        var apply = Store(step =>
        {
            if (step is not ("retain-moved" or "install-moved" or "owner-written")) return;
            Assert.Throws<IOException>(() => NativeRegistryFileLease.Open(Registry, Token)); locked.Add(step);
        }).Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.True(apply.Succeeded, apply.Message); Assert.Equal("desired", apply.Owner!.State);
        Assert.Equal(new[] { "retain-moved", "install-moved", "owner-written" }, locked);
        Assert.True(PathResolver.IsSymlink(Exposure)); Assert.Equal(NewTree, Assert.Single(Inventory().Skills).TreeSha);
        using (var after = JsonDocument.Parse(Selected()))
        {
            Assert.Equal(NewTree, after.RootElement.GetProperty("skillFolderHash").GetString());
            Assert.Equal("2020-01-01T00:00:00Z", after.RootElement.GetProperty("installedAt").GetString());
            Assert.True(after.RootElement.GetProperty("selectedExtension").GetProperty("keep").GetBoolean());
        }
        File.WriteAllText(Registry, File.ReadAllText(Registry).Replace("[1,2,3]", "[1,2,3,4]"));
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        var undo = Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(undo.Succeeded, undo.Message); Assert.Equal("before", undo.Owner!.State);
        Assert.Equal(beforeEntry, Selected());
        Assert.Equal(OriginalRegistry.Replace("[1,2]", "[1,2,3,4]"), File.ReadAllText(Registry));
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.True(PathResolver.IsSymlink(Exposure));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Payload, "extra")), File.ReadAllBytes(Path.Combine(Exposure, "extra")));
    }

    [Theory]
    [InlineData("retain-moved", "original-retained", "before")]
    [InlineData("install-moved", "installed", "before")]
    [InlineData("owner-written", "installed", "desired")]
    public async Task FreshOwnerContextRecoversEveryForwardPhase(string stop, string topology, string ownerState)
    {
        var plan = await Plan();
        var interrupted = Store(step => { if (step == stop) throw new IOException("Interrupted"); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.False(interrupted.Succeeded); Assert.True(interrupted.MutationObserved);
        var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Equal(topology, inspect.Status); Assert.Equal(ownerState, inspect.Owner!.State);
        var recovered = Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), false, Token);
        Assert.True(recovered.Succeeded, recovered.Message); Assert.Equal("desired", recovered.Owner!.State);
    }

    [Theory]
    [InlineData("displace-moved", "replacement-retained", "desired")]
    [InlineData("restore-moved", "restored", "desired")]
    [InlineData("owner-written", "restored", "before")]
    public async Task FreshOwnerContextCompletesEveryUndoPhase(string stop, string topology, string ownerState)
    {
        var plan = await Plan(); var original = PayloadSnapshot.Capture(Payload, Token);
        Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        var interrupted = Store(step => { if (step == stop) throw new IOException("Interrupted"); })
            .Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), true, Token);
        Assert.False(interrupted.Succeeded);
        inspect = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Equal(topology, inspect.Status); Assert.Equal(ownerState, inspect.Owner!.State);
        var recovered = Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(recovered.Succeeded, recovered.Message); Assert.Equal("before", recovered.Owner!.State);
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
    }

    [Theory]
    [InlineData("registry-truncated")]
    [InlineData("registry-chunk-written")]
    [InlineData("registry-flushed")]
    public async Task FailedOwnerWriteRestoresPreimageAndRecoveryFinishesSavedUpdate(string stop)
    {
        var plan = await Plan(); var before = File.ReadAllBytes(Registry);
        var result = Store(step => { if (step == stop) throw new IOException("Injected failure"); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.False(result.Succeeded); Assert.True(result.MutationObserved); Assert.Equal(before, File.ReadAllBytes(Registry));
        var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Equal("installed", inspect.Status); Assert.Equal("before", inspect.Owner!.State);
        var recovered = Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), false, Token);
        Assert.True(recovered.Succeeded, recovered.Message); Assert.True(recovered.MutationObserved);
    }

    [Fact]
    public async Task UnrelatedBytesChangingAfterRecoveryReviewRequireNewInspection()
    {
        var plan = await Plan();
        Assert.False(Store(step => { if (step == "install-moved") throw new IOException("Interrupted"); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        File.WriteAllText(Registry, File.ReadAllText(Registry).Replace("[1,2]", "[1,2,3]"));
        Assert.Throws<IOException>(() => Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), false, Token));
        Assert.Contains("[1,2,3]", File.ReadAllText(Registry));
    }

    [Fact]
    public async Task ForeignSelectedEntryRefusesBeforeOriginalDisplacement()
    {
        var plan = await Plan(); var original = PayloadSnapshot.Capture(Payload, Token);
        File.WriteAllText(Registry, File.ReadAllText(Registry).Replace("\"selectedExtension\":{\"keep\":true}", "\"selectedExtension\":{\"keep\":false}"));
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistryParentGenerationChangeRefusesApplyOrTornRepairWithoutPayloadMutation(bool torn)
    {
        var plan = torn ? await TornPlan() : await Plan();
        var original = PayloadSnapshot.Capture(Payload, Token);
        var bytes = File.ReadAllBytes(Registry);
        using (var registry = NativeRegistryFileLease.Open(Registry, Token))
            Assert.Equal(plan.Revision!.Owner!.ParentIdentity, registry.ParentIdentity);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Registry)!, "other-entry"), "Namespace change");
        if (torn) Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        else Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Equal(bytes, File.ReadAllBytes(Registry));
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(original.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
        if (!torn) Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task RegistryParentGenerationChangeDuringStagingRefusesPlanPublication()
    {
        var inventory = Inventory(); var original = PayloadSnapshot.Capture(Payload, Token);
        await Assert.ThrowsAsync<IOException>(() => Store().PlanUpdateAsync(Assert.Single(inventory.Skills), inventory, Source,
            async (directory, token) =>
            {
                await Stage(directory, token);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Registry)!, "other-entry"), "Namespace change");
            }, Base, false, Token));
        Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(original.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
        Assert.Empty(Directory.EnumerateFiles(State, "plan.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FirstSharedPlanWithStateUnderRegistryParentInitializesBeforeCapturingWitness()
    {
        var nestedState = Path.Combine(Path.GetDirectoryName(Registry)!, "SkillView", "replacements");
        Assert.False(Directory.Exists(Path.GetDirectoryName(nestedState)));
        var store = Store(stateRoot: nestedState);
        var plan = await Plan(store);
        Assert.True(store.Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var inspected = store.Inspect(plan.Id, Inventory(), Token);
        var undo = store.Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(undo.Succeeded, undo.Message);
        Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
    }

    [Fact]
    public async Task MalformedForwardIntentsCannotConsumeReservedUndoAllowance()
    {
        var plan = await Plan(); var original = PayloadSnapshot.Capture(Payload, Token);
        Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var journal = Path.Combine(State, plan.Id, "owner-journal");
        using (var authority = NativeStateDirectory.OpenOrCreate(journal, [], Token))
        using (var writer = authority.OpenExistingFile("writer.lock", true, Token))
        {
            for (var count = 0; count < 4; count++)
            {
                using var file = authority.OpenFile($"registry-{Guid.NewGuid():N}-intent.json", false, Token);
                file.WriteByte((byte)'{'); file.Flush(true);
            }
            authority.FlushPublication(Token);
        }
        var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        var undone = Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(Encoding.UTF8.GetBytes(OriginalRegistry), File.ReadAllBytes(Registry));
    }

    private sealed class SimulatedProcessDeath : Exception;

    private async Task<ReplacementResult> TornPlan(bool undo = false, bool compensation = false)
    {
        var plan = await Plan();
        if (undo) Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var store = Store(step =>
        {
            if (compensation)
            {
                if (step == "registry-truncated") throw new IOException("Live write failed");
                if (step.EndsWith("-restore-intent.json", StringComparison.Ordinal)) throw new SimulatedProcessDeath();
            }
            else if (step == "registry-truncated") throw new SimulatedProcessDeath();
        });
        if (undo)
        {
            var inspected = Store().Inspect(plan.Id, Inventory(), Token);
            Assert.Throws<SimulatedProcessDeath>(() => store.Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token));
        }
        else Assert.Throws<SimulatedProcessDeath>(() => store.Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Empty(File.ReadAllBytes(Registry));
        return plan;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TornForwardUndoAndCompensationRequireExplicitReviewedImageRepair(bool undo, bool compensation)
    {
        var plan = await TornPlan(undo, compensation);
        var originalSlot = PayloadSnapshot.Capture(Payload, Token);
        var inventory = Inventory(); Assert.True(inventory.IsPartial);
        var inspect = Store().Inspect(plan.Id, inventory, Token);
        Assert.Equal("torn", inspect.Owner!.State);
        Assert.Equal(undo ? "undo" : "forward", inspect.Owner.Repair!.Direction);
        Assert.Equal(undo ? "desired" : "before", inspect.Owner.Repair.EntryState);
        Assert.Throws<IOException>(() => Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), undo, Token));
        Assert.Empty(File.ReadAllBytes(Registry));
        var result = Store().RepairOwner(plan.Id, inspect.ReviewFingerprint, Inventory(), Token);
        Assert.True(result.Succeeded, result.Message); Assert.Equal("owner-repaired", result.Status); Assert.True(result.MutationObserved);
        Assert.Equal(inspect.Owner.Repair.ImageHash, NativeRegistryFileLease.Hash(File.ReadAllBytes(Registry)));
        Assert.Equal(originalSlot.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(originalSlot.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
        var reviewed = Store().Inspect(plan.Id, Inventory(), Token);
        var recovered = Store().Recover(plan.Id, reviewed.ReviewFingerprint, Inventory(), undo, Token);
        Assert.True(recovered.Succeeded, recovered.Message);
        Assert.Equal(undo ? "before" : "desired", recovered.Owner!.State);
    }

    [Fact]
    public async Task InterruptedExplicitRepairRetainsTheSameReviewedImageAndCanBeReviewedAgain()
    {
        var plan = await TornPlan();
        var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Throws<SimulatedProcessDeath>(() => Store(step => { if (step == "registry-truncated") throw new SimulatedProcessDeath(); })
            .RepairOwner(plan.Id, inspect.ReviewFingerprint, Inventory(), Token));
        var reviewed = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.NotEqual(inspect.Owner!.Repair!.WriteId, reviewed.Owner!.Repair!.WriteId);
        Assert.Equal(inspect.Owner.Repair.ImageHash, reviewed.Owner.Repair.ImageHash);
        Assert.NotEqual(inspect.ReviewFingerprint, reviewed.ReviewFingerprint);
        Assert.True(Store().RepairOwner(plan.Id, reviewed.ReviewFingerprint, Inventory(), Token).Succeeded);
        Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairMainIntentWithoutCompleteRawIntentPreservesItsExactUnchangedParentReview(bool partialRawIntent)
    {
        var plan = await TornPlan(); var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Throws<SimulatedProcessDeath>(() => Store(step => { if (step == "owner-repair-intent") throw new SimulatedProcessDeath(); })
            .RepairOwner(plan.Id, inspect.ReviewFingerprint, Inventory(), Token));
        if (partialRawIntent)
        {
            var operation = Path.Combine(State, plan.Id);
            var main = Directory.EnumerateFiles(operation, "step-*.json").Order(StringComparer.Ordinal).Last();
            var step = JsonSerializer.Deserialize(File.ReadAllBytes(main), ReplacementJsonContext.Default.ReplacementStep)!;
            using var journal = NativeStateDirectory.OpenOrCreate(Path.Combine(operation, "owner-journal"), [], Token);
            using var writer = journal.OpenExistingFile("writer.lock", true, Token);
            using var file = journal.OpenFile($"registry-{step.OwnerWriteId}-intent.json", false, Token);
            file.WriteByte((byte)'{'); file.Flush(true); journal.FlushPublication(Token);
        }
        var reviewed = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Equal(inspect.Owner!.Repair, reviewed.Owner!.Repair);
        Assert.True(Store().RepairOwner(plan.Id, reviewed.ReviewFingerprint, Inventory(), Token).Succeeded);
        Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialMainStepRetainsPriorAuthorityOnlyWithoutAnUnboundAdmittedWrite(bool unboundWrite)
    {
        var plan = await TornPlan(); var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        var operation = Path.Combine(State, plan.Id);
        var next = Directory.EnumerateFiles(operation, "step-*.json").Count();
        using (var directory = NativeStateDirectory.OpenOrCreate(operation, [], Token))
        using (var writer = directory.OpenExistingFile("writer.lock", true, Token))
        {
            using var file = directory.OpenFile($"step-{next:D4}.json", false, Token);
            file.WriteByte((byte)'{'); file.Flush(true); directory.FlushPublication(Token);
        }
        if (unboundWrite)
        {
            using var journal = NativeStateDirectory.OpenOrCreate(Path.Combine(operation, "owner-journal"), [], Token);
            using var writer = journal.OpenExistingFile("writer.lock", true, Token);
            using var file = journal.OpenFile($"registry-{Guid.NewGuid():N}-complete.json", false, Token);
            file.WriteByte((byte)'{'); file.Flush(true); journal.FlushPublication(Token);
            Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        }
        else
        {
            var reviewed = Store().Inspect(plan.Id, Inventory(), Token);
            Assert.Equal(inspect.Owner!.Repair, reviewed.Owner!.Repair);
            Assert.True(Store().RepairOwner(plan.Id, reviewed.ReviewFingerprint, Inventory(), Token).Succeeded);
            Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
        }
    }

    [Fact]
    public async Task RepairRefusesStaleReviewAndPreservesForeignValidOrUnattributedMalformedBytes()
    {
        var plan = await TornPlan(); var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        var foreign = Encoding.UTF8.GetBytes(OriginalRegistry.Replace("[1,2]", "[1,2,3]"));
        File.WriteAllBytes(Registry, foreign);
        Assert.Throws<IOException>(() => Store().RepairOwner(plan.Id, inspect.ReviewFingerprint, Inventory(), Token));
        Assert.Equal(foreign, File.ReadAllBytes(Registry));
        File.WriteAllText(Registry, "foreign {");
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.Equal("foreign {", File.ReadAllText(Registry));
    }

    [Fact]
    public async Task CompletedAncestorCannotAuthorizeRepairOfAChildAttempt()
    {
        var plan = await TornPlan(); var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Throws<SimulatedProcessDeath>(() => Store(step => { if (step == "registry-truncated") throw new SimulatedProcessDeath(); })
            .RepairOwner(plan.Id, inspect.ReviewFingerprint, Inventory(), Token));
        var journal = Path.Combine(State, plan.Id, "owner-journal");
        using (var authority = NativeStateDirectory.OpenOrCreate(journal, [], Token))
        using (var writer = authority.OpenExistingFile("writer.lock", true, Token))
        {
            using var file = authority.OpenFile($"registry-{inspect.Owner!.Repair!.WriteId}-complete.json", false, Token);
            file.WriteByte((byte)'{'); file.Flush(true); authority.FlushPublication(Token);
        }
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.Empty(File.ReadAllBytes(Registry));
    }

    [Fact]
    public async Task TornRegistryExceptionDoesNotHideOtherProviderOrOwnerFailures()
    {
        var plan = await TornPlan(); var inventory = Inventory();
        var unrelated = inventory with { Providers = inventory.Providers.Add(new("gh", false, [AgentRoot], DateTimeOffset.UtcNow, "Network failure")) };
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, unrelated, Token));
        File.WriteAllText(Path.Combine(Home, "skills-lock.json"), "broken");
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.Empty(File.ReadAllBytes(Registry));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":4,\"skills\":{}}")]
    [InlineData("{\"version\":3,\"version\":3,\"skills\":{}}")]
    public async Task ValidButIncompatibleRegistryNeverUsesWholeImageRepair(string foreign)
    {
        var plan = await TornPlan(); var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        File.WriteAllText(Registry, foreign);
        Assert.Throws<IOException>(() => Store().RepairOwner(plan.Id, inspect.ReviewFingerprint, Inventory(), Token));
        Assert.Equal(foreign, File.ReadAllText(Registry));
    }
}
