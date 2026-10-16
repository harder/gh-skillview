using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class StandaloneUnpinTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-unpin-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Skills => Path.Combine(temp.FullName, "skills");
    private string Payload => Path.Combine(Skills, "demo");
    private string State => Path.Combine(temp.FullName, "state");
    private const string Metadata = "---\nname: demo\ndescription: Edited locally\nmetadata:\n  github-repo: https://github.com/harder/fixture\n  github-ref: refs/tags/v1\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n  github-pinned: v1 # pin comment stays\n---\nLocal authored instructions\n";
    public StandaloneUnpinTests()
    {
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata);
        File.WriteAllBytes(Path.Combine(Payload, "extra"), [0, 255, 13, 10]);
        Directory.CreateDirectory(Path.Combine(Payload, "empty"));
        if (OperatingSystem.IsWindows())
            foreach (var path in new[] { Payload, Path.Combine(Payload, "SKILL.md"), Path.Combine(Payload, "extra"), Path.Combine(Payload, "empty") })
                WindowsPayloadMetadataTests.SetFixtureOwner(path);
    }
    public void Dispose() => temp.Delete(true);
    private ReplacementPlanStore Store(Action<string>? checkpoint = null) => new(State, new Logger(), checkpoint,
        new ProcessRunner(new Logger(), environment: new Dictionary<string, string>
        { ["HOME"] = temp.FullName, ["USERPROFILE"] = temp.FullName }, defaultWorkingDirectory: temp.FullName));
    private InventorySnapshot Inventory()
    {
        ScanRoot[] roots = [new(Skills, Scope.Custom, null)];
        return InventorySnapshot.Empty with
        {
            Skills = new LocalSkillScanner(new Logger()).Scan(roots).ToImmutableArray(),
            ConfiguredRoots = roots.ToImmutableArray(),
            ScannedRoots = roots.ToImmutableArray()
        };
    }
    private ReplacementResult? Plan(ReplacementPlanStore? store = null)
    {
        var inventory = Inventory(); var before = PayloadSnapshot.Capture(Payload, Token);
        try { return Assert.IsType<ReplacementResult>((store ?? Store()).PlanUnpin(Assert.Single(inventory.Skills), inventory, Token).Plan); }
        catch (IOException ex) when (OperatingSystem.IsLinux() && ex.Message.Contains("CAP_SYS_ADMIN", StringComparison.Ordinal))
        {
            // Ordinary Linux test hosts cannot prove visibility of trusted.*.
            // Assert the concrete refusal; native publication success is proven
            // separately on admitted macOS/Windows/privileged Linux hosts.
            Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
            Assert.Equal(before.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
            Assert.Empty(Directory.EnumerateFiles(State, "plan.json", SearchOption.AllDirectories));
            return null;
        }
    }

    [Fact]
    public void StrictInstalledArchiveBindsNativeEvidenceDespiteDistinctSkillComparisonHashFlavor()
    {
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(Payload, "local-link"), "extra");
        using var original = NativeDirectoryMoveLease.Open(Skills, Payload, Token);
        var native = original.Capture(Token);
        using var state = NativeStateDirectory.OpenOrCreate(State, [Payload], Token);
        var installed = CapturedPayloadArchive.Capture(state, "installed", original, Token);
        var beforeSkill = Assert.Single(native.Entries, entry => entry.Path == "SKILL.md");
        var archivedSkill = Assert.Single(installed.Snapshot.Entries, entry => entry.Path == "SKILL.md");
        Assert.Equal(beforeSkill.ContentHash, beforeSkill.ComparisonHash);
        Assert.NotEqual(archivedSkill.ContentHash, archivedSkill.ComparisonHash);
        Assert.Equal(beforeSkill.ContentHash, archivedSkill.ContentHash);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(Assert.Single(native.Entries, entry => entry.Path == "local-link").UnixMode);
            Assert.NotNull(Assert.Single(installed.Snapshot.Entries, entry => entry.Path == "local-link").UnixMode);
        }
        ReplacementPlanStore.VerifyUnpinOriginal(ReplacementEvidence.From(native), installed, Token);
        // The strict immutable archive keeps its authored-comparison hash.
        Assert.Equal(archivedSkill, Assert.Single(installed.Snapshot.Entries, entry => entry.Path == "SKILL.md"));
    }

    [Theory]
    [InlineData("root-id")]
    [InlineData("root-generation")]
    [InlineData("root-mode")]
    [InlineData("entry-hash")]
    [InlineData("entry-comparison")]
    [InlineData("entry-mode")]
    [InlineData("entry-bytes")]
    [InlineData("child-generation")]
    [InlineData("payload-fingerprint")]
    [InlineData("object-fingerprint")]
    public void ArchiveProjectionStillRejectsChangedRawContentModesAndFullNativeEvidence(string change)
    {
        using var lease = NativeDirectoryMoveLease.Open(Skills, Payload, Token);
        var native = lease.Capture(Token);
        using var state = NativeStateDirectory.OpenOrCreate(State, [Payload], Token);
        var installed = CapturedPayloadArchive.Capture(state, "installed", lease, Token);
        var original = ReplacementEvidence.From(native);
        var entry = original.Entries[0];
        var child = original.Children[0];
        original = change switch
        {
            "root-id" => original with { Identity = original.Identity with { FileIdLow = original.Identity.FileIdLow ^ 1 } },
            "root-generation" => original with { Identity = original.Identity with { ChangeTimeNanoseconds = original.Identity.ChangeTimeNanoseconds + 1, WindowsChangeTime = original.Identity.WindowsChangeTime + 1 } },
            "root-mode" => original with { Mode = original.Mode == 448 ? 493 : 448 },
            "entry-hash" => original with { Entries = original.Entries.SetItem(0, entry with { ContentHash = new('f', 64) }) },
            "entry-comparison" => original with { Entries = original.Entries.SetItem(0, entry with { ComparisonHash = new('f', 64) }) },
            "entry-mode" => original with { Entries = original.Entries.SetItem(0, entry with { UnixMode = entry.UnixMode == 448 ? 493 : 448 }) },
            "entry-bytes" => original with { Entries = original.Entries.SetItem(0, entry with { Bytes = entry.Bytes + 1 }) },
            "payload-fingerprint" => original with { Payload = new('f', 64) },
            "object-fingerprint" => original with { Objects = new('f', 64) },
            _ => original with { Children = original.Children.SetItem(0, child with { Identity = child.Identity with { ChangeTimeNanoseconds = child.Identity.ChangeTimeNanoseconds + 1, WindowsChangeTime = child.Identity.WindowsChangeTime + 1 } }) }
        };
        var error = Assert.Throws<IOException>(() => ReplacementPlanStore.VerifyUnpinOriginal(original, installed, Token));
        Assert.Contains("Unpin archive is not the actual captured original", error.Message);
        Assert.Contains("match: False", error.Message);
    }

    [Fact]
    public void AlreadyUnpinnedPrecedesStateWriterAndFullQuotaAndNeverNeedsSource()
    {
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata.Replace("  github-pinned: v1 # pin comment stays\n", ""));
        var before = PayloadSnapshot.Capture(Payload, Token);
        var inventory = Inventory();
        var first = Store().PlanUnpin(Assert.Single(inventory.Skills), inventory, Token);
        Assert.NotNull(first.Unchanged); Assert.Null(first.Plan); Assert.False(Directory.Exists(State));
        Directory.CreateDirectory(State);
        for (var i = 0; i < 32; i++) File.WriteAllText(Path.Combine(State, "full-" + i), "quota sentinel");
        var entries = Directory.GetFileSystemEntries(State).Order().ToArray();
        var second = Store().PlanUnpin(Assert.Single(inventory.Skills), inventory, Token);
        Assert.NotNull(second.Unchanged); Assert.Equal(entries, Directory.GetFileSystemEntries(State).Order().ToArray());
        Assert.False(File.Exists(Path.Combine(State, "writer.lock")));
        Assert.Equal(before.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }

    [Fact]
    public void PartialInventoryAndRootAuthoredPinRefuseBeforeStateCreation()
    {
        var inventory = Inventory();
        Assert.Throws<IOException>(() => Store().PlanUnpin(Assert.Single(inventory.Skills), inventory with { ConfiguredRootsComplete = false }, Token));
        Assert.False(Directory.Exists(State));
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata.Replace("description:", "pinned: true\ndescription:"));
        inventory = Inventory();
        Assert.Throws<IOException>(() => Store().PlanUnpin(Assert.Single(inventory.Skills), inventory, Token));
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void RefusedPrivateCaptureReportsItsExactRetainedIdentityAndUnderlyingReason()
    {
        var inventory = Inventory(); var before = PayloadSnapshot.Capture(Payload, Token);
        var failure = new IOException("Captured private evidence cannot be finalized");
        var store = Store(step => { if (step == "unpin-installed-captured") throw failure; });
        var error = Assert.Throws<IOException>(() => store.PlanUnpin(Assert.Single(inventory.Skills), inventory, Token));
        var operation = Assert.Single(Directory.EnumerateDirectories(State));
        Assert.Contains(Path.GetFileName(operation), error.Message); Assert.Contains(operation, error.Message);
        Assert.Contains(failure.Message, error.Message); Assert.Same(failure, error.InnerException);
        Assert.True(Directory.Exists(Path.Combine(operation, "installed-capture"))); Assert.False(File.Exists(Path.Combine(operation, "plan.json")));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(before.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }

    [Fact]
    public void CanceledPrivateCaptureReportsRetainedEvidenceAndPreservesCancellationToken()
    {
        var inventory = Inventory(); var before = PayloadSnapshot.Capture(Payload, Token);
        using var cancellation = new SkillView.Threading.CancellationSource(Token, _ => { });
        var store = Store(step => { if (step == "unpin-installed-captured") cancellation.TryCancel(); });
        var error = Assert.ThrowsAny<OperationCanceledException>(() => store.PlanUnpin(Assert.Single(inventory.Skills), inventory, cancellation.Token));
        var operation = Assert.Single(Directory.EnumerateDirectories(State));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Contains(Path.GetFileName(operation), error.Message); Assert.Contains(operation, error.Message);
        Assert.True(Directory.Exists(Path.Combine(operation, "installed-capture"))); Assert.False(File.Exists(Path.Combine(operation, "plan.json")));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(before.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }

    [Fact]
    public void AuthoredMetadataChangedAfterInventoryRefusesBeforePrivateWrites()
    {
        var inventory = Inventory();
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata.Replace("Edited locally", "Changed after inventory"));
        var error = Assert.Throws<IOException>(() => Store().PlanUnpin(Assert.Single(inventory.Skills), inventory, Token));
        Assert.Contains("authored metadata changed", error.Message); Assert.False(Directory.Exists(State));
    }

    [Theory]
    [InlineData("agents: [claude, codex]", "agents: 'claude, codex'", "agents")]
    [InlineData("allowed-tools: [Read, Write]", "allowed-tools: 'Read, Write'", "allowed-tools")]
    public void FlattenedMetadataCollisionCannotHideChangedParsedCollections(string original, string changed, string key)
    {
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata.Replace("metadata:", original + "\nmetadata:"));
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata.Replace("metadata:", changed + "\nmetadata:"));
        var current = Assert.Single(Inventory().Skills);
        Assert.Equal(target.FrontMatter.Extra[key], current.FrontMatter.Extra[key]);
        var error = Assert.Throws<IOException>(() => Store().PlanUnpin(target, inventory, Token));
        Assert.Contains("authored metadata changed", error.Message); Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void EquivalentParsedMetadataPresentationStillAdmitsVerifiedNoop()
    {
        var unpinned = Metadata.Replace("  github-pinned: v1 # pin comment stays\n", "");
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), unpinned);
        var inventory = Inventory();
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), unpinned.Replace("description: Edited locally", "description: 'Edited locally'"));
        var result = Store().PlanUnpin(Assert.Single(inventory.Skills), inventory, Token);
        Assert.NotNull(result.Unchanged); Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void CoordinatorCancellationRetainsExactPrivateEvidenceInOutcomeAndDurableLog()
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        var before = PayloadSnapshot.Capture(Payload, Token);
        using var cancellation = new SkillView.Threading.CancellationSource(Token, _ => { });
        var store = Store(step => { if (step == "unpin-installed-captured") cancellation.TryCancel(); });
        var logger = new Logger(); var runner = new ProcessRunner(logger);
        var catalog = new SkillView.Gh.GhSkillListAdapter(runner, logger);
        var availability = new SkillUpdateCoordinator(new(runner, logger), new(runner, logger), catalog, logger, runner);
        var coordinator = new SavedUpdateCoordinator(store, availability, catalog, logger);
        var logDirectory = Path.Combine(temp.FullName, "logs");
        SkillView.Gh.Models.UpdateResult result;
        using (var sink = new FileLogSink(logDirectory))
        {
            sink.Attach(logger);
            result = coordinator.PrepareUnpin(target, inventory, cancellation.Token);
        }
        var operation = Assert.Single(Directory.EnumerateDirectories(State));
        Assert.False(result.Succeeded); Assert.True(result.Cancelled); Assert.Equal(130, result.ExitCode);
        Assert.Null(result.SavedPlan); Assert.False(result.MutationAttempted);
        Assert.Contains(operation, result.ErrorMessage); Assert.Contains(Path.GetFileName(operation), result.ErrorMessage);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal("canceled", outcome.Status); Assert.Equal(result.ErrorMessage, outcome.Message);
        var log = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(logDirectory, "skillview-*.log")));
        Assert.Contains(operation, log); Assert.Contains(Path.GetFileName(operation), log); Assert.Contains("OperationCanceledException", log);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(before.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }

    [Fact]
    public void ExactInstalledClonePreservesLocalContentModesAndOriginalNativeUndo()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Payload, (UnixFileMode)448);
            File.SetUnixFileMode(Path.Combine(Payload, "extra"), (UnixFileMode)448);
        }
        var before = PayloadSnapshot.Capture(Payload, Token);
        var plan = Plan(); if (plan is null) return;
        Assert.Null(plan.Revision); Assert.Null(plan.LocalDisposition); Assert.NotNull(plan.PinPolicy);
        Assert.Contains("new native object identities", plan.Message);
        using (var json = JsonDocument.Parse(File.ReadAllBytes(plan.PlanPath)))
        {
            Assert.Equal(4, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("unpin-only", json.RootElement.GetProperty("purpose").GetString());
            Assert.False(json.RootElement.TryGetProperty("revision", out _));
        }
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "retain-original", "unpin-only"));
        var applied = Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "unpin-only", "unpin-only");
        Assert.True(applied.Succeeded, applied.Message);
        Assert.Equal(PinTrackingWriter.Unpin(System.Text.Encoding.UTF8.GetBytes(Metadata)), File.ReadAllBytes(Path.Combine(Payload, "SKILL.md")));
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, File.ReadAllBytes(Path.Combine(Payload, "extra")));
        Assert.True(Directory.Exists(Path.Combine(Payload, "empty")));
        var current = Assert.Single(Inventory().Skills).FrontMatter;
        Assert.False(current.Pinned); Assert.Equal("refs/tags/v1", current.GithubRef); Assert.Equal(new string('a', 40), current.GithubTreeSha);
        Assert.True(NativeDirectoryMoveLease.SameObject(before.RootIdentity!.Value, PayloadSnapshot.Capture(plan.RetainedPath, Token).RootIdentity!.Value));
        var inspect = Store().Inspect(plan.Id, Inventory(), Token, "unpin-only");
        var undone = Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), true, Token, "unpin-only");
        Assert.True(undone.Succeeded, undone.Message);
        var restored = PayloadSnapshot.Capture(Payload, Token);
        Assert.Equal(before.Fingerprint, restored.Fingerprint); Assert.Equal(before.Objects, restored.Objects);
        Assert.Equal(before.RootUnixMode, restored.RootUnixMode);
    }

    [Theory]
    [InlineData("retain-moved")]
    [InlineData("install-moved")]
    [InlineData("displace-moved")]
    [InlineData("restore-moved")]
    public void EveryPayloadMoveBoundaryRecoversAndRestoresExactOriginal(string stop)
    {
        var original = PayloadSnapshot.Capture(Payload, Token);
        var plan = Plan(); if (plan is null) return;
        var undoStop = stop is "displace-moved" or "restore-moved";
        var first = Store(undoStop ? null : step => { if (step == stop) throw new IOException("stop"); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "unpin-only", "unpin-only");
        Assert.Equal(undoStop, first.Succeeded);
        var inspect = Store().Inspect(plan.Id, Inventory(), Token, "unpin-only");
        if (undoStop)
        {
            var interrupted = Store(step => { if (step == stop) throw new IOException("stop"); })
                .Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), true, Token, "unpin-only");
            Assert.False(interrupted.Succeeded); Assert.True(interrupted.MutationObserved);
        }
        else
        {
            var recovered = Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), false, Token, "unpin-only");
            Assert.True(recovered.Succeeded, recovered.Message);
        }
        inspect = Store().Inspect(plan.Id, Inventory(), Token, "unpin-only");
        var undo = Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), true, Token, "unpin-only");
        Assert.True(undo.Succeeded, undo.Message); Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(original.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("candidate")]
    [InlineData("exposure")]
    [InlineData("archive")]
    public void StalePayloadCandidateExposureAndArchiveRefuseBeforeMoves(string change)
    {
        var plan = Plan(); if (plan is null) return;
        var inventory = Inventory();
        if (change == "payload") File.AppendAllText(Path.Combine(Payload, "extra"), "later");
        if (change == "candidate") File.AppendAllText(Path.Combine(plan.CandidatePath, "extra"), "later");
        if (change == "exposure") inventory = inventory with { ConfiguredRoots = inventory.ConfiguredRoots.Add(new(Path.Combine(temp.FullName, "added"), Scope.Custom, null)) };
        if (change == "archive") File.AppendAllText(Path.Combine(State, plan.Id, "installed-capture", "entry-00000"), "tamper");
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, inventory, Token, "unpin-only", "unpin-only"));
        Assert.False(Directory.Exists(plan.RetainedPath));
    }
}
