using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class ReplacementPlanStoreTests
{
    private Task<InventorySnapshot> RetirementInventory(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Inventory()); }
    private async Task<RetirementReview> RetirementInspection(ReplacementResult plan)
    {
        var result = await Store().InspectHistoryAsync(plan.Id, RetirementInventory, [], Token);
        return result.Retirement ?? throw new IOException("Fixture did not admit retirement: " + result.Message);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("installed")]
    [InlineData("restored")]
    public async Task RetirementDeletesOnlySavedOperationAndPreservesLiveObjectsAndConsumers(string topology)
    {
        var plan = await Plan();
        if (topology != "ready") Apply(plan);
        if (topology == "restored") Store().Recover(plan.Id, Store().Inspect(plan.Id, Inventory(), Token).ReviewFingerprint, Inventory(), true, Token);
        var before = PayloadSnapshot.Capture(Payload, Token);
        var consumers = Inventory().Skills.SelectMany(skill => skill.Agents).ToArray();
        var review = await RetirementInspection(plan);
        Assert.Equal(topology == "ready" ? "abandon-review" : "discard-undo", review.Kind);
        Assert.False(review.Resuming);
        Assert.Contains(topology == "ready" ? "live location stays unchanged" : "lose Undo", review.Consequence);
        await Assert.ThrowsAsync<IOException>(() => Store().RetireAsync(review, "yes", RetirementInventory, [], Token));
        Assert.True(File.Exists(plan.PlanPath));
        await Store().RetireAsync(review, review.Kind, RetirementInventory, [], Token);
        Assert.False(Directory.Exists(Path.Combine(State, plan.Id)));
        Assert.Empty(Store().ReadHistory(Token).Entries);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(consumers, Inventory().Skills.SelectMany(skill => skill.Agents));
    }

    [Fact]
    public async Task RetirementActuallyReleasesCapacityForSeventeenthReview()
    {
        var first = await Plan();
        for (var index = 1; index < 16; index++) await Plan();
        await Assert.ThrowsAsync<IOException>(() => Plan());
        var review = await RetirementInspection(first);
        var interrupted = Store(point => { if (point == "retirement-removed-candidate") throw new IOException("Quota must remain occupied"); });
        await Assert.ThrowsAsync<IOException>(() => interrupted.RetireAsync(review, review.Kind, RetirementInventory, [], Token));
        Assert.Equal(16, Store().ReadHistory(Token).ObservedCount);
        await Assert.ThrowsAsync<IOException>(() => Plan());
        review = await RetirementInspection(first);
        await Store().RetireAsync(review, review.Kind, RetirementInventory, [], Token);
        Assert.Equal(15, Store().ReadHistory(Token).ObservedCount);
        var seventeenth = await Plan();
        Assert.NotEqual(first.Id, seventeenth.Id);
        Assert.Equal(16, Store().ReadHistory(Token).ObservedCount);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("child")]
    [InlineData("unknown")]
    [InlineData("plan")]
    [InlineData("operation")]
    [InlineData("alias")]
    public async Task StaleRetirementApprovalRefusesAllChangesBeforeMarker(string change)
    {
        if (change == "alias" && OperatingSystem.IsWindows()) return;
        var plan = await Plan();
        var review = await RetirementInspection(plan);
        var operation = Path.Combine(State, plan.Id);
        if (change == "target") File.AppendAllText(Path.Combine(Payload, "extra"), "changed");
        else if (change == "child")
        {
            var path = Path.Combine(plan.CandidatePath, "script.sh");
            var bytes = File.ReadAllBytes(path); File.Delete(path); File.WriteAllBytes(path, bytes);
        }
        else if (change == "unknown") File.WriteAllText(Path.Combine(operation, "unknown"), "must remain");
        else if (change == "plan") File.AppendAllText(plan.PlanPath, " ");
        else
        {
            Directory.Move(operation, operation + "-preserved");
            if (change == "alias") Directory.CreateSymbolicLink(operation, operation + "-preserved");
            else
            {
                Directory.CreateDirectory(operation);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(operation, (UnixFileMode)448);
                File.Copy(Path.Combine(operation + "-preserved", "plan.json"), plan.PlanPath);
                File.WriteAllText(Path.Combine(operation, "writer.lock"), "");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(plan.PlanPath, (UnixFileMode)384);
                    File.SetUnixFileMode(Path.Combine(operation, "writer.lock"), (UnixFileMode)384);
                }
            }
        }
        await Assert.ThrowsAnyAsync<IOException>(() => Store().RetireAsync(review, review.Kind, RetirementInventory, [], Token));
        Assert.True(File.Exists(Path.Combine(Payload, "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(operation, "retiring.json")));
    }

    [Theory]
    [InlineData("root-lock")]
    [InlineData("operation-lock")]
    [InlineData("partial")]
    [InlineData("active")]
    [InlineData("local-review")]
    [InlineData("undo-recovery")]
    [InlineData("unknown-directory")]
    public async Task RetirementAdmissionProtectsIncompleteConflictedBusyAndHistoricalOperations(string change)
    {
        var plan = await Plan(); var review = await RetirementInspection(plan);
        NativeStateDirectory? held = null; FileStream? writer = null;
        var active = change == "active" ? new[] { plan.Id } : [];
        try
        {
            if (change.EndsWith("lock", StringComparison.Ordinal))
            {
                held = NativeStateDirectory.OpenOrCreate(change == "root-lock" ? State : Path.Combine(State, plan.Id), [], Token, allowCreate: false);
                writer = held.OpenExistingFile("writer.lock", true, Token);
            }
            else if (change is not ("partial" or "active")) Directory.CreateDirectory(Path.Combine(State, plan.Id, change));
            Task<InventorySnapshot> Capture(CancellationToken _) => Task.FromResult(change == "partial" ? Inventory() with { Providers = [new("fixture-partial", false, [], DateTimeOffset.UtcNow)] } : Inventory());
            await Assert.ThrowsAnyAsync<IOException>(() => Store().RetireAsync(review, review.Kind, Capture, active, Token));
        }
        finally { writer?.Dispose(); held?.Dispose(); }
        Assert.True(File.Exists(plan.PlanPath)); Assert.True(Directory.Exists(plan.CandidatePath));
        Assert.False(File.Exists(Path.Combine(State, plan.Id, "retiring.json")));
    }

    [Theory]
    [InlineData("retain-moved")]
    [InlineData("displace-moved")]
    public async Task RetirementNeverAdmitsIntermediateTopology(string stop)
    {
        var plan = await Plan();
        if (stop == "displace-moved") Apply(plan);
        var interrupted = Store(point => { if (point == stop) throw new IOException("Interruption"); });
        if (stop == "retain-moved") interrupted.Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        else interrupted.Recover(plan.Id, Store().Inspect(plan.Id, Inventory(), Token).ReviewFingerprint, Inventory(), true, Token);
        var result = await Store().InspectHistoryAsync(plan.Id, RetirementInventory, [], Token);
        Assert.Null(result.Retirement); Assert.Contains("protected", result.Message);
        Assert.True(File.Exists(plan.PlanPath));
    }

    [Theory]
    [InlineData("retirement-marker-written")]
    [InlineData("retirement-before-candidate")]
    [InlineData("retirement-removed-candidate")]
    [InlineData("retirement-before-operation")]
    public async Task CancellationRetainsMarkerAndRequiresFreshExplicitResume(string stop)
    {
        var plan = await Plan(); var review = await RetirementInspection(plan);
        var bytes = File.ReadAllBytes(plan.PlanPath); var before = PayloadSnapshot.Capture(Payload, Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var interrupted = Store(point => { if (point == stop) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.RetireAsync(review, review.Kind, RetirementInventory, [], cancel.Token));
        Assert.Equal(bytes, File.ReadAllBytes(plan.PlanPath));
        Assert.True(File.Exists(Path.Combine(State, plan.Id, "retiring.json")));
        Assert.Contains("retirement started", Assert.Single(Store().ReadHistory(Token).Entries).Hint);
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        await Assert.ThrowsAsync<IOException>(() => Store().RetireAsync(review, review.Kind, RetirementInventory, [], Token));
        var resumed = await RetirementInspection(plan);
        Assert.True(resumed.Resuming); Assert.NotEqual(review.Fingerprint, resumed.Fingerprint);
        Assert.True(Directory.Exists(Path.Combine(State, plan.Id)));
        await Store().RetireAsync(resumed, resumed.Kind, RetirementInventory, [], Token);
        Assert.False(Directory.Exists(Path.Combine(State, plan.Id)));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
    }

    [Fact]
    public async Task MidTreeFailureAndUnknownNewContentNeverBecomeResumeAuthority()
    {
        var plan = await Plan(); var review = await RetirementInspection(plan);
        var deleting = 0;
        var interrupted = Store(point => { if (point == "retirement-deleting-entry" && ++deleting == 2) throw new IOException("Injected mid-tree failure"); });
        await Assert.ThrowsAsync<IOException>(() => interrupted.RetireAsync(review, review.Kind, RetirementInventory, [], Token));
        File.WriteAllText(Path.Combine(plan.CandidatePath, "new-content"), "must survive");
        var failure = await Assert.ThrowsAsync<IOException>(() => Store().InspectHistoryAsync(plan.Id, RetirementInventory, [], Token));
        Assert.Contains("partially removed", failure.Message);
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(plan.CandidatePath, "new-content")));
        Assert.True(File.Exists(Path.Combine(State, plan.Id, "retiring.json")));
    }

    [Fact]
    public async Task InventoryCaptureForMutationRunsUnderRootWriter()
    {
        var plan = await Plan(); var review = await RetirementInspection(plan); var captures = 0;
        Task<InventorySnapshot> Capture(CancellationToken _)
        {
            if (++captures >= 2)
            {
                using var state = NativeStateDirectory.OpenOrCreate(State, [], Token, allowCreate: false);
                Assert.Throws<IOException>(() => state.OpenExistingFile("writer.lock", true, Token));
            }
            return Task.FromResult(Inventory());
        }
        await Store().RetireAsync(review, review.Kind, Capture, [], Token);
        Assert.True(captures >= 2);
    }

    [Theory]
    [InlineData("child")]
    [InlineData("target")]
    [InlineData("unknown")]
    [InlineData("unsupported-marker")]
    [InlineData("state")]
    public async Task MarkerResumeNeverBlessesChangedNativeObjectsOrUnknownState(string change)
    {
        var plan = await Plan(); var review = await RetirementInspection(plan);
        var interrupted = Store(point => { if (point == "retirement-marker-written") throw new IOException("Before removal"); });
        await Assert.ThrowsAsync<IOException>(() => interrupted.RetireAsync(review, review.Kind, RetirementInventory, [], Token));
        var marker = Path.Combine(State, plan.Id, "retiring.json");
        if (change == "child")
        {
            var path = Path.Combine(plan.CandidatePath, "script.sh");
            var bytes = File.ReadAllBytes(path); File.Move(path, path + "-held"); File.WriteAllBytes(path, bytes);
        }
        else if (change == "target") File.AppendAllText(Path.Combine(Payload, "extra"), "changed");
        else if (change == "unknown") File.WriteAllText(Path.Combine(State, plan.Id, "unknown"), "preserve");
        else if (change == "unsupported-marker") File.WriteAllText(marker, File.ReadAllText(marker).Replace("\"version\": 2", "\"version\": 99", StringComparison.Ordinal));
        else
        {
            Directory.Move(State, State + "-held"); Directory.CreateDirectory(State);
            File.Copy(Path.Combine(State + "-held", "writer.lock"), Path.Combine(State, "writer.lock"));
            Directory.Move(Path.Combine(State + "-held", plan.Id), Path.Combine(State, plan.Id));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(State, (UnixFileMode)448);
                File.SetUnixFileMode(Path.Combine(State, "writer.lock"), (UnixFileMode)384);
            }
        }
        await Assert.ThrowsAnyAsync<IOException>(() => Store().InspectHistoryAsync(plan.Id, RetirementInventory, [], Token));
        Assert.True(File.Exists(marker)); Assert.True(Directory.Exists(plan.CandidatePath));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AdvancedOwnershipOfLiveCopyDoesNotAuthorizeOrBlockPrivateRetirement(bool readOnly, bool exclusive)
    {
        var plan = await Plan(); var review = await RetirementInspection(plan);
        Task<InventorySnapshot> Capture(CancellationToken _) => Task.FromResult(Inventory() with
        {
            OwnershipRecords = [new SkillPackage("fixture", "native", null, null, null)
            { Provider = "native", InstallationRoot = Skills, Selector = "demo", ReadOnly = readOnly, IsExclusiveOwner = exclusive }]
        });
        var live = PayloadSnapshot.Capture(Payload, Token);
        var current = await Store().InspectHistoryAsync(plan.Id, Capture, [], Token);
        Assert.NotNull(current.Retirement);
        await Store().RetireAsync(current.Retirement, current.Retirement.Kind, Capture, [], Token);
        Assert.False(File.Exists(plan.PlanPath));
        Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
    }

    [Fact]
    public async Task PublicationFailureAfterRootRemovalIsReportedRatherThanSilentSuccess()
    {
        var plan = await Plan(); var review = await RetirementInspection(plan);
        var before = PayloadSnapshot.Capture(Payload, Token);
        var failing = Store(point => { if (point == "retirement-before-root-flush") throw new IOException("Injected publication failure"); });
        await Assert.ThrowsAsync<IOException>(() => failing.RetireAsync(review, review.Kind, RetirementInventory, [], Token));
        Assert.False(Directory.Exists(Path.Combine(State, plan.Id)));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.Empty(Store().ReadHistory(Token).Entries);
    }

    [Fact]
    public async Task MarkerWriteFailurePreservesAllPayloadsAndNeverAuthorizesResume()
    {
        var plan = await Plan(); var review = await RetirementInspection(plan);
        var before = PayloadSnapshot.Capture(plan.CandidatePath, Token);
        var failing = Store(point => { if (point == "retirement-marker-created") throw new IOException("Injected marker write ENOSPC"); });
        await Assert.ThrowsAsync<IOException>(() => failing.RetireAsync(review, review.Kind, RetirementInventory, [], Token));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(plan.CandidatePath, Token).ObjectStateFingerprint);
        Assert.True(File.Exists(plan.PlanPath));
        Assert.Equal(0, new FileInfo(Path.Combine(State, plan.Id, "retiring.json")).Length);
        await Assert.ThrowsAsync<IOException>(() => Store().InspectHistoryAsync(plan.Id, RetirementInventory, [], Token));
        Assert.True(Directory.Exists(plan.CandidatePath));
    }

    [Fact]
    public async Task AliasedRetainedFilesAreProtectedBeforeAnyUnlink()
    {
        if (OperatingSystem.IsWindows()) return;
        var inventory = Inventory();
        var plan = await Store().PlanAsync(Assert.Single(inventory.Skills), inventory, async (directory, token) =>
        {
            await Stage(directory, token);
            var start = new System.Diagnostics.ProcessStartInfo("/bin/ln")
            { WorkingDirectory = directory.CanonicalPath, UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(directory.CanonicalPath, "script.sh"));
            start.ArgumentList.Add(Path.Combine(directory.CanonicalPath, "hard-linked-script"));
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync(token); Assert.Equal(0, process.ExitCode);
        }, Token);
        var before = PayloadSnapshot.Capture(plan.CandidatePath, Token);
        var result = await Store().InspectHistoryAsync(plan.Id, RetirementInventory, [], Token);
        Assert.Null(result.Retirement); Assert.Contains("Hard-linked/aliased", result.Message);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(plan.CandidatePath, Token).ObjectStateFingerprint);
        Assert.False(File.Exists(Path.Combine(State, plan.Id, "retiring.json")));
    }

    [Fact]
    public async Task VersionOneMarkerKeepsExactNativeResumeEvidence()
    {
        var plan = await Plan(); var review = await RetirementInspection(plan);
        var stopped = Store(point => { if (point == "retirement-marker-written") throw new IOException("Retained marker"); });
        await Assert.ThrowsAsync<IOException>(() => stopped.RetireAsync(review, review.Kind, RetirementInventory, [], Token));
        var markerPath = Path.Combine(State, plan.Id, "retiring.json");
        var marker = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(markerPath), RetirementJsonContext.Default.RetirementMarker)!;
        Assert.True(SecureRemovalBackend.TryCaptureIdentity(Skills, out var root, out _));
        marker = marker with { Version = 1, CurrentLive = null, Before = marker.Before with { Root = root } };
        File.WriteAllBytes(markerPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(marker, RetirementJsonContext.Default.RetirementMarker));
        var resumed = await RetirementInspection(plan); Assert.True(resumed.Resuming);
        var live = PayloadSnapshot.Capture(Payload, Token);
        await Store().RetireAsync(resumed, resumed.Kind, RetirementInventory, [], Token);
        Assert.False(File.Exists(plan.PlanPath));
        Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
    }
}
