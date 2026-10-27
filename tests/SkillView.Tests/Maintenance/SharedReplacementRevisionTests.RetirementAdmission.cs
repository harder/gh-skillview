using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class SharedReplacementRevisionTests
{
    private Task<InventorySnapshot> CurrentRetirementInventory(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Inventory()); }

    private async Task<ReplacementResult> ApplyNextRetirementUpdate()
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        var current = File.ReadAllText(Path.Combine(Payload, "SKILL.md"));
        var next = new string('d', 40);
        var result = await Store().PlanUpdateAsync(target, inventory,
            new("https://github.com/harder/fixture", "refs/tags/v3", new('e', 40), next, "skills/demo"),
            (destination, _) => { File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), current.Replace(target.TreeSha!, next).Replace(target.FrontMatter.GithubRef!, "refs/tags/v3") + "Later actual update\n"); return Task.CompletedTask; },
            (destination, _) => { File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), current); return Task.CompletedTask; }, false, Token);
        var applied = Store().Apply(result.Id, result.PlanFingerprint, Inventory(), Token);
        Assert.True(applied.Succeeded, applied.Message);
        return result;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteReviewOrAppliedOperationCanRetireAfterAnActualLaterSavedUpdate(bool applied)
    {
        var first = await Plan();
        if (applied) Assert.True(Store().Apply(first.Id, first.PlanFingerprint, Inventory(), Token).Succeeded);
        var old = Store().Inspect(first.Id, Inventory(), Token);
        var second = await ApplyNextRetirementUpdate();
        var live = PayloadSnapshot.Capture(Payload, Token); var registry = File.ReadAllBytes(Registry);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var consumer, out _));
        Assert.Throws<IOException>(() => Store().Inspect(first.Id, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().Apply(first.Id, first.PlanFingerprint, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().Recover(first.Id, old.ReviewFingerprint, Inventory(), true, Token));
        var current = await Store().InspectHistoryAsync(first.Id, CurrentRetirementInventory, [], Token);
        Assert.NotNull(current.Retirement); Assert.True(current.Retirement.Superseded);
        Assert.Contains("The live copy changed since this operation; Undo was already unavailable.", current.Message);
        Assert.Contains("Undo was already unavailable", current.Retirement.Consequence);
        var stopped = Store(point => { if (point == "retirement-marker-written") throw new IOException("Inspect persisted current live evidence"); });
        await Assert.ThrowsAsync<IOException>(() => stopped.RetireAsync(current.Retirement, current.Retirement.Kind, CurrentRetirementInventory, [], Token));
        var marker = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(State, first.Id, "retiring.json")), RetirementJsonContext.Default.RetirementMarker)!;
        Assert.Equal(2, marker.Version); Assert.NotNull(marker.CurrentLive);
        Assert.Equal(live.RootIdentity, marker.CurrentLive.Identity);
        Assert.True(live.Objects.SequenceEqual(marker.CurrentLive.Children));
        var resumed = (await Store().InspectHistoryAsync(first.Id, CurrentRetirementInventory, [], Token)).Retirement!;
        Assert.True(resumed.Resuming); Assert.True(resumed.Superseded);
        await Store().RetireAsync(resumed, resumed.Kind, CurrentRetirementInventory, [], Token);
        Assert.False(Directory.Exists(Path.Combine(State, first.Id))); Assert.True(File.Exists(second.PlanPath));
        Assert.Equal(registry, File.ReadAllBytes(Registry));
        Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var afterConsumer, out _)); Assert.Equal(consumer, afterConsumer);
    }

    [Fact]
    public async Task AdvancedRegistryAndAddedLiveConsumerDoNotBecomePrivateDeletionAuthority()
    {
        var plan = await Plan(); Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var extraRoot = Directory.CreateDirectory(Path.Combine(Home, ".cursor", "skills")).FullName;
        var extra = Path.Combine(extraRoot, "demo"); Directory.CreateSymbolicLink(extra, Payload);
        File.WriteAllText(Registry, File.ReadAllText(Registry).Replace(NewTree, new string('d', 40)));
        Task<InventorySnapshot> Capture(CancellationToken _) => Task.FromResult(Inventory() with
        {
            Skills = new LocalSkillScanner(new Logger()).Scan([.. Inventory().ScannedRoots, new(extraRoot, Scope.User, "cursor")]).ToImmutableArray(),
            ScannedRoots = [.. Inventory().ScannedRoots, new(extraRoot, Scope.User, "cursor")],
            ConfiguredRoots = [.. Inventory().ConfiguredRoots, new(extraRoot, Scope.User, "cursor")]
        });
        var live = PayloadSnapshot.Capture(Payload, Token); var registry = File.ReadAllBytes(Registry);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(extra, out var link, out _));
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        var result = await Store().InspectHistoryAsync(plan.Id, Capture, [], Token);
        Assert.NotNull(result.Retirement);
        await Store().RetireAsync(result.Retirement, result.Retirement.Kind, Capture, [], Token);
        Assert.Equal(registry, File.ReadAllBytes(Registry));
        Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(extra, out var after, out _)); Assert.Equal(link, after);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingLiveAllowsOnlyProvablyUntouchedReviewAbandonment(bool applied)
    {
        var plan = await Plan(); if (applied) Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var held = Path.Combine(temporary.FullName, "external-live"); Directory.Move(Payload, held);
        var snapshot = PayloadSnapshot.Capture(held, Token); var registry = File.ReadAllBytes(Registry);
        if (applied)
        {
            var error = await Assert.ThrowsAsync<IOException>(() => Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token));
            Assert.Contains("only copy", error.Message); Assert.True(Directory.Exists(plan.RetainedPath));
        }
        else
        {
            var result = await Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token);
            Assert.NotNull(result.Retirement); Assert.Equal("abandon-review", result.Retirement.Kind);
            await Store().RetireAsync(result.Retirement, result.Retirement.Kind, CurrentRetirementInventory, [], Token);
            Assert.False(Directory.Exists(Path.Combine(State, plan.Id)));
        }
        Assert.Equal(registry, File.ReadAllBytes(Registry));
        Assert.Equal(snapshot.ObjectStateFingerprint, PayloadSnapshot.Capture(held, Token).ObjectStateFingerprint);
        Assert.False(Directory.Exists(Payload));
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("retirement-before-original")]
    [InlineData("retirement-deleting-entry")]
    [InlineData("retirement-removed-original")]
    public async Task LiveReplacementOrDisappearanceDuringRetirementStopsBeforeFurtherDeletion(string stop)
    {
        var plan = await Plan(); Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var result = await Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token); Assert.NotNull(result.Retirement);
        var registry = File.ReadAllBytes(Registry); var held = Path.Combine(temporary.FullName, "held-live"); var changed = false; PayloadSnapshot? changedHeld = null; PayloadSnapshot? changedLive = null; SecureLinkIdentity? changedConsumer = null;
        void ChangeLive()
        {
            Directory.Move(Payload, held);
            if (stop is "preview" or "retirement-deleting-entry")
            {
                Directory.CreateDirectory(Payload); File.WriteAllText(Path.Combine(Payload, "SKILL.md"), File.ReadAllText(Path.Combine(held, "SKILL.md")));
            }
            changedHeld = PayloadSnapshot.Capture(held, Token);
            changedLive = Directory.Exists(Payload) ? PayloadSnapshot.Capture(Payload, Token) : null;
            Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var link, out _)); changedConsumer = link;
            changed = true;
        }
        if (stop == "preview") ChangeLive();
        var store = Store(point => { if (point == stop && !changed) ChangeLive(); });
        await Assert.ThrowsAsync<IOException>(() => store.RetireAsync(result.Retirement, result.Retirement.Kind, CurrentRetirementInventory, [], Token));
        Assert.True(changed); Assert.Equal(registry, File.ReadAllBytes(Registry));
        Assert.Equal(changedHeld!.ObjectStateFingerprint, PayloadSnapshot.Capture(held, Token).ObjectStateFingerprint);
        if (changedLive is not null) Assert.Equal(changedLive.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        else Assert.False(Directory.Exists(Payload));
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var afterConsumer, out _)); Assert.Equal(changedConsumer, afterConsumer);
        Assert.True(Directory.Exists(Path.Combine(State, plan.Id, "recorded-base")));
        if (stop != "retirement-removed-original") Assert.True(Directory.Exists(plan.RetainedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentLiveAliasesToPrivateMetadataOrPayloadAreProtected(bool metadata)
    {
        if (OperatingSystem.IsWindows()) return; // Cross-platform native proof uses os.link, including Windows.
        var plan = await Plan(); Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        var source = metadata ? plan.PlanPath : Path.Combine(plan.RetainedPath, "extra");
        var start = new System.Diagnostics.ProcessStartInfo("/bin/ln") { UseShellExecute = false, WorkingDirectory = Home };
        start.ArgumentList.Add(source); start.ArgumentList.Add(Path.Combine(Payload, "private-native-alias"));
        using (var process = System.Diagnostics.Process.Start(start)!) { await process.WaitForExitAsync(Token); Assert.Equal(0, process.ExitCode); }
        var live = PayloadSnapshot.Capture(Payload, Token); var registry = File.ReadAllBytes(Registry);
        ReplacementResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await Store().InspectHistoryAsync(plan.Id, CurrentRetirementInventory, [], Token));
        if (error is not null) Assert.IsAssignableFrom<IOException>(error);
        else { Assert.Null(result!.Retirement); Assert.Contains("Hard-linked/aliased", result.Message); }
        Assert.Equal(registry, File.ReadAllBytes(Registry));
        Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.True(File.Exists(plan.PlanPath)); Assert.True(Directory.Exists(plan.RetainedPath));
        Assert.False(File.Exists(Path.Combine(State, plan.Id, "retiring.json")));
    }

    [Fact]
    public async Task HistoricalConsumerLocatorStillProtectsPrivateDataWhenItsScanRootWasRemoved()
    {
        var plan = await Plan(); Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token).Succeeded);
        Directory.Delete(Exposure); Directory.CreateSymbolicLink(Exposure, plan.RetainedPath);
        Task<InventorySnapshot> Capture(CancellationToken _) => Task.FromResult(Inventory() with
        {
            Skills = new LocalSkillScanner(new Logger()).Scan([new(Skills, Scope.User, null)]).ToImmutableArray(),
            ScannedRoots = [new(Skills, Scope.User, null)],
            ConfiguredRoots = [new(Skills, Scope.User, null)]
        });
        var live = PayloadSnapshot.Capture(Payload, Token); var retained = PayloadSnapshot.Capture(plan.RetainedPath, Token);
        var registry = File.ReadAllBytes(Registry);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var link, out _));
        await Assert.ThrowsAsync<IOException>(() => Store().InspectHistoryAsync(plan.Id, Capture, [], Token));
        Assert.Equal(live.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.Equal(retained.ObjectStateFingerprint, PayloadSnapshot.Capture(plan.RetainedPath, Token).ObjectStateFingerprint);
        Assert.Equal(registry, File.ReadAllBytes(Registry));
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var after, out _)); Assert.Equal(link, after);
    }
}
