using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class SharedReplacementRevisionTests
{
    [Theory]
    [InlineData("ready")]
    [InlineData("installed")]
    [InlineData("restored")]
    public async Task RetirementNeverChangesSharedRegistryOrConsumerObjects(string topology)
    {
        var plan = await Plan();
        if (topology != "ready") Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        if (topology == "restored") Assert.True(Store().Recover(plan.Id, Store().Inspect(plan.Id, Inventory(), Token).ReviewFingerprint, Inventory(), true, Token).Succeeded);
        var registry = File.ReadAllBytes(Registry);
        var before = PayloadSnapshot.Capture(Payload, Token);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var exposure, out _));
        Task<InventorySnapshot> Capture(CancellationToken _) => Task.FromResult(Inventory());
        var result = await Store().InspectHistoryAsync(plan.Id, Capture, [], Token);
        var review = result.Retirement;
        Assert.NotNull(review);
        await Store().RetireAsync(review, review.Kind, Capture, [], Token);
        Assert.Equal(registry, File.ReadAllBytes(Registry));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var after, out _));
        Assert.Equal(exposure, after);
        Assert.False(Directory.Exists(Path.Combine(State, plan.Id)));
    }

    [Fact]
    public async Task UnfinishedRawOwnerIntentProtectsEvenAnInstalledTerminalPayload()
    {
        var plan = await Plan();
        Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var registry = File.ReadAllBytes(Registry);
        var before = PayloadSnapshot.Capture(Payload, Token);
        using (var journal = NativeStateDirectory.OpenOrCreate(Path.Combine(State, plan.Id, "owner-journal"), [], Token, allowCreate: false))
        using (var writer = journal.OpenExistingFile("writer.lock", true, Token))
        {
            using (var incomplete = journal.OpenFile("registry-" + Guid.NewGuid().ToString("N") + "-intent.json", false, Token))
            { incomplete.WriteByte((byte)'{'); incomplete.Flush(true); }
            journal.FlushPublication(Token);
        }
        Task<InventorySnapshot> Capture(CancellationToken _) => Task.FromResult(Inventory());
        var result = await Store().InspectHistoryAsync(plan.Id, Capture, [], Token);
        Assert.Equal("installed", result.Status); Assert.Null(result.Retirement);
        Assert.Contains("Unfinished owner journal", result.Message);
        Assert.Equal(registry, File.ReadAllBytes(Registry));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.True(File.Exists(plan.PlanPath));
        Assert.False(File.Exists(Path.Combine(State, plan.Id, "retiring.json")));
    }
}
