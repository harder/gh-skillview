using System.Text.Json;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class ReplacementLocalReviewTests
{
    [Theory]
    [InlineData("retain-moved", "original-retained")]
    [InlineData("install-moved", "installed")]
    [InlineData("displace-moved", "replacement-retained")]
    [InlineData("restore-moved", "restored")]
    public async Task CancellationAtMergedMoveFinishesItsJournalAndRestartCanRestoreExactOriginal(string boundary, string topology)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var original = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Plan();
        HistoricalMergeFixture.Create(State, plan, Token);
        var merged = Store().Inspect(plan.Id, Inventory(), Token);
        var undo = boundary is "displace-moved" or "restore-moved";
        if (undo)
            Assert.True(Store().Apply(plan.Id, merged.PlanFingerprint, Inventory(), Token, "merge-reviewed").Succeeded);

        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var moves = new List<string>();
        var interruptedStore = Store(step =>
        {
            if (!step.EndsWith("-moved", StringComparison.Ordinal)) return;
            moves.Add(step);
            if (step == boundary) canceled.Cancel();
        });
        var result = undo
            ? interruptedStore.Recover(plan.Id, Store().Inspect(plan.Id, Inventory(), Token).ReviewFingerprint, Inventory(), true, canceled.Token)
            : interruptedStore.Apply(plan.Id, merged.PlanFingerprint, Inventory(), canceled.Token, "merge-reviewed");
        Assert.True(result.Canceled);
        Assert.True(result.MutationObserved);
        Assert.Equal(boundary is "install-moved" ? new[] { "retain-moved", boundary }
            : boundary is "restore-moved" ? new[] { "displace-moved", boundary } : new[] { boundary }, moves);
        var journal = Path.Combine(State, plan.Id);
        if (Directory.Exists(Path.Combine(journal, "undo-recovery"))) journal = Path.Combine(journal, "undo-recovery");
        var steps = Directory.GetFiles(journal, "step-*.json").Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(steps);
        using (var last = JsonDocument.Parse(File.ReadAllBytes(steps[^1])))
            Assert.Equal(boundary.Replace("-moved", "-complete", StringComparison.Ordinal), last.RootElement.GetProperty("status").GetString());
        var inspection = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Equal(topology, inspection.Status);
        var restored = Store().Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(restored.Succeeded, restored.Message);
        var actual = PayloadSnapshot.Capture(Payload, Token);
        Assert.Equal(original.Fingerprint, actual.Fingerprint);
        Assert.Equal(original.Objects, actual.Objects);
        Assert.Equal(original.RootUnixMode, actual.RootUnixMode);
        Assert.True(SkillView.Inventory.NativeDirectoryMoveLease.SameObject(original.RootIdentity!.Value, actual.RootIdentity!.Value));
    }
}
