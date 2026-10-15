using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class ReplacementPlanStoreTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-replacement-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Skills => Path.Combine(temporary.FullName, "skills");
    private string Payload => Path.Combine(Skills, "demo");
    private string State => Path.Combine(temporary.FullName, "state");
    private const string Metadata = "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/harder/fixture\n  github-ref: refs/heads/main\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n---\n";
    private ReplacementPlanStore Store(Action<string>? checkpoint = null, Action<OperationImpact>? subjectObserved = null) => new(State, new Logger(), checkpoint,
        new ProcessRunner(new Logger(), environment: new Dictionary<string, string>
        {
            ["HOME"] = temporary.FullName,
            ["USERPROFILE"] = temporary.FullName,
            ["XDG_STATE_HOME"] = Path.Combine(temporary.FullName, "xdg-state")
        }, defaultWorkingDirectory: temporary.FullName), subjectObserved: subjectObserved);
    public ReplacementPlanStoreTests()
    {
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata + "Local instructions\n");
        File.WriteAllText(Path.Combine(Payload, "extra"), "User extra");
        File.WriteAllText(Path.Combine(Payload, ".skillview-ignore"), "Keep this exclusion on undo");
        Directory.CreateDirectory(Path.Combine(Payload, "empty"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Payload, (UnixFileMode)Convert.ToInt32("700", 8));
    }
    public void Dispose() => temporary.Delete(true);
    private InventorySnapshot Inventory(params ScanRoot[] additional)
    {
        ScanRoot[] roots = [new(Skills, Scope.Custom, null), .. additional];
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
        var destination = directory.CanonicalPath;
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "SKILL.md"), Metadata + "Approved instructions\n");
        File.WriteAllText(Path.Combine(destination, "script.sh"), "echo approved\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(destination, "script.sh"), (UnixFileMode)Convert.ToInt32("755", 8));
        }
        return Task.CompletedTask;
    }
    private async Task<ReplacementResult> Plan(ReplacementPlanStore? store = null)
    {
        var inventory = Inventory();
        return await (store ?? Store()).PlanAsync(Assert.Single(inventory.Skills), inventory, Stage, Token);
    }
    private ReplacementResult Apply(ReplacementResult plan)
    {
        var result = Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.True(result.Succeeded, result.Message); Assert.Equal("installed", result.Status); return result;
    }

    [Fact]
    public async Task TransientIdentityProjectionsDoNotAlterSavedSubjectOrResultSerialization()
    {
        var plan = await Plan();
        Assert.NotNull(plan.Impact);
        Assert.Equal(JsonSerializer.Serialize(plan with { Impact = null }, ReplacementJsonContext.Default.ReplacementResult),
            JsonSerializer.Serialize(plan, ReplacementJsonContext.Default.ReplacementResult));
        var owner = new SkillPackage("fixture/plugin", "unknown", null, null, null) { Provider = "codex-plugin" };
        var recorded = plan.Subject with { Owners = [owner] };
        var observed = recorded with
        {
            Owners = [owner with
            { ManagedRegion = new(Payload, "codex-plugin", "codex", "plugin", "fixture/plugin", "1", "extra-live-context") }]
        };
        Assert.Equal(JsonSerializer.Serialize(recorded, ReplacementJsonContext.Default.ReplacementSubject),
            JsonSerializer.Serialize(observed, ReplacementJsonContext.Default.ReplacementSubject));
        var bytes = File.ReadAllBytes(plan.PlanPath);
        Assert.DoesNotContain("\"impact\"", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("managedRegion", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task SavedSubjectSurvivesRefusalAndVacantOriginalAddress()
    {
        var plan = await Plan();
        OperationImpact? frozen = null;
        var store = Store(subjectObserved: impact => frozen = impact);
        Assert.Throws<IOException>(() => store.Apply(plan.Id, new string('0', 64), Inventory(), Token));
        Assert.NotNull(frozen);
        Assert.Equal(plan.Subject.InstallationId, frozen.Subject.InstallationId);
        Assert.Equal("saved-original-evidence", frozen.Subject.NativeObject.Role);
        Assert.False(frozen.ExposureCoverageComplete);
        var before = frozen.Subject.NativeObject;
        Directory.Move(Payload, Payload + "-elsewhere");
        frozen = null;
        Assert.Throws<IOException>(() => store.Inspect(plan.Id, Inventory(), Token));
        Assert.NotNull(frozen);
        Assert.Equal(before, frozen.Subject.NativeObject);
        Assert.Equal(plan.OriginalPath, frozen.Subject.Path);
        Assert.False(Directory.Exists(Payload));
    }

    [Fact]
    public async Task SavedPlanPromotesExactStageAndUndoRetainsBothNativeObjects()
    {
        var before = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Plan();
        Assert.Equal("planned", plan.Status); Assert.False(plan.MutationObserved);
        Assert.Equal(plan.OriginalPath, plan.Impact!.RetainedPayloadPath);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Contains("extra", plan.ChangedPaths); Assert.Contains(".skillview-ignore", plan.ChangedPaths);
        if (!OperatingSystem.IsWindows()) Assert.Contains("/", plan.ChangedPaths);
        var staged = PayloadSnapshot.Capture(plan.CandidatePath, Token);
        var applied = Apply(plan);
        Assert.Equal(applied.RetainedPath, applied.Impact!.RetainedPayloadPath);
        var installed = PayloadSnapshot.Capture(Payload, Token);
        Assert.Equal(staged.Fingerprint, installed.Fingerprint);
        Assert.True(NativeDirectoryMoveLease.SameObject(staged.RootIdentity!.Value, installed.RootIdentity!.Value));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(applied.RetainedPath, Token).Fingerprint);
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        var restored = Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token);
        Assert.Equal(restored.OriginalPath, restored.Impact!.RetainedPayloadPath);
        Assert.True(restored.Succeeded, restored.Message); Assert.Equal("restored", restored.Status);
        var final = PayloadSnapshot.Capture(Payload, Token);
        Assert.Equal(before.Fingerprint, final.Fingerprint); Assert.Equal(before.RootUnixMode, final.RootUnixMode);
        Assert.True(NativeDirectoryMoveLease.SameObject(before.RootIdentity!.Value, final.RootIdentity!.Value));
        Assert.Equal(staged.Fingerprint, PayloadSnapshot.Capture(restored.DisplacedPath, Token).Fingerprint);
        var again = Store().Inspect(plan.Id, Inventory(), Token);
        var noop = Store().Recover(plan.Id, again.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(noop.Succeeded); Assert.False(noop.MutationObserved);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("tree")]
    [InlineData("ref")]
    [InlineData("pin")]
    public async Task TrackingChangedAfterInventoryRefusesPlanningBeforeStaging(string field)
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        var file = Path.Combine(Payload, "SKILL.md");
        var text = File.ReadAllText(file);
        text = field switch
        {
            "source" => text.Replace("https://github.com/harder/fixture", "https://github.com/harder/other"),
            "tree" => text.Replace(new string('a', 40), new string('b', 40)),
            "ref" => text.Replace("refs/heads/main", "refs/heads/other"),
            _ => text.Replace("  github-ref:", "  github-pinned: new-pin\n  github-ref:")
        };
        File.WriteAllText(file, text);
        var stageCalled = false;
        await Assert.ThrowsAsync<IOException>(() => Store().PlanAsync(target, inventory, (directory, ct) =>
        { stageCalled = true; return Stage(directory, ct); }, Token));
        Assert.False(stageCalled); Assert.False(Directory.Exists(State));
        Assert.Equal(text, File.ReadAllText(file));
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("retain-moved", false)]
    [InlineData("displace-moved", false)]
    [InlineData("none", true)]
    [InlineData("retain-moved", true)]
    [InlineData("displace-moved", true)]
    public async Task SingletonNestedNamespaceSupportsApplyRestartRecoveryAndUndo(string checkpoint, bool readme)
    {
        var parent = Directory.CreateDirectory(Path.Combine(Skills, "publisher", "collection")).FullName;
        var payload = Path.Combine(parent, "demo"); Directory.Move(Payload, payload);
        if (readme) File.WriteAllText(Path.Combine(parent, "README.md"), "Namespace documentation");
        var inventory = Inventory(); var before = PayloadSnapshot.Capture(payload, Token);
        var plan = await Store().PlanAsync(Assert.Single(inventory.Skills), inventory, Stage, Token);
        var interrupted = Store(name => { if (name == checkpoint) throw new IOException("Interrupted"); });
        var applied = (checkpoint == "retain-moved" ? interrupted : Store())
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        if (checkpoint == "retain-moved") Assert.False(applied.Succeeded);
        else Assert.True(applied.Succeeded, applied.Message);
        if (checkpoint == "displace-moved")
        {
            var inspected = Store().Inspect(plan.Id, Inventory(), Token);
            Assert.False(interrupted.Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token).Succeeded);
        }
        var current = Store().Inspect(plan.Id, Inventory(), Token);
        if (checkpoint == "retain-moved")
        {
            Assert.Equal("original-retained", current.Status);
            Assert.True(Store().Recover(plan.Id, current.ReviewFingerprint, Inventory(), false, Token).Succeeded);
            current = Store().Inspect(plan.Id, Inventory(), Token);
        }
        else Assert.Equal(checkpoint == "none" ? "installed" : "replacement-retained", current.Status);
        var restored = Store().Recover(plan.Id, current.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(restored.Succeeded, restored.Message);
        var final = PayloadSnapshot.Capture(payload, Token);
        Assert.Equal(before.Fingerprint, final.Fingerprint);
        Assert.True(NativeDirectoryMoveLease.SameObject(before.RootIdentity!.Value, final.RootIdentity!.Value));
        if (readme) Assert.Equal("Namespace documentation", File.ReadAllText(Path.Combine(parent, "README.md")));
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("owner")]
    public async Task NewlyManagedNamespaceAfterInterruptionRemainsARefusal(string kind)
    {
        var parent = Directory.CreateDirectory(Path.Combine(Skills, "publisher")).FullName;
        var payload = Path.Combine(parent, "demo"); Directory.Move(Payload, payload);
        var inventory = Inventory();
        var plan = await Store().PlanAsync(Assert.Single(inventory.Skills), inventory, Stage, Token);
        var interrupted = Store(name => { if (name == "retain-moved") throw new IOException("Interrupted"); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.False(interrupted.Succeeded);
        // A .skill-lock.json beside the physical installation root is the
        // reader's package-ownership convention, including namespace roots.
        var file = kind == "skill" ? Path.Combine(parent, "SKILL.md") : Path.Combine(Skills, ".skill-lock.json");
        var content = kind == "skill" ? Metadata.Replace("name: demo", "name: publisher") : "{\"version\":999,\"skills\":{}}";
        File.WriteAllText(file, content);
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.True(Directory.Exists(plan.RetainedPath)); Assert.True(Directory.Exists(plan.CandidatePath));
        Assert.Equal(content, File.ReadAllText(file));
    }

    [Theory]
    [InlineData("retain-moved", "original-retained")]
    [InlineData("step-0001.json", "original-retained")]
    [InlineData("install-moved", "installed")]
    [InlineData("step-0003.json", "installed")]
    public async Task FreshProcessCanReviewAndResumeEveryInterruptedForwardMove(string checkpoint, string topology)
    {
        var plan = await Plan();
        var interrupted = Store(name => { if (name == checkpoint) throw new IOException("Simulated abrupt exit"); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.False(interrupted.Succeeded); Assert.True(interrupted.MutationObserved);
        var inspected = Store().Inspect(plan.Id, Inventory(), Token); Assert.Equal(topology, inspected.Status);
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        var recovered = Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), false, Token);
        Assert.True(recovered.Succeeded, recovered.Message); Assert.Equal("installed", recovered.Status);
        Assert.Equal("Approved instructions\n", File.ReadAllText(Path.Combine(Payload, "SKILL.md"))[Metadata.Length..]);
        Assert.True(File.Exists(Path.Combine(recovered.RetainedPath, "extra")));
    }

    [Theory]
    [InlineData("displace-moved", "replacement-retained")]
    [InlineData("step-0005.json", "replacement-retained")]
    [InlineData("restore-moved", "restored")]
    public async Task FreshProcessCanReviewAndResumeInterruptedUndo(string checkpoint, string topology)
    {
        var plan = await Plan(); Apply(plan);
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        var interrupted = Store(name => { if (name == checkpoint) throw new IOException("Simulated abrupt exit"); })
            .Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token);
        Assert.False(interrupted.Succeeded); Assert.True(interrupted.MutationObserved);
        var current = Store().Inspect(plan.Id, Inventory(), Token); Assert.Equal(topology, current.Status);
        var recovered = Store().Recover(plan.Id, current.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(recovered.Succeeded, recovered.Message);
        Assert.True(File.Exists(Path.Combine(Payload, "extra"))); Assert.True(Directory.Exists(recovered.DisplacedPath));
    }

    [Theory]
    [InlineData("SKILL.md")]
    [InlineData("extra")]
    [InlineData("delete")]
    [InlineData("same-bytes-new-object")]
    public async Task InstalledChangesAfterPlanRefuseApplyWithoutMovingAnything(string change)
    {
        var plan = await Plan();
        if (change == "delete") File.Delete(Path.Combine(Payload, "extra"));
        else if (change == "same-bytes-new-object")
        {
            var file = Path.Combine(Payload, "extra"); File.Move(file, file + ".old");
            File.WriteAllText(file, "User extra"); File.Delete(file + ".old");
        }
        else File.AppendAllText(Path.Combine(Payload, change), "edited");
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.True(Directory.Exists(Payload)); Assert.True(Directory.Exists(plan.CandidatePath)); Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedStageOrRetainedOriginalRefusesFurtherMoves(bool retained)
    {
        var plan = await Plan();
        if (retained) Apply(plan);
        File.AppendAllText(Path.Combine(retained ? plan.RetainedPath : plan.CandidatePath, "SKILL.md"), "modified");
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        if (!retained) Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.True(Directory.Exists(Payload));
    }

    [Fact]
    public async Task ModifiedInstalledReplacementIsRetainedInPlaceWhenUndoRefuses()
    {
        var plan = await Plan(); Apply(plan);
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        File.WriteAllText(Path.Combine(Payload, "new-user-file"), "keep");
        Assert.Throws<IOException>(() => Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Payload, "new-user-file")));
        Assert.True(Directory.Exists(plan.RetainedPath)); Assert.False(Directory.Exists(plan.DisplacedPath));
    }

    [Fact]
    public async Task CanceledBeforeMutationLeavesOriginalAndCanceledAfterMoveHasRecoverableJournal()
    {
        var plan = await Plan();
        Assert.Throws<OperationCanceledException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), new(true)));
        Assert.True(Directory.Exists(Payload)); Assert.False(Directory.Exists(plan.RetainedPath));
        using var cancellation = new CancellationTokenSource();
        var canceled = Store(name => { if (name == "retain-moved") cancellation.Cancel(); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), cancellation.Token);
        Assert.True(canceled.Canceled); Assert.True(canceled.MutationObserved); Assert.False(canceled.Succeeded);
        var inspected = Store().Inspect(plan.Id, Inventory(), Token); Assert.Equal("original-retained", inspected.Status);
        var restored = Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(restored.Succeeded, restored.Message); Assert.True(File.Exists(Path.Combine(Payload, "extra")));
    }

    [Fact]
    public async Task PartialLastJournalIsRetainedAndExplicitRecoveryCanAppend()
    {
        var plan = await Plan();
        Store(name => { if (name == "retain-moved") throw new IOException("Interrupted"); }).Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var broken = Path.Combine(State, plan.Id, "step-0001.json");
        WritePrivateJournal(broken, System.Text.Encoding.UTF8.GetBytes("{\"sequence\":"));
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        var recovered = Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), false, Token);
        Assert.True(recovered.Succeeded, recovered.Message);
        Assert.Equal("{\"sequence\":", File.ReadAllText(broken));
        Assert.True(File.Exists(Path.Combine(State, plan.Id, "step-0002.json")));
    }

    [Fact]
    public async Task UnrelatedSameNamedInstallationDoesNotChange()
    {
        var otherRoot = Directory.CreateDirectory(Path.Combine(temporary.FullName, "other-skills")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(otherRoot, "demo")).FullName;
        File.WriteAllText(Path.Combine(other, "SKILL.md"), Metadata + "Unrelated\n");
        var before = PayloadSnapshot.Capture(other, Token);
        var inventory = Inventory(new ScanRoot(otherRoot, Scope.Custom, null));
        var target = Assert.Single(inventory.Skills, skill => skill.ResolvedPath == Payload);
        var plan = await Store().PlanAsync(target, inventory, Stage, Token);
        var applied = Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(new ScanRoot(otherRoot, Scope.Custom, null)), Token);
        Assert.True(applied.Succeeded, applied.Message);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(other, Token).ObjectStateFingerprint);
    }

    [Fact]
    public async Task NewlyDiscoveredSharedConsumerInvalidatesSavedPlan()
    {
        var agentRoot = Directory.CreateDirectory(Path.Combine(temporary.FullName, "agent")).FullName;
        ScanRoot agent = new(agentRoot, Scope.User, "claude");
        var inventory = Inventory(agent); var target = Assert.Single(inventory.Skills);
        var plan = await Store().PlanAsync(target, inventory, Stage, Token);
        Directory.CreateSymbolicLink(Path.Combine(agentRoot, "demo"), Payload);
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(agent), Token));
        Assert.True(Directory.Exists(Payload)); Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedSharedReplacementCanRestartWithTemporarilyDanglingConsumer(bool undo)
    {
        var agentRoot = Directory.CreateDirectory(Path.Combine(temporary.FullName, "agent")).FullName;
        var link = Path.Combine(agentRoot, "demo");
        Directory.CreateSymbolicLink(link, Payload);
        var root = new ScanRoot(agentRoot, Scope.User, "claude");
        var inventory = Inventory(root);
        var before = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Store().PlanAsync(Assert.Single(inventory.Skills), inventory, Stage, Token);
        var checkpoint = undo ? "displace-moved" : "retain-moved";
        var interruptedStore = Store(name => { if (name == checkpoint) throw new IOException("Interrupted"); });
        if (undo)
        {
            Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(root), Token).Succeeded);
            var current = Store().Inspect(plan.Id, Inventory(root), Token);
            Assert.False(interruptedStore.Recover(plan.Id, current.ReviewFingerprint, Inventory(root), true, Token).Succeeded);
        }
        else Assert.False(interruptedStore.Apply(plan.Id, plan.PlanFingerprint, Inventory(root), Token).Succeeded);
        Assert.False(File.Exists(Path.Combine(link, "SKILL.md")));
        Assert.True(PathResolver.IsSymlink(link));
        var restarted = Store().Inspect(plan.Id, Inventory(root), Token);
        Assert.Equal(undo ? "replacement-retained" : "original-retained", restarted.Status);
        var restored = Store().Recover(plan.Id, restarted.ReviewFingerprint, Inventory(root), true, Token);
        Assert.True(restored.Succeeded, restored.Message);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.True(PathResolver.IsSymlink(link));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Payload, "SKILL.md")), File.ReadAllBytes(Path.Combine(link, "SKILL.md")));
        Assert.Equal("User extra", File.ReadAllText(Path.Combine(link, "extra")));
        Assert.True(NativeDirectoryMoveLease.SameObject(before.RootIdentity!.Value, PayloadSnapshot.Capture(Payload, Token).RootIdentity!.Value));
    }

    [Fact]
    public async Task OriginalSlotCollisionAfterRetentionNeverOverwritesReplacement()
    {
        var plan = await Plan();
        Store(name => { if (name == "retain-moved") throw new IOException("Interrupted"); }).Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Directory.CreateDirectory(Payload); File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata + "New unrelated object\n");
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.Contains("New unrelated", File.ReadAllText(Path.Combine(Payload, "SKILL.md")));
        Assert.True(Directory.Exists(plan.RetainedPath)); Assert.True(Directory.Exists(plan.CandidatePath));
    }

    [Fact]
    public async Task PlanFingerprintAndJournalSchemaAreCheckedBeforeMutation()
    {
        var plan = await Plan();
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, new string('0', 64), Inventory(), Token));
        var json = File.ReadAllText(plan.PlanPath); File.WriteAllText(plan.PlanPath, json.Replace("restore-recorded", "replace-anything"));
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.True(Directory.Exists(Payload)); Assert.False(Directory.Exists(plan.RetainedPath));
    }
    [Fact]
    public async Task NewlyCreatedConsumerDuringStagingInvalidatesEvenTheCapturedInventory()
    {
        var agentRoot = Directory.CreateDirectory(Path.Combine(temporary.FullName, "agent")).FullName;
        var inventory = Inventory(new ScanRoot(agentRoot, Scope.User, "claude"));
        var target = Assert.Single(inventory.Skills);
        await Assert.ThrowsAsync<IOException>(() => Store().PlanAsync(target, inventory, async (destination, ct) =>
        {
            await Stage(destination, ct);
            Directory.CreateSymbolicLink(Path.Combine(agentRoot, "demo"), Payload);
        }, Token));
        Assert.True(File.Exists(Path.Combine(Payload, "extra")));
    }

    [Fact]
    public async Task PinnedSharedConsumersKeepLinksAndExactOriginalOnUndo()
    {
        var text = File.ReadAllText(Path.Combine(Payload, "SKILL.md"));
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), text.Replace("  github-ref:", "  github-pinned: approved-pin\n  github-ref:") + "Local instructions\n");
        var agentRoot = Directory.CreateDirectory(Path.Combine(temporary.FullName, "agent")).FullName;
        var link = Path.Combine(agentRoot, "demo"); Directory.CreateSymbolicLink(link, Payload);
        var root = new ScanRoot(agentRoot, Scope.User, "claude");
        var inventory = Inventory(root); var target = Assert.Single(inventory.Skills) with { ScanRoot = agentRoot };
        var before = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Store().PlanAsync(target, inventory, async (destination, ct) =>
        {
            await Stage(destination, ct);
            var bound = RecordedTrackingWriter.Bind(Metadata + "Approved instructions\n", target.FrontMatter);
            File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), bound);
        }, Token);
        Assert.Equal("approved-pin", plan.Subject.Metadata.PinnedRef);
        var applied = Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(root), Token);
        Assert.True(applied.Succeeded, applied.Message); Assert.True(PathResolver.IsSymlink(link));
        var current = Store().Inspect(plan.Id, Inventory(root), Token);
        var restored = Store().Recover(plan.Id, current.ReviewFingerprint, Inventory(root), true, Token);
        Assert.True(restored.Succeeded, restored.Message); Assert.True(PathResolver.IsSymlink(link));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.True(File.Exists(Path.Combine(link, "extra")));
    }

    [Fact]
    public async Task NewOwnerRecordAfterInventoryCaptureRefusesWithoutInstalledMutation()
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        await Assert.ThrowsAsync<IOException>(() => Store().PlanAsync(target, inventory, async (destination, ct) =>
        {
            await Stage(destination, ct);
            File.WriteAllText(Path.Combine(temporary.FullName, "skills-lock.json"), "{\"version\":1,\"skills\":{\"demo\":{\"source\":\"other/repo\",\"sourceType\":\"github\",\"computedHash\":\"changed\"}}}");
        }, Token));
        Assert.True(File.Exists(Path.Combine(Payload, "extra")));
    }

    [Fact]
    public async Task NewlyExistingConfiguredRootIsScannedBeforePlanAdmission()
    {
        var agentRoot = Path.Combine(temporary.FullName, "new-agent");
        var inventory = Inventory() with
        {
            ConfiguredRoots = [new(Skills, Scope.Custom, null), new(agentRoot, Scope.User, "claude")]
        };
        var target = Assert.Single(inventory.Skills);
        await Assert.ThrowsAsync<IOException>(() => Store().PlanAsync(target, inventory, async (destination, ct) =>
        {
            await Stage(destination, ct);
            Directory.CreateDirectory(agentRoot);
            Directory.CreateSymbolicLink(Path.Combine(agentRoot, "demo"), Payload);
        }, Token));
        Assert.True(File.Exists(Path.Combine(Payload, "extra")));
    }

    [Fact]
    public async Task ExhaustedForwardJournalStillAllowsReviewedUndoThroughSeparateJournal()
    {
        var plan = await Plan();
        Store(name => { if (name == "retain-moved") throw new IOException("Interrupted"); }).Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var operation = Path.Combine(State, plan.Id);
        var first = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(operation, "step-0000.json")), ReplacementJsonContext.Default.ReplacementStep)!;
        for (var index = 1; index < 32; index++)
        {
            var path = Path.Combine(operation, $"step-{index:D4}.json");
            WritePrivateJournal(path, JsonSerializer.SerializeToUtf8Bytes(first with { Sequence = index }, ReplacementJsonContext.Default.ReplacementStep));
        }
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Throws<IOException>(() => Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), false, Token));
        var restored = Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(restored.Succeeded, restored.Message); Assert.True(File.Exists(Path.Combine(Payload, "extra")));
        Assert.True(File.Exists(Path.Combine(operation, "undo-recovery", "step-0000.json")));
    }

    [Fact]
    public async Task SecondaryJournalCannotDisplaceWhenWholeUndoWillNotFit()
    {
        var plan = await Plan(); Apply(plan);
        var operation = Path.Combine(State, plan.Id);
        var recovery = Path.Combine(operation, "undo-recovery");
        using (var directory = NativeStateDirectory.OpenOrCreate(recovery, [], Token)) { }
        var first = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(operation, "step-0000.json")), ReplacementJsonContext.Default.ReplacementStep)!;
        for (var index = 0; index < 29; index++)
        {
            var path = Path.Combine(recovery, $"step-{index:D4}.json");
            WritePrivateJournal(path, JsonSerializer.SerializeToUtf8Bytes(first with { Sequence = index, Status = "displace-intent" }, ReplacementJsonContext.Default.ReplacementStep));
        }
        var installed = PayloadSnapshot.Capture(Payload, Token);
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Throws<IOException>(() => Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token));
        Assert.Equal(installed.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.True(Directory.Exists(plan.RetainedPath)); Assert.False(Directory.Exists(plan.DisplacedPath));
    }

    [Fact]
    public async Task WhitespaceNamedLocalExtraSurvivesPlanApplyAndUndo()
    {
        var path = Path.Combine(Payload, " ");
        if (OperatingSystem.IsWindows()) path = @"\\?\" + path;
        File.WriteAllText(path, "Keep whitespace-named extra");
        var before = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Plan(); Assert.Contains(" ", plan.ChangedPaths); Apply(plan);
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        var restored = Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(restored.Succeeded, restored.Message);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal("Keep whitespace-named extra", File.ReadAllText(path));
    }

    [Fact]
    public async Task CompleteHiddenConsumerInventoryRemainsUsableThroughApplyAndUndo()
    {
        var container = Directory.CreateDirectory(Path.Combine(Skills, ".hidden")).FullName;
        var link = Path.Combine(container, "demo"); Directory.CreateSymbolicLink(link, Payload);
        InventorySnapshot Capture()
        {
            ScanRoot[] roots = [new(Skills, Scope.Custom, null)];
            var inaccessible = new List<string>();
            var skills = new LocalSkillScanner(new Logger()).ScanWithCancellation(roots,
                new LocalSkillScanner.Options(AllowHiddenDirs: true), Token, inaccessible);
            Assert.Empty(inaccessible);
            return InventorySnapshot.Empty with { Skills = skills, ScannedRoots = roots.ToImmutableArray(), ConfiguredRoots = roots.ToImmutableArray() };
        }
        var inventory = Capture(); var target = Assert.Single(inventory.Skills);
        Assert.Equal(2, target.Agents.Length);
        var plan = await Store().PlanAsync(target, inventory, Stage, Token);
        var applied = Store().Apply(plan.Id, plan.PlanFingerprint, Capture(), Token);
        Assert.True(applied.Succeeded, applied.Message);
        var inspected = Store().Inspect(plan.Id, Capture(), Token);
        var restored = Store().Recover(plan.Id, inspected.ReviewFingerprint, Capture(), true, Token);
        Assert.True(restored.Succeeded, restored.Message); Assert.True(File.Exists(Path.Combine(link, "extra")));
    }

    private void WritePrivateJournal(string path, byte[] bytes)
    {
        using var directory = NativeStateDirectory.OpenOrCreate(Path.GetDirectoryName(path)!, [], Token);
        using var writer = directory.OpenFile("writer.lock", true, Token);
        using (var file = directory.OpenFile(Path.GetFileName(path), false, Token)) { file.Write(bytes); file.Flush(true); }
        directory.FlushPublication(Token);
    }

}
