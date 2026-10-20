using SkillView.Maintenance;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class ReviewedUpdatesTabTests
{
    [Fact]
    public async Task SourceReviewDeclarationAndRetentionAreIndependentAndBoundToCapturedPlan()
    {
        var target = Target("demo");
        var report = new SourceReviewReport([], 0, 0, new("limited", [], 0, [], 0), new("limited", [], 0, [], 0), [], 0, "Frozen literal evidence; no execution");
        var review = Review(target) with { SourceReview = new(1, "review-installed-candidate", "review-source-candidate", new('1', 64), new('2', 64), SourceUpdateReview.Hash(report), report) };
        ReplacementResult? received = null;
        using var view = View([target], (_, _, _) => Task.FromResult(Result(review)), (saved, _, _) =>
        { received = saved; return Task.FromResult(Result(saved with { Status = "installed" }, true)); });
        await view.LoadAsync(); view.AllBoxForTests.Value = CheckState.Checked; await view.RunForTestsAsync(false, false);
        view.ToggleDetailsForTests();
        Assert.Contains(review.SourceReview.Fingerprint, view.PreviewTextForTests);
        Assert.Contains("not-reviewed", view.PreviewTextForTests);
        view.RetainBoxForTests.Value = CheckState.Checked;
        Assert.False(view.ApplyButtonForTests.Enabled);
        await view.ApplyReviewForTestsAsync(); Assert.Null(received);
        view.SourceReviewBoxForTests.Value = CheckState.Checked;
        view.RetainBoxForTests.Value = CheckState.UnChecked;
        Assert.False(view.ApplyButtonForTests.Enabled);
        await view.ApplyReviewForTestsAsync(); Assert.Null(received);
        view.RetainBoxForTests.Value = CheckState.Checked;
        Assert.True(view.ApplyButtonForTests.Enabled);
        await view.ApplyReviewForTestsAsync();
        Assert.NotNull(received); Assert.True(received.AcknowledgeSourceReview);
        Assert.Equal(review.PlanFingerprint, received.PlanFingerprint);
        Assert.Same(review.SourceReview, received.SourceReview);
        Assert.Equal(CheckState.UnChecked, view.SourceReviewBoxForTests.Value);
        Assert.Equal(CheckState.UnChecked, view.RetainBoxForTests.Value);
    }
}
