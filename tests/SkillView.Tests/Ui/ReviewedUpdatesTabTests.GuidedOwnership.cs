using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class ReviewedUpdatesTabTests
{
    private static GuidedRepairSequence Guided(InstalledSkill first, InstalledSkill next)
    {
        var sequence = new GuidedRepairSequence();
        sequence.Start([InlineCopyWorkflowTests.Entry(first), InlineCopyWorkflowTests.Entry(next)],
            InventorySnapshot.Empty with { Skills = [first, next] }, "context");
        var item = sequence.ReviewNext()!;
        Assert.True(sequence.DispatchUpdate(item, item.Entry.Subject));
        return sequence;
    }
    private static void BindGuided(UpdatesTabView view, GuidedRepairSequence sequence)
    {
        view.ReviewLoaded = sequence.ReviewLoaded;
        view.ReviewWorkStarted = sequence.WorkStarted;
        view.ReviewWorkSettled = sequence.WorkSettled;
        view.ReviewSkipped = sequence.ReviewSkipped;
    }

    [Fact]
    public async Task LoadingAnotherSubjectSkipsTheUnstartedGuidedReviewWithoutAttributingItsWork()
    {
        var x = Target("first"); var y = Target("second"); var next = Target("next");
        var sequence = Guided(x, next); var calls = new List<string>();
        using var view = View([x, y, next], (_, options, _) =>
        { calls.Add("prepare Y"); return Task.FromResult(Result(Review(Assert.Single(options.Targets!)))); },
            (plan, _, _) => { calls.Add("apply Y"); return Task.FromResult(Result(plan with { Status = "installed", MutationObserved = true }, true)); });
        BindGuided(view, sequence);
        await view.LoadForSubjectAsync(OperationSubject.From(x));
        await view.LoadForSubjectAsync(OperationSubject.From(y));
        var skipped = Assert.Single(sequence.Outcomes);
        Assert.Equal(x.ResolvedPath, skipped.Path); Assert.Equal("Skipped: another review replaced it", skipped.Status);
        Assert.Null(skipped.SavedOperationId);
        await view.ReviewUpdateAsync(); view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        Assert.Equal(new[] { "prepare Y", "apply Y" }, calls);
        Assert.Equal(skipped, Assert.Single(sequence.Outcomes));
        Assert.Equal(next.ResolvedPath, sequence.ReviewNext()!.Entry.Subject.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingTheViewCannotDiscardMatchingInFlightNativeOutcomeOrSettleItFromY(bool stop)
    {
        var x = Target("first"); var y = Target("second"); var next = Target("next");
        var sequence = Guided(x, next); var originalItem = sequence.Active;
        var held = new TaskCompletionSource<SkillView.Gh.Models.UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var view = View([x, y, next], (_, options, _) => Task.FromResult(Result(Review(Assert.Single(options.Targets!)))),
            (plan, _, _) => plan.Subject.InstallationId == x.InstallationId ? held.Task
                : Task.FromResult(Result(plan with { Status = "installed", MutationObserved = true }, true)));
        BindGuided(view, sequence);
        await view.LoadForSubjectAsync(OperationSubject.From(x)); await view.ReviewUpdateAsync();
        Assert.Empty(sequence.Outcomes); Assert.Null(sequence.ReviewNext());
        view.RetainBoxForTests.Value = CheckState.Checked; var applyingX = view.ApplyReviewForTestsAsync();
        Assert.Contains("waiting for reconciliation", sequence.Summary);
        view.CancelPendingWork(); await view.LoadForSubjectAsync(OperationSubject.From(y));
        await view.ReviewUpdateAsync(); view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        if (stop) sequence.Stop();
        Assert.Same(originalItem, sequence.Active); Assert.Empty(sequence.Outcomes); Assert.Null(sequence.ReviewNext());
        held.SetResult(Result(Review(x) with { Status = "installed", MutationObserved = true }, true) with { ReconciliationPartial = true });
        await applyingX;
        var outcome = Assert.Single(sequence.Outcomes);
        Assert.Equal(x.ResolvedPath, outcome.Path); Assert.Equal(Review(x).Id, outcome.SavedOperationId);
        Assert.NotEqual(Review(y).Id, outcome.SavedOperationId); Assert.StartsWith("Native replacement installed", outcome.Status);
        Assert.Contains("inventory verification incomplete", outcome.Status);
        if (stop) { Assert.Null(sequence.ReviewNext()); Assert.Contains("1 remaining", sequence.Summary); }
        else Assert.Equal(next.ResolvedPath, sequence.ReviewNext()!.Entry.Subject.Path);
    }

    [Fact]
    public void ExactSubjectPlanLoadAndOperationInstanceAllMustMatchBeforeSettlement()
    {
        var x = Target("first"); var y = Target("second"); var next = Target("next");
        var subject = OperationSubject.From(x); var plan = Review(x); var sequence = Guided(x, next);
        sequence.ReviewLoaded(subject, 2);
        var prepare = new UpdateReviewWork(subject, null, 2, false); sequence.WorkStarted(prepare);
        sequence.WorkSettled(prepare with { }, Result(plan)); // Same values, retired/different operation instance.
        sequence.WorkSettled(prepare, Result(Review(y)));
        Assert.Contains("waiting for reconciliation", sequence.Summary); Assert.Empty(sequence.Outcomes);
        sequence.WorkSettled(prepare, Result(plan));
        sequence.ReviewLoaded(OperationSubject.From(y), 1); // A stale load cannot replace the active review.
        Assert.NotNull(sequence.Active);
        var wrongPlan = new UpdateReviewWork(subject, Review(y).Id, 2, true);
        sequence.WorkStarted(wrongPlan); sequence.WorkSettled(wrongPlan, Result(plan, true));
        sequence.WorkStarted(new(subject with { Path = "/same-id/different-path" }, plan.Id, 2, true));
        sequence.WorkStarted(new(subject with { InstallationId = "other-id" }, plan.Id, 2, true));
        sequence.WorkStarted(new(subject, plan.Id, 1, true));
        Assert.DoesNotContain("waiting for reconciliation", sequence.Summary); Assert.Empty(sequence.Outcomes);
        var applying = new UpdateReviewWork(subject, plan.Id, 2, true); sequence.WorkStarted(applying);
        sequence.WorkSettled(prepare, Result(plan));
        sequence.WorkSettled(applying with { Subject = subject with { Path = "/same-id/different-path" } }, Result(plan, true));
        sequence.WorkSettled(applying, Result(plan with { Id = Review(y).Id, Status = "installed", MutationObserved = true }, true));
        sequence.ReviewSkipped(subject, Review(y).Id, 2);
        Assert.Empty(sequence.Outcomes); Assert.Null(sequence.ReviewNext());
        sequence.WorkSettled(applying, Result(plan with { Status = "installed", MutationObserved = true }, true));
        Assert.Equal(plan.Id, Assert.Single(sequence.Outcomes).SavedOperationId);
        var nextItem = sequence.ReviewNext()!; Assert.True(sequence.DispatchUpdate(nextItem, nextItem.Entry.Subject));
        sequence.ReviewLoaded(nextItem.Entry.Subject, 3);
        sequence.WorkStarted(prepare); sequence.WorkSettled(applying, Result(plan, true));
        Assert.Same(nextItem, sequence.Active); Assert.Single(sequence.Outcomes);
        Assert.DoesNotContain("waiting for reconciliation", sequence.Summary);
    }

    [Fact]
    public async Task ReplacedInFlightPreparationRetainsOnlyItsOwnLaterSavedCandidate()
    {
        var x = Target("first"); var y = Target("second"); var next = Target("next");
        var sequence = Guided(x, next);
        var held = new TaskCompletionSource<SkillView.Gh.Models.UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var view = View([x, y, next], (_, options, _) => Assert.Single(options.Targets!).InstallationId == x.InstallationId
            ? held.Task : Task.FromResult(Result(Review(y))), (_, _, _) => throw new InvalidOperationException("No apply submitted"));
        BindGuided(view, sequence); await view.LoadForSubjectAsync(OperationSubject.From(x)); var preparingX = view.ReviewUpdateAsync();
        view.CancelPendingWork(); await view.LoadForSubjectAsync(OperationSubject.From(y)); await view.ReviewUpdateAsync();
        Assert.Empty(sequence.Outcomes); Assert.Null(sequence.ReviewNext());
        held.SetResult(Result(Review(x))); await preparingX;
        var outcome = Assert.Single(sequence.Outcomes);
        Assert.StartsWith("Skipped: another review replaced it", outcome.Status);
        Assert.Equal(x.ResolvedPath, outcome.Path); Assert.Equal(Review(x).Id, outcome.SavedOperationId);
        Assert.Equal(next.ResolvedPath, sequence.ReviewNext()!.Entry.Subject.Path);
    }

    [Fact]
    public void ReopenedSameSubjectRejectsItsRetiredLoadAndWork()
    {
        var x = Target("first"); var next = Target("next"); var subject = OperationSubject.From(x);
        var sequence = Guided(x, next); sequence.ReviewLoaded(subject, 1);
        var retired = new UpdateReviewWork(subject, null, 1, false);
        sequence.Stop();
        sequence.Start([InlineCopyWorkflowTests.Entry(x)], InventorySnapshot.Empty with { Skills = [x] }, "context");
        var current = sequence.ReviewNext()!; Assert.True(sequence.DispatchUpdate(current, subject)); sequence.ReviewLoaded(subject, 2);
        sequence.WorkStarted(retired); sequence.WorkSettled(retired, Result(Review(x)));
        Assert.Same(current, sequence.Active); Assert.Empty(sequence.Outcomes);
        Assert.DoesNotContain("waiting for reconciliation", sequence.Summary);
        var fresh = new UpdateReviewWork(subject, null, 2, false); sequence.WorkStarted(fresh);
        Assert.Contains("waiting for reconciliation", sequence.Summary);
        sequence.WorkSettled(fresh, Result()); Assert.Single(sequence.Outcomes);
    }
}
