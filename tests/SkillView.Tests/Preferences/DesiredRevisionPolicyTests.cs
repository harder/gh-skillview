using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Preferences;
using Xunit;

namespace SkillView.Tests.Preferences;

public sealed class DesiredRevisionPolicyTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-desired-policy-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Skills => Path.Combine(temporary.FullName, "skills");
    private string Payload => Path.Combine(Skills, "demo");
    private string State => Path.Combine(temporary.FullName, "replacements");
    private string PolicyRoot => Path.Combine(temporary.FullName, "desired-state");
    private DesiredRevisionPolicyStore Policies => new(PolicyRoot);
    private ReplacementPlanStore Plans(Action<string>? checkpoint = null) => new(State, new Logger(), checkpoint);
    private const string Metadata = "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/fixture/original\n  github-ref: refs/heads/main\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n---\n";

    public DesiredRevisionPolicyTests()
    {
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata + "Original\n");
    }

    private InventorySnapshot Inventory() => InventorySnapshot.Empty with
    {
        Skills = new LocalSkillScanner(new Logger()).ScanWithCancellation([new(Skills, Scope.Custom, null)], null, Token),
        ScannedRoots = [new(Skills, Scope.Custom, null)],
        ConfiguredRoots = [new(Skills, Scope.Custom, null)]
    };
    private InstalledSkill Target => Assert.Single(Inventory().Skills);
    private DesiredPolicyObservation Observe() => Policies.Observe(Target, [Skills], Token);
    private DesiredPolicyObservation Set(DesiredRevisionPolicy? policy)
    {
        var target = Target;
        var before = Policies.Observe(target, [Skills], Token);
        using var writer = Policies.Acquire([Skills], Token);
        return writer.Set(target, policy, before.Fingerprint!, Token);
    }
    private async Task<ReplacementResult> Plan()
    {
        var inventory = Inventory();
        return await Plans().PlanAsync(Assert.Single(inventory.Skills), inventory, (stage, _) =>
        {
            File.WriteAllText(Path.Combine(stage.CanonicalPath, "SKILL.md"), Metadata + "Saved candidate\n");
            return Task.CompletedTask;
        }, Token);
    }

    [Fact]
    public void ProvenMissingIsReadOnlyDefaultButExistingIncompleteFileAndBrokenLinkAreUnknown()
    {
        var absent = Observe();
        Assert.Equal("absent", absent.Status); Assert.True(absent.Known); Assert.NotNull(absent.Fingerprint);
        Assert.False(Directory.Exists(PolicyRoot));
        Directory.CreateDirectory(PolicyRoot);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(PolicyRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal("unknown", Observe().Status); // Existing directory without a writer is not a default.
        Directory.Delete(PolicyRoot);
        File.WriteAllText(PolicyRoot, "a file is not absent");
        Assert.Equal("unknown", Observe().Status); File.Delete(PolicyRoot);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(PolicyRoot, Path.Combine(temporary.FullName, "missing-target"));
            Assert.Equal("unknown", Observe().Status);
            File.Delete(PolicyRoot);
        }
    }

    [Fact]
    public void InaccessibleNativeParentNeverBecomesDefault()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix permission boundary; Windows ACL proof is separate."); return; }
        var denied = Directory.CreateDirectory(Path.Combine(temporary.FullName, "denied")).FullName;
        File.SetUnixFileMode(denied, UnixFileMode.None);
        try
        {
            var read = new DesiredRevisionPolicyStore(Path.Combine(denied, "desired-state")).Observe(Target, [Skills], Token);
            Assert.Equal("unknown", read.Status); Assert.False(read.Known); Assert.Null(read.Fingerprint);
        }
        finally { File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public async Task OriginalPolicyAncestorAliasCannotSwitchStoresAtFirstMutation()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("POSIX ancestor-alias fixture; Windows native address proof requires its host."); return; }
        var first = Directory.CreateDirectory(Path.Combine(temporary.FullName, "context-a")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(temporary.FullName, "context-b")).FullName;
        var alias = Path.Combine(temporary.FullName, "context-alias");
        Directory.CreateSymbolicLink(alias, first);
        var other = new DesiredRevisionPolicyStore(Path.Combine(second, "desired-state"));
        var target = Target;
        var prior = other.Observe(target, [Skills], Token);
        using (var writer = other.Acquire([Skills], Token)) writer.Set(target, new("manual-fork"), prior.Fingerprint!, Token);
        var root = Path.Combine(alias, "replacements");
        var plan = await new ReplacementPlanStore(root, new Logger()).PlanAsync(target, Inventory(), (stage, _) =>
        {
            File.WriteAllText(Path.Combine(stage.CanonicalPath, "SKILL.md"), Metadata + "Selected candidate\n");
            return Task.CompletedTask;
        }, Token);
        var original = PayloadSnapshot.Capture(Payload, Token);
        var switched = false;
        var store = new ReplacementPlanStore(root, new Logger(), phase =>
        {
            if (phase != "step-0000.json") return;
            Directory.Delete(alias); Directory.CreateSymbolicLink(alias, second); switched = true;
        });
        var result = store.Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "retain-original");
        Assert.True(switched);
        Assert.False(result.Succeeded); Assert.False(result.MutationObserved);
        Assert.Contains("original address", result.Message);
        var after = PayloadSnapshot.Capture(Payload, Token);
        Assert.Equal(original.Fingerprint, after.Fingerprint);
        Assert.Equal(original.Objects, after.Objects);
        Assert.Equal(original.RootIdentity, after.RootIdentity);
        Assert.Equal("manual-fork", other.Observe(Target, [Skills], Token).Policy!.Mode);
        Directory.Delete(alias);
    }

    [Fact]
    public void ForkIntentDoesNotWritePayloadOrPinsAndChangedSourceIsStaleRatherThanDefault()
    {
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata.Replace("  github-ref:", "  github-pinned: v1\n  github-ref:") + "Custom instructions\n");
        var bytes = File.ReadAllBytes(Path.Combine(Payload, "SKILL.md"));
        var fork = Set(new("manual-fork"));
        Assert.Equal("active", fork.Status); Assert.True(Target.Pinned);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Payload, "SKILL.md")));
        Assert.Throws<IOException>(() => DesiredRevisionPolicyGate.RequireAllowed(Target, fork, "update-revision", explicitUnpin: true));
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), System.Text.Encoding.UTF8.GetString(bytes).Replace("fixture/original", "fixture/different"));
        var stale = Observe(); Assert.Equal("stale-binding", stale.Status); Assert.False(stale.Known);
        using (var writer = Policies.Acquire([Skills], Token))
            Assert.Equal("absent", writer.Set(Target, null, stale.Fingerprint!, Token).Status);
        Assert.True(Target.Pinned); // Clearing intent never clears actual metadata.
    }

    [Fact]
    public async Task LegacyReadyPlanCannotApplyNewPolicyButRemainsInspectableAndUndoable()
    {
        var plan = await Plan();
        var saved = File.ReadAllBytes(plan.PlanPath);
        File.Delete(Path.Combine(Path.GetDirectoryName(plan.PlanPath)!, "desired-policy.json"));
        Set(new("follow-gh-default"));
        var error = Assert.Throws<IOException>(() => Plans().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Contains("legacy ready plan", error.Message);
        Assert.Equal(saved, File.ReadAllBytes(plan.PlanPath));
        Assert.False(Directory.Exists(plan.RetainedPath));
        var inspection = Plans().Inspect(plan.Id, Inventory(), Token);
        Assert.Equal("ready", inspection.Status);
        var recovery = Assert.Throws<IOException>(() => Plans().Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), false, Token));
        Assert.Contains("legacy ready plan", recovery.Message);
        Assert.True(Plans().Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), true, Token).Succeeded);
    }

    [Fact]
    public async Task PolicyPublicationClosesEarlierReceiptEvenWhenModeIsStillDefault()
    {
        var plan = await Plan();
        Set(new("follow-gh-default"));
        Assert.Throws<IOException>(() => Plans().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.False(Directory.Exists(plan.RetainedPath));
        Assert.Contains("Original", File.ReadAllText(Path.Combine(Payload, "SKILL.md")));
        Set(null);
        Assert.Equal("absent", Observe().Status);
        Assert.Throws<IOException>(() => Plans().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.False(Directory.Exists(plan.RetainedPath)); // Clear cannot revive an old default receipt.
    }

    [Fact]
    public async Task FirstApplyHoldsPolicyWriterAndStartedUndoSurvivesCorruptPolicy()
    {
        var before = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Plan();
        var concurrentRefused = false;
        var result = Plans(phase =>
        {
            if (phase != "step-0000.json") return;
            Assert.Throws<IOException>(() => Policies.Acquire([Skills], Token));
            concurrentRefused = true;
        }).Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.True(concurrentRefused); Assert.True(result.Succeeded, result.Message);
        File.WriteAllText(Path.Combine(PolicyRoot, "corrupt.json"), "broken");
        Assert.Equal("unknown", Observe().Status);
        var inspection = Plans().Inspect(plan.Id, Inventory(), Token);
        var undone = Plans().Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(undone.Succeeded, undone.Message);
        var restored = PayloadSnapshot.Capture(Payload, Token);
        Assert.Equal(before.Fingerprint, restored.Fingerprint);
        Assert.True(NativeDirectoryMoveLease.SameObject(before.RootIdentity!.Value, restored.RootIdentity!.Value));
    }

    [Theory]
    [InlineData("manual-fork")]
    [InlineData("exact-pin")]
    [InlineData("native-manager")]
    [InlineData("follow-chosen-track")]
    public void ProtectedIntentCannotBecomeRoutineUpdateOrForce(string mode)
    {
        var observation = Set(new(mode, mode is "exact-pin" or "follow-chosen-track" ? "v1" : null));
        Assert.Throws<IOException>(() => DesiredRevisionPolicyGate.RequireAllowed(Target, observation, "update-revision", explicitUnpin: true));
    }

    public void Dispose() => temporary.Delete(true);
}
