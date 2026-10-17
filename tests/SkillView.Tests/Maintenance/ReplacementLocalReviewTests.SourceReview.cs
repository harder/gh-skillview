using System.Text;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class ReplacementLocalReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyFallbackInspectsAndReopensRealRetainedReviewAttemptWithoutGrantingApproval(bool unreadableNewestPlan)
    {
        var plan = await Plan();
        Assert.Null(plan.SourceReview);
        var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var selected = Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint, [new("SKILL.md", "source")], Inventory(), Token);
        var original = PayloadSnapshot.Capture(Payload, Token);
        // Reproduce the exact durable prefix of a canceled first capture using
        // real immutable archive copies. This does not fabricate publication
        // authority on hosts that correctly refuse full metadata materialization.
        using (var state = NativeStateDirectory.OpenOrCreate(State, [], Token, allowCreate: false))
        using (var writer = state.OpenExistingFile("writer.lock", true, Token))
        using (var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(State, plan.Id), [], Token, allowCreate: false))
        using (var operationWriter = operation.OpenExistingFile("writer.lock", true, Token))
        {
            using (var attempt = operation.CreateStagingDirectory("merged-00", Token)) attempt.SealStagingDirectory(493, Token);
            using var captured = NativeStateDirectory.OpenOrCreate(Path.Combine(selected.ReviewPath, "installed"), [], Token, allowCreate: false);
            var fingerprint = CapturedPayloadArchive.Load(captured, null, Token).Fingerprint;
            CapturedPayloadArchive.CopyInto(captured, operation, "review-installed-merged-00", fingerprint, Token);
            // Capture can be canceled immediately after creating its directory.
            using (var partial = operation.CreateStagingDirectory("review-source-merged-00", Token)) partial.FlushPublication(Token);
            if (unreadableNewestPlan)
            {
                using var incomplete = operation.OpenFile("merge-plan-00.json", false, Token);
                incomplete.Write(Encoding.UTF8.GetBytes("{")); incomplete.Flush(true);
                operation.FlushPublication(Token);
            }
        }
        foreach (var current in new[] { Store(), Store(sourceReviewRequired: true) })
        {
            var inspection = current.Inspect(plan.Id, Inventory(), Token);
            Assert.Equal("ready", inspection.Status); Assert.Null(inspection.SourceReview); Assert.False(inspection.SourceReviewAcknowledged);
            Assert.Equal(selected.ReviewFingerprint, current.ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token).ReviewFingerprint);
            Assert.Throws<IOException>(() => current.Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
            Assert.Throws<IOException>(() => current.Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), false, Token));
        }
        Assert.Equal(original.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("unbound")]
    [InlineData("unknown")]
    [InlineData("excess")]
    [InlineData("link")]
    public async Task LegacyFallbackRefusesUnboundUnknownExcessAndLinkedAttemptArchives(string kind)
    {
        var plan = await Plan();
        Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var operation = Path.Combine(State, plan.Id);
        Directory.CreateDirectory(Path.Combine(operation, "merged-00"));
        var archive = Path.Combine(operation, kind == "unbound" ? "review-installed-merged-01" : "review-installed-merged-00");
        Directory.CreateDirectory(archive);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(archive, (UnixFileMode)448);
        if (kind == "corrupt")
        {
            var header = Path.Combine(operation, "local-review", "header.json");
            File.WriteAllText(header, File.ReadAllText(header).Replace(plan.PlanFingerprint, new string('0', 64), StringComparison.Ordinal));
        }
        if (kind == "unknown") File.WriteAllText(Path.Combine(archive, "unexpected"), "No authority");
        if (kind == "excess")
        {
            using var oversized = File.Create(Path.Combine(archive, "entry-00000")); oversized.SetLength(PayloadSnapshot.MaxBytes + 1);
        }
        if (kind == "link")
        {
            var target = Path.Combine(temporary.FullName, "external"); File.WriteAllText(target, "No authority");
            File.CreateSymbolicLink(Path.Combine(archive, "entry-00000"), target);
        }
        Assert.Throws<IOException>(() => Store(sourceReviewRequired: true).Inspect(plan.Id, Inventory(), Token));
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    public static bool SupportsNativeMergePublication => OperatingSystem.IsMacOS();
    [Fact(Skip = "Full native merge metadata publication proof requires supported host coverage.", SkipUnless = nameof(SupportsNativeMergePublication))]
    public async Task CanceledLegacyFinalizeAfterInstalledArchiveRetriesIntoFreshReviewedAttempt()
    {
        var plan = await Plan(); var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var selected = Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint, [new("SKILL.md", "source")], Inventory(), Token);
        Assert.True(selected.CanMaterialize, string.Join("; ", selected.MetadataRefusals));
        var original = PayloadSnapshot.Capture(Payload, Token);
        Assert.Throws<OperationCanceledException>(() => Store(step =>
        { if (step == "merge-source-review-installed-captured") throw new OperationCanceledException("capture canceled"); }, true)
            .FinalizeLocalChanges(plan.Id, selected.ReviewFingerprint, Inventory(), Token));
        Assert.Equal("ready", Store().Inspect(plan.Id, Inventory(), Token).Status);
        var reopened = Store(sourceReviewRequired: true).ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var merged = Store(sourceReviewRequired: true).FinalizeLocalChanges(plan.Id, reopened.ReviewFingerprint, Inventory(), Token);
        Assert.EndsWith("merged-01", merged.CandidatePath); Assert.NotNull(merged.SourceReview);
        Assert.False(merged.SourceReviewAcknowledged);
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, merged.PlanFingerprint, Inventory(), Token, "merge-reviewed"));
        Assert.Equal(original.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
    }
}
