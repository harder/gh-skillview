using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using Xunit;
using Terminal.Gui.Views;

namespace SkillView.Tests.Ui;

public sealed partial class ReviewedUpdatesTabTests
{
    [Theory]
    [InlineData("restored")]
    [InlineData("installed")]
    public async Task IncompleteUndoReconciliationSeparatesNativeStateFromInventoryVerification(string nativeState)
    {
        var target = Target("demo"); var plan = Review(target); var applied = plan with { Status = "installed", MutationObserved = true };
        using var view = View([target], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result(applied, true)),
            inspectUndo: (_, _, _) => Task.FromResult(applied), undo: (saved, _, _) => Task.FromResult(Result(saved with { Status = nativeState }, true) with
            { Succeeded = false, ReconciliationPartial = true, ErrorMessage = "Post-undo inventory incomplete" }));
        await view.LoadForSubjectAsync(OperationSubject.From(target)); await view.ReviewUpdateAsync();
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        await view.InspectUndoForTestsAsync(); view.UndoApprovalForTests.Value = CheckState.Checked; await view.UndoForTestsAsync();
        Assert.Contains("verification incomplete", view.PreviewTextForTests);
        Assert.Contains("verification incomplete", view.StatusTextForTests);
        Assert.DoesNotContain("Undo failed or refused", view.PreviewTextForTests);
        Assert.DoesNotContain("before recovery", view.StatusTextForTests);
        Assert.Equal(nativeState == "restored", view.PreviewTextForTests.Contains("Original restored", StringComparison.Ordinal));
        Assert.Equal(nativeState == "restored", view.StatusTextForTests.Contains("original restored", StringComparison.Ordinal));
        Assert.False(view.UndoButtonForTests.Visible); Assert.True(view.SubViews.OfType<Button>().Single(b => b.Text == "Refresh inventory").Enabled);
    }

    [Theory]
    [InlineData("unpin")]
    [InlineData("local-reimport")]
    [InlineData("wrong-purpose")]
    [InlineData("partial")]
    [InlineData("canceled")]
    public async Task NonRevisionOrIncompleteAppliesCannotOfferResultUndo(string kind)
    {
        var target = Target("demo"); var plan = Review(target);
        var applied = plan with { Status = "installed", MutationObserved = true };
        if (kind == "unpin") applied = applied with { Revision = null, PinPolicy = new(target.FrontMatter, "archive", "policy", []) };
        if (kind == "local-reimport") applied = applied with { Revision = null, Purpose = "local-reimport" };
        if (kind == "wrong-purpose") applied = applied with { Purpose = "restore-recorded" };
        using var view = View([target], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result(applied, true) with
        { ReconciliationPartial = kind == "partial", Cancelled = kind == "canceled" }),
            inspectUndo: (_, _, _) => throw new InvalidOperationException("No inspection allowed"), undo: (_, _, _) => throw new InvalidOperationException("No undo allowed"));
        await view.LoadForSubjectAsync(OperationSubject.From(target)); await view.ReviewUpdateAsync();
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        Assert.False(view.UndoButtonForTests.Visible); await view.InspectUndoForTestsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedMutationFailuresInvalidateInventoryCloseApprovalAndRetainFailureDetails(bool duringUndo)
    {
        var target = Target("demo"); var plan = Review(target); var applied = plan with { Status = "installed", MutationObserved = true };
        var invalidations = 0;
        using var view = View([target], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) =>
            duringUndo ? Task.FromResult(Result(applied, true)) : throw new InvalidOperationException("Unexpected apply failure"),
            changed: () => invalidations++, inspectUndo: (_, _, _) => Task.FromResult(applied),
            undo: (_, _, _) => throw new InvalidOperationException("Unexpected undo failure"));
        await view.LoadForSubjectAsync(OperationSubject.From(target)); await view.ReviewUpdateAsync();
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        if (duringUndo)
        {
            await view.InspectUndoForTestsAsync(); view.UndoApprovalForTests.Value = CheckState.Checked; await view.UndoForTestsAsync();
        }
        Assert.Equal(duringUndo ? 2 : 1, invalidations);
        Assert.False(view.ApplyButtonForTests.Enabled); Assert.False(view.ConfirmUndoButtonForTests.Enabled);
        Assert.False(view.ChooseVersionButtonForTests.Visible);
        Assert.True(view.SubViews.OfType<Button>().Single(b => b.Text == "Refresh inventory").Enabled);
        view.ToggleDetailsForTests(); Assert.Contains(duringUndo ? "Unexpected undo failure" : "Unexpected apply failure", view.PreviewTextForTests);
        Assert.Contains(plan.PlanPath, view.PreviewTextForTests);
    }

    [Fact]
    public async Task InspectionFailureDetailsIncludeCurrentDiagnosticAndExactPlanWithoutReusingStaleText()
    {
        var target = Target("demo"); var plan = Review(target); var applied = plan with { Status = "installed", MutationObserved = true };
        using var view = View([target], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result(applied, true)),
            inspectUndo: (_, _, _) => throw new IOException("Fresh inspection failure\u001b[31m"), undo: (_, _, _) => throw new InvalidOperationException());
        await view.LoadForSubjectAsync(OperationSubject.From(target)); await view.ReviewUpdateAsync();
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync(); await view.InspectUndoForTestsAsync();
        view.ToggleDetailsForTests();
        Assert.Contains("Fresh inspection failure", view.PreviewTextForTests); Assert.Contains(plan.Id, view.PreviewTextForTests);
        Assert.Contains(plan.PlanPath, view.PreviewTextForTests); Assert.DoesNotContain('\u001b', view.PreviewTextForTests);
    }
}
