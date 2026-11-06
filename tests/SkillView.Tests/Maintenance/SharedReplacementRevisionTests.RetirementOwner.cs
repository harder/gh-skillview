using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class SharedReplacementRevisionTests
{
    [Theory]
    [InlineData("install-moved", false)]
    [InlineData("owner-publish-reviewed", false)]
    [InlineData("restore-moved", true)]
    public async Task OwnUnpublishedTransitionProtectsRetainedEvidenceAndOrdinaryRecoveryStillWorks(string stop, bool undo)
    {
        var plan = await Plan();
        if (undo) Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var registryBeforeTransition = File.ReadAllBytes(Registry);
        var stopped = Store(point =>
        {
            if (point != stop) return;
            if (stop == "owner-publish-reviewed")
            {
                AppendRetirementRegistryByte(Registry);
            }
            else throw new IOException("Stop immediately after payload move, before owner publication");
        });
        var interrupted = undo ? stopped.Recover(plan.Id, Store().Inspect(plan.Id, Inventory(), Token).ReviewFingerprint, Inventory(), true, Token)
            : stopped.Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.False(interrupted.Succeeded);
        if (stop == "owner-publish-reviewed") Assert.True(interrupted.Message.Contains("Registry changed after transition review", StringComparison.Ordinal), interrupted.Message);
        else Assert.Equal(registryBeforeTransition, File.ReadAllBytes(Registry));
        var registry = File.ReadAllBytes(Registry); var live = PayloadSnapshot.Capture(Payload, Token);
        var retainedPath = Path.Combine(State, plan.Id, undo ? "displaced" : "original");
        var retained = PayloadSnapshot.Capture(retainedPath, Token); var baseline = PayloadSnapshot.Capture(Path.Combine(State, plan.Id, "recorded-base"), Token);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var consumer, out _));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token);
            Assert.True(result.Retirement is null, result.Message);
            Assert.True(result.Message.Contains("owner transition is still pending", StringComparison.Ordinal), result.Message);
        }
        Assert.Equal(registry, File.ReadAllBytes(Registry));
        Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.Equal(retained.ObjectStateFingerprint, PayloadSnapshot.Capture(retainedPath, Token).ObjectStateFingerprint);
        Assert.Equal(baseline.ObjectStateFingerprint, PayloadSnapshot.Capture(Path.Combine(State, plan.Id, "recorded-base"), Token).ObjectStateFingerprint);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var afterConsumer, out _)); Assert.Equal(consumer, afterConsumer);
        Assert.False(File.Exists(Path.Combine(State, plan.Id, "retiring.json")));
        var reviewed = Store().Inspect(plan.Id, Inventory(), Token);
        var recovered = Store().Recover(plan.Id, reviewed.ReviewFingerprint, Inventory(), undo, Token);
        Assert.True(recovered.Succeeded, recovered.Message);
        Assert.NotNull((await Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token)).Retirement);
    }

    [Fact]
    public void RegistryAndOwnedParentWitnessesKeepSeparateCaptureDomains()
    {
        using var parent = OwnedDirectoryLease.Open(Path.GetDirectoryName(Registry)!);
        var before = parent.Capture(Token).Identity;
        using (var registry = NativeRegistryFileLease.Open(Registry, Token))
        {
            var snapshot = registry.Read(Token);
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(OriginalRegistry), snapshot.Bytes);
            Assert.Equal(before with { DirectoryFingerprint = null }, registry.ParentIdentity with { DirectoryFingerprint = null });
            if (OperatingSystem.IsWindows())
            {
                Assert.NotNull(before.DirectoryFingerprint);
                Assert.NotNull(registry.ParentIdentity.DirectoryFingerprint);
                Assert.NotEqual(before.DirectoryFingerprint, registry.ParentIdentity.DirectoryFingerprint);
            }
            Assert.Equal(before, parent.Capture(Token).Identity);
            Assert.Equal(snapshot.Identity, registry.Read(Token).Identity);
            Assert.Throws<IOException>(() => NativeRegistryFileLease.Open(Registry, Token));
        }
        Assert.Equal(before, parent.Capture(Token).Identity);
        using var reopened = NativeRegistryFileLease.Open(Registry, Token);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(OriginalRegistry), reopened.Read(Token).Bytes);
    }

    [Fact]
    public async Task PendingOwnerFallbackRetainsEvidenceWhileTheRealRegistryLockIsHeld()
    {
        var plan = await Plan();
        Assert.False(Store(point => { if (point == "install-moved") throw new IOException("Before owner intent"); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var inventory = Inventory();
        Task<SkillView.Inventory.Models.InventorySnapshot> Capture(CancellationToken _) => Task.FromResult(inventory);
        var live = PayloadSnapshot.Capture(Payload, Token); var retained = PayloadSnapshot.Capture(plan.RetainedPath, Token);
        using (var externalOwner = NativeRegistryFileLease.Open(Registry, Token))
        {
            var registry = externalOwner.Read(Token);
            var blocked = await Store().InspectHistoryAsync(plan.Id, Capture, [], Token);
            Assert.True(blocked.Retirement is null, blocked.Message);
            Assert.True(blocked.Message.Contains("locked", StringComparison.OrdinalIgnoreCase), blocked.Message);
            Assert.Equal(registry.Identity, externalOwner.Read(Token).Identity);
            Assert.Equal(registry.Bytes, externalOwner.Read(Token).Bytes);
            Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
            Assert.Equal(retained.ObjectStateFingerprint, PayloadSnapshot.Capture(plan.RetainedPath, Token).ObjectStateFingerprint);
        }
        var pending = await Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token);
        Assert.True(pending.Retirement is null, pending.Message);
        Assert.True(pending.Message.Contains("owner transition is still pending", StringComparison.Ordinal), pending.Message);
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        var recovered = Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), false, Token);
        Assert.True(recovered.Succeeded, recovered.Message);
        var terminal = await Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token);
        Assert.True(terminal.Retirement is not null, terminal.Message);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("retirement-before-original")]
    [InlineData("resume")]
    public async Task RegistryFallbackIsRecheckedAtApprovalSlotsAndMarkerResume(string stop)
    {
        var plan = await Plan();
        Assert.False(Store(point => { if (point == "install-moved") throw new IOException("Before owner intent"); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var pending = File.ReadAllBytes(Registry);
        File.WriteAllText(Registry, OriginalRegistry.Replace(OldTree, new string('d', 40)));
        var result = await Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token);
        Assert.True(result.Retirement is not null, result.Message);
        if (stop == "resume")
            await Assert.ThrowsAsync<IOException>(() => Store(point => { if (point == "retirement-marker-written") throw new IOException("Stop at marker"); })
                .RetireAsync(result.Retirement, result.Retirement.Kind, CurrentRetirementInventory, [], Token));
        var live = PayloadSnapshot.Capture(Payload, Token); var retained = PayloadSnapshot.Capture(plan.RetainedPath, Token);
        if (stop != "retirement-before-original") File.WriteAllBytes(Registry, pending);
        if (stop == "resume")
            await Assert.ThrowsAsync<IOException>(() => Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token));
        else
            await Assert.ThrowsAsync<IOException>(() => Store(point => { if (point == stop) File.WriteAllBytes(Registry, pending); })
                .RetireAsync(result.Retirement, result.Retirement.Kind, CurrentRetirementInventory, [], Token));
        Assert.Equal(pending, File.ReadAllBytes(Registry));
        Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.Equal(retained.ObjectStateFingerprint, PayloadSnapshot.Capture(plan.RetainedPath, Token).ObjectStateFingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvenCompletedOwnerTransitionPermitsLaterRegistryReversion(bool undo)
    {
        var plan = await Plan(); Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var desired = File.ReadAllBytes(Registry);
        if (undo) Assert.True(Store().Recover(plan.Id, Store().Inspect(plan.Id, Inventory(), Token).ReviewFingerprint, Inventory(), true, Token).Succeeded);
        File.WriteAllBytes(Registry, undo ? desired : System.Text.Encoding.UTF8.GetBytes(OriginalRegistry));
        var registry = File.ReadAllBytes(Registry); var live = PayloadSnapshot.Capture(Payload, Token);
        var result = await Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token);
        Assert.True(result.Retirement is not null, result.Message);
        await Store().RetireAsync(result.Retirement, result.Retirement.Kind, CurrentRetirementInventory, [], Token);
        Assert.False(Directory.Exists(Path.Combine(State, plan.Id)));
        Assert.Equal(registry, File.ReadAllBytes(Registry));
        Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
    }

    [Theory]
    [InlineData("satisfied", true)]
    [InlineData("foreign", true)]
    [InlineData("absent-entry", true)]
    [InlineData("absent-file", true)]
    [InlineData("torn", false)]
    [InlineData("duplicate", false)]
    [InlineData("unsupported", false)]
    public async Task MissingLocalCompletionUsesOnlyBoundedReadableCurrentRegistryEvidence(string state, bool eligible)
    {
        var plan = await Plan();
        Assert.False(Store(point => { if (point == "install-moved") throw new IOException("No owner evidence"); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var transition = plan.Revision!.Owner!;
        if (state == "satisfied") File.WriteAllBytes(Registry, GhRegistryEditor.ReplaceEntry(File.ReadAllBytes(Registry), "demo", transition.BeforeFingerprint, transition.DesiredEntry, Token).DesiredBytes);
        else if (state == "foreign") File.WriteAllText(Registry, OriginalRegistry.Replace(OldTree, new string('d', 40)));
        else if (state == "absent-entry") File.WriteAllText(Registry, "{\"version\":3,\"skills\":{}}");
        else if (state == "absent-file") File.Delete(Registry);
        else if (state == "duplicate") File.WriteAllText(Registry, "{\"version\":3,\"skills\":{},\"skills\":{}}");
        else if (state == "unsupported") File.WriteAllText(Registry, "{\"version\":4,\"skills\":{}}");
        else File.WriteAllText(Registry, "{broken");
        var registry = File.Exists(Registry) ? File.ReadAllBytes(Registry) : null;
        var live = PayloadSnapshot.Capture(Payload, Token); var retained = PayloadSnapshot.Capture(plan.RetainedPath, Token);
        // Inventory coverage itself also protects unreadable registries. The
        // fallback parser is exercised with a complete supplied fixture snapshot.
        var snapshot = Inventory() with { OwnershipReadDiagnostics = [], OwnershipRecords = [], Providers = [] };
        Task<SkillView.Inventory.Models.InventorySnapshot> Capture(CancellationToken _) => Task.FromResult(snapshot);
        var result = await Store().InspectHistoryAsync(plan.Id, Capture, [], Token);
        Assert.True(eligible == (result.Retirement is not null), $"{state}: expected retirement={eligible}. {result.Message}");
        if (!eligible)
        {
            var reason = state switch { "torn" => "Unreadable or torn owner registry", "duplicate" => "Ambiguous owner registry", _ => "Unsupported owner registry" };
            Assert.True(result.Message.Contains(reason, StringComparison.Ordinal), result.Message);
        }
        Assert.Equal(registry, File.Exists(Registry) ? File.ReadAllBytes(Registry) : null);
        Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.Equal(retained.ObjectStateFingerprint, PayloadSnapshot.Capture(plan.RetainedPath, Token).ObjectStateFingerprint);
        Assert.False(File.Exists(Path.Combine(State, plan.Id, "retiring.json")));
    }

    [Fact]
    public async Task EqualOwnerTransitionNeedsNoPublicationProofAfterPayloadMoves()
    {
        var plan = await EqualPlan();
        using (var operation = NativeStateDirectory.OpenOrCreate(Path.GetDirectoryName(plan.PlanPath)!, [], Token))
        using (var original = NativeDirectoryMoveLease.Open(Skills, Payload, Token))
            operation.ReceiveDirectory(original, "original", Token);
        var reviewed = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.True(Store().Recover(plan.Id, reviewed.ReviewFingerprint, Inventory(), false, Token).Succeeded);
        var result = await Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token);
        Assert.True(result.Retirement is not null, result.Message);
        var registry = File.ReadAllBytes(Registry);
        await Store().RetireAsync(result.Retirement, result.Retirement.Kind, CurrentRetirementInventory, [], Token);
        Assert.Equal(registry, File.ReadAllBytes(Registry));
    }
    // Fixture adversary: bypass advisory .NET/gh locks deliberately, append
    // outside Windows' first-byte range lock, and keep the same file object.
    private static void AppendRetirementRegistryByte(string path)
    {
        using var handle = OperatingSystem.IsWindows()
            ? OpenRetirementRegistryWindows(path, 0x40000000, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero)
            : new SafeFileHandle(new IntPtr(OpenRetirementRegistryUnix(path, 1)), ownsHandle: true);
        if (handle.IsInvalid) throw new IOException("Fixture registry adversary open failed: " + Marshal.GetLastPInvokeError());
        using var change = new FileStream(handle, FileAccess.Write, bufferSize: 1);
        change.Position = change.Length; change.WriteByte(32); change.Flush(true);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenRetirementRegistryUnix(string path, int flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenRetirementRegistryWindows(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

}
