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
    public async Task LegacyFallbackInspectsRealRetainedReviewAttemptWithoutGrantingApproval(bool unreadableNewestPlan)
    {
        var plan = await Plan();
        Assert.Null(plan.SourceReview);
        var selected = HistoricalMergeFixture.CaptureReview(State, plan, Token, "source");
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
        HistoricalMergeFixture.CaptureReview(State, plan, Token);
        var operation = Path.Combine(State, plan.Id);
        var archiveName = kind == "unbound" ? "review-installed-merged-01" : "review-installed-merged-00";
        HistoricalMergeFixture.CreateIncompleteAttempt(State, plan, Token, archiveName);
        var archive = Path.Combine(operation, archiveName);
        if (kind == "corrupt")
        {
            var header = Path.Combine(operation, "local-review", "header.json");
            File.WriteAllText(header, File.ReadAllText(header).Replace(plan.PlanFingerprint, new string('0', 64), StringComparison.Ordinal));
        }
        if (kind is "unknown" or "excess") HistoricalMergeFixture.SeedOperation(State, plan, Token, held =>
        {
            using var attempt = NativeStateDirectory.OpenOrCreate(Path.Combine(held.CanonicalPath, archiveName), [], Token, allowCreate: false);
            using var file = attempt.OpenFile(kind == "unknown" ? "unexpected" : "entry-00000", false, Token);
            if (kind == "unknown") file.Write("No authority"u8);
            else file.SetLength(PayloadSnapshot.MaxBytes + 1);
            file.Flush(true); attempt.FlushNamespace(Token);
        });
        if (kind == "link")
        {
            var target = Path.Combine(temporary.FullName, "external"); File.WriteAllText(target, "No authority");
            File.CreateSymbolicLink(Path.Combine(archive, "entry-00000"), target);
        }
        Assert.Throws<IOException>(() => Store(sourceReviewRequired: true).Inspect(plan.Id, Inventory(), Token));
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

}
