using System.Text.Json;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class SharedReplacementRevisionTests
{
    private async Task<ReplacementResult> LocalMergePlan()
    {
        var plan = await Plan();
        var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var chosen = Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint, [new("SKILL.md", "installed")], Inventory(), Token);
        Assert.True(chosen.CanMaterialize, string.Join(" ", chosen.MetadataRefusals));
        return Store().FinalizeLocalChanges(plan.Id, chosen.ReviewFingerprint, Inventory(), Token);
    }

    [Fact]
    public async Task SharedLocalMergeKeepsConsumerLinkUpdatesRealSourceOwnerAndUndoPreservesFreshUnrelatedBytes()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var original = PayloadSnapshot.Capture(Payload, Token);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var link, out var error), error);
        var selected = Selected();
        var plan = await LocalMergePlan();
        Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
        var applied = Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "merge-reviewed");
        Assert.True(applied.Succeeded, applied.Message); Assert.Equal("installed", applied.Status);
        Assert.Equal("Keep local extra", File.ReadAllText(Path.Combine(Payload, "extra")));
        Assert.Contains("Local instructions", File.ReadAllText(Path.Combine(Payload, "SKILL.md")));
        using (var document = JsonDocument.Parse(Selected()))
            Assert.Equal(NewTree, document.RootElement.GetProperty("skillFolderHash").GetString());
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var after, out error), error);
        Assert.Equal(link, after);
        File.WriteAllText(Registry, File.ReadAllText(Registry).Replace("\"extension\":[1,2]", "\"extension\":[1,2,3],\"fresh\":true"));
        var inspect = Store().Inspect(plan.Id, Inventory(), Token);
        var undone = Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(undone.Succeeded, undone.Message); Assert.Equal("restored", undone.Status);
        Assert.Equal(selected, Selected()); Assert.Contains("\"fresh\":true", File.ReadAllText(Registry));
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out after, out error), error);
        Assert.Equal(link, after);
    }

    [Theory]
    [InlineData("install-moved")]
    [InlineData("owner-written")]
    public async Task SharedMergedRecoveryCompletesOwnerTransitionFromExactSavedSource(string stop)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var plan = await LocalMergePlan();
        var interrupted = Store(step => { if (step == stop) throw new IOException("interrupt merged owner"); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "merge-reviewed");
        Assert.False(interrupted.Succeeded); Assert.True(interrupted.MutationObserved);
        var inspection = Store().Inspect(plan.Id, Inventory(), Token);
        var result = Store().Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), false, Token);
        Assert.True(result.Succeeded, result.Message); Assert.Equal("installed", result.Status);
        using var document = JsonDocument.Parse(Selected());
        Assert.Equal(NewTree, document.RootElement.GetProperty("skillFolderHash").GetString());
        Assert.Equal("Keep local extra", File.ReadAllText(Path.Combine(Payload, "extra")));
    }
}
