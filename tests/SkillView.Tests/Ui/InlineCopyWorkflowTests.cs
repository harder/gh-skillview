using System.Drawing;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InlineCopyWorkflowTests
{
    internal static InstalledSkill Observed(string path)
    {
        var target = CopiesPresentationTests.Copy(path);
        return target with
        {
            ObservedSubject = OperationSubject.From(target) with
            {
                NativeObject = NativeObjectObservation.From(new(1, (ulong)path.Length, 0, path, true, false, 12, 34), DateTimeOffset.UtcNow, "inventory-observation"),
                ExposureCoverageComplete = true
            }
        };
    }
    internal static (InlineCopyRequest Request, InstalledSkill Reference) Request()
    {
        var target = Observed(Path.GetFullPath("/fixture/selected"));
        var reference = Observed(Path.GetFullPath("/fixture/reference"));
        var snapshot = InventorySnapshot.Empty with { Skills = [target, reference] };
        return (InlineCopyRequest.Capture(snapshot, 7, "context", 11, target, new object())!, reference);
    }
    internal static SkillComparisonResult Equal(InlineCopyRequest request, InstalledSkill reference) =>
        new(reference.InstallationId, request.Subject.InstallationId, "same-source", "same-recorded-revision", "different-contexts",
            "byte-identical", "raw", "raw", [], 0, true, false, "", [])
        {
            LeftRootIdentity = new(1, (ulong)reference.ResolvedPath.Length, 0, reference.ResolvedPath, true, false, 12, 34),
            RightRootIdentity = new(1, (ulong)request.Target.ResolvedPath.Length, 0, request.Target.ResolvedPath, true, false, 12, 34),
            LeftRootUnixMode = 493,
            RightRootUnixMode = 493
        };

    [Fact]
    public void FrozenAddressRejectsMissingAmbiguousAndReconstructedTargets()
    {
        var (request, reference) = Request();
        Assert.True(request.HasExactTarget);
        Assert.Null(InlineCopyRequest.Capture(request.Snapshot with { Skills = [reference] }, 7, "context", 1, request.Target, new object()));
        Assert.Null(InlineCopyRequest.Capture(request.Snapshot with { Skills = [request.Target, request.Target] }, 7, "context", 1, request.Target, new object()));
        Assert.Null(InlineCopyRequest.Capture(request.Snapshot, 7, "context", 1, request.Target with { }, new object()));
        var fact = new CapturedCopyComparison(request, reference, Equal(request, reference), DateTimeOffset.UtcNow);
        Assert.True(fact.Matches(request, reference));
        Assert.False(fact.Matches(request with { Epoch = 12 }, reference));
        Assert.False(fact.Matches(request, reference with { }));
    }

    [Theory]
    [InlineData("partial", "coverage is incomplete")]
    [InlineData("roots", "coverage is incomplete")]
    [InlineData("consumers", "coverage is incomplete")]
    [InlineData("changed-witness", "native roots changed since inventory")]
    [InlineData("changed-witness-raw-different", "native roots changed since inventory")]
    [InlineData("ambiguity", "coverage is incomplete")]
    [InlineData("omitted", "coverage is incomplete")]
    [InlineData("modes", "root permissions differ")]
    [InlineData("unknown-modes", "root permissions differ")]
    [InlineData("aliases", "One physical payload")]
    [InlineData("missing-witness", "independence is unknown")]
    [InlineData("unknown-source", "source/revision unknown")]
    [InlineData("unknown-revision", "source/revision unknown")]
    [InlineData("normalized-equal", "Raw captured payloads differ")]
    [InlineData("different-source", "recorded sources/subpaths differ")]
    [InlineData("different-revision", "incompatible or uncertain")]
    [InlineData("ref-domain", "incompatible or uncertain")]
    [InlineData("incomplete", "Local capture incomplete")]
    [InlineData("complete", "Full ACL/xattr/ADS metadata equivalence is unverified")]
    public void ComparisonGuidanceSeparatesRawNativeSourceModesAndCoverage(string condition, string expected)
    {
        var (request, reference) = Request();
        var result = Equal(request, reference);
        switch (condition)
        {
            case "changed-witness": result = result with { RightRootIdentity = result.RightRootIdentity!.Value with { ChangeTimeSeconds = 999 } }; break;
            case "changed-witness-raw-different": result = result with { RightRootIdentity = result.RightRootIdentity!.Value with { ChangeTimeSeconds = 999 }, RightFingerprint = "different-raw" }; break;
            case "ambiguity": request = request with { Ambiguous = 1 }; break;
            case "omitted": request = request with { Omitted = 1 }; break;
            case "partial": request = request with { Snapshot = request.Snapshot with { Diagnostics = new() { InaccessiblePaths = ["/omitted"] } } }; break;
            case "roots": request = request with { Snapshot = request.Snapshot with { ConfiguredRootsComplete = false } }; break;
            case "consumers": request = request with { Subject = request.Subject with { ExposureCoverageComplete = false } }; break;
            case "modes": result = result with { RightRootUnixMode = 448 }; break;
            case "unknown-modes": result = result with { RightRootUnixMode = null }; break;
            case "aliases": result = result with { RightRootIdentity = result.LeftRootIdentity!.Value with { ChangeTimeSeconds = 999, CanonicalPath = "/another/address" } }; break;
            case "missing-witness": result = result with { RightRootIdentity = null, RightObjectStateFingerprint = "different-hash" }; break;
            case "unknown-source": result = result with { SourceRelationship = "unknown-origin" }; break;
            case "unknown-revision": result = result with { RevisionRelationship = "unknown-recorded-revision" }; break;
            case "normalized-equal": result = result with { PayloadRelationship = "installer-metadata-only", RightFingerprint = "different-raw", DifferingFiles = [], DifferingFileCount = 0 }; break;
            case "different-source": result = result with { SourceRelationship = "different-origins" }; break;
            case "ref-domain": reference = reference with { FrontMatter = reference.FrontMatter with { GithubRef = "refs/heads/v1" } }; break;
            case "different-revision": result = result with { RevisionRelationship = "different-recorded-revisions" }; break;
            case "incomplete": result = result with { Complete = false }; break;
        }
        var fact = new CapturedCopyComparison(request, reference, result, DateTimeOffset.UtcNow);
        Assert.Contains(expected, fact.Guidance);
        Assert.DoesNotContain("safe to remove", fact.Guidance);
        if (condition == "changed-witness-raw-different") Assert.DoesNotContain("Matching captured files", fact.Guidance);
    }

    [Fact]
    public void ExactIntentRejectsSameAddressNativeReplacementAndUnknownEvidence()
    {
        var (request, _) = Request();
        var intent = ExactCopyIntent.Capture(request.Target);
        Assert.Same(request.Target, intent.Resolve(request.Snapshot));
        var changed = request.Target with { ObservedSubject = request.Subject with { NativeObject = request.Subject.NativeObject with { ObjectId = "replacement" } } };
        Assert.Null(intent.Resolve(request.Snapshot with { Skills = [changed] }));
        Assert.Null(ExactCopyIntent.Capture(request.Target with { ObservedSubject = null }).Resolve(request.Snapshot));
    }

    [Fact]
    public void GuidedCursorWaitsForReconciliationAndPreservesCanceledPartialOutcomes()
    {
        var (request, reference) = Request();
        var sequence = new GuidedRepairSequence();
        sequence.Start([Entry(request.Target), Entry(reference)], request.Snapshot, "context");
        var first = sequence.ReviewNext(); Assert.NotNull(first);
        var work = BeginApply(sequence, first, ReviewedUpdatesTabTests.Review(request.Target));
        sequence.CloseReview();
        Assert.Null(sequence.ReviewNext()); sequence.Skip(); Assert.Empty(sequence.Outcomes);
        sequence.WorkSettled(work, UpdateResult.Failure(false, 130, "canceled after native work", []) with
        { Cancelled = true, MutationAttempted = true, ReconciliationPartial = true });
        Assert.Contains("inventory verification incomplete", Assert.Single(sequence.Outcomes).Status);
        var second = sequence.ReviewNext(); Assert.NotNull(second); Assert.NotEqual(first, second);
        sequence.CloseReview();
        Assert.Equal(2, sequence.Outcomes.Length);
        Assert.Null(sequence.ReviewNext());
    }

    [Fact]
    public void NativeInstalledWithPartialVerificationRemainsANativeSuccessObservation()
    {
        var (request, _) = Request(); var target = request.Target;
        var sequence = new GuidedRepairSequence(); sequence.Start([Entry(target)], request.Snapshot, "context");
        var item = sequence.ReviewNext(); Assert.NotNull(item);
        var plan = new ReplacementResult(new('a', 32), "installed", true, true, false, target.ResolvedPath,
            "/state/original", "/state/candidate", "/state/displaced", new('b', 64), new('c', 64), [], "Installed",
            new(target.InstallationId, target.Name, target.Scope, target.Agents, target.FrontMatter, []), "/state/plan.json", 493, 493);
        var work = BeginApply(sequence, item, plan);
        sequence.WorkSettled(work, UpdateResult.Failure(false, 2, "post-action verification unavailable", []) with
        { SavedPlan = plan, MutationAttempted = true, ReconciliationPartial = true });
        var outcome = Assert.Single(sequence.Outcomes);
        Assert.StartsWith("Native replacement installed", outcome.Status);
        Assert.Contains("inventory verification incomplete", outcome.Status);
        Assert.Equal(plan.Id, outcome.SavedOperationId);
    }

    [Fact]
    public void RetiredItemCannotCompleteOrCloseANewerGuidedReview()
    {
        var (request, reference) = Request(); var sequence = new GuidedRepairSequence();
        sequence.Start([Entry(request.Target)], request.Snapshot, "old"); var retired = sequence.ReviewNext()!;
        sequence.Stop(); sequence.Start([Entry(reference)], request.Snapshot, "new"); var current = sequence.ReviewNext()!;
        sequence.Complete(retired, "stale completion"); sequence.CloseReview(retired);
        Assert.Same(current, sequence.Active); Assert.Empty(sequence.Outcomes);
        sequence.Complete(current, "new exact outcome"); Assert.Single(sequence.Outcomes);
    }

    [Fact]
    public void StopDuringCleanupReconciliationKeepsTheCompletedNativeOutcome()
    {
        var (request, reference) = Request(); var sequence = new GuidedRepairSequence();
        sequence.Start([Entry(request.Target), Entry(reference)], request.Snapshot, "context");
        var item = sequence.ReviewNext()!; sequence.CleanupWorkStarted(); sequence.Stop();
        Assert.Same(item, sequence.Active); Assert.Empty(sequence.Outcomes); Assert.Null(sequence.ReviewNext());
        sequence.CompleteWork(item, "Removed 1; inventory verification incomplete");
        Assert.Contains("Removed 1", Assert.Single(sequence.Outcomes).Status);
        Assert.Contains("0 skipped", sequence.Summary); Assert.Contains("1 remaining", sequence.Summary);
        Assert.Null(sequence.ReviewNext());
    }

    private static UpdateReviewWork BeginApply(GuidedRepairSequence sequence, GuidedRepairItem item, ReplacementResult plan)
    {
        Assert.True(sequence.DispatchUpdate(item, item.Entry.Subject));
        sequence.ReviewLoaded(item.Entry.Subject, 1);
        var prepare = new UpdateReviewWork(item.Entry.Subject, null, 1, false);
        sequence.WorkStarted(prepare);
        sequence.WorkSettled(prepare, UpdateResult.Failure(false, 0, "", []) with { SavedPlan = plan });
        var apply = new UpdateReviewWork(item.Entry.Subject, plan.Id, 1, true);
        sequence.WorkStarted(apply);
        return apply;
    }

    [Fact]
    public void GuidedSkipAndStopNeverPrepareRemainingItems()
    {
        var (request, reference) = Request();
        var sequence = new GuidedRepairSequence(); sequence.Start([Entry(request.Target), Entry(reference)], request.Snapshot, "context");
        sequence.Skip(); Assert.StartsWith("Skipped", Assert.Single(sequence.Outcomes).Status);
        sequence.Stop(); Assert.Null(sequence.ReviewNext()); Assert.Contains("1 remaining", sequence.Summary);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void PopupAnchorIsActualControlAndBoundedEvenAtBottomRight(int width, int height)
    {
        var screen = new Rectangle(0, 0, width, height); var size = new Size(35, 8);
        var anchor = new Rectangle(width - 14, height - 2, 13, 1);
        var point = AnchoredActions.Position(anchor, size, screen);
        Assert.True(screen.Contains(new Rectangle(point, size)));
        Assert.Equal(height - 10, point.Y);
        var left = new Rectangle(4, 3, 12, 1);
        Assert.Equal(new Point(4, 4), AnchoredActions.Position(left, size, screen));
    }

    internal static MaintenanceQueueEntry Entry(InstalledSkill target) => new("update-" + target.InstallationId, "upstream-update", target.Name,
        OperationSubject.From(target), [target.ResolvedPath], "explicit", "fixture", "review-update", false, false);
}
