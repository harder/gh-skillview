using SkillView.Gh;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class ReviewedUpdatesTabTests
{
    [Fact]
    public void CopiesVersionOptionsKeepExactCopyWithoutPreparingAnUnrequestedLatestCandidate()
    {
        var chosen = Target("demo", "/chosen");
        var prepared = 0;
        using var view = View([chosen], (_, _, _) => { prepared++; return Task.FromResult(Result()); },
            (_, _, _) => throw new InvalidOperationException());
        view.ActivateExactReview(OperationSubject.From(chosen), prepareImmediately: false);
        Assert.Equal(0, prepared);
        Assert.Equal(chosen.InstallationId, view.SelectedInstallationForTests);
        Assert.True(view.ChooseVersionButtonForTests.Enabled);
    }

    [Fact]
    public void MainReviewImmediatelyPreparesOnlyTheChosenCopyWithoutAnotherSelection()
    {
        var first = Target("demo", "/first");
        var chosen = Target("demo", "/chosen");
        GhSkillUpdateService.Options? received = null;
        using var view = View([first, chosen], (_, options, _) =>
        {
            received = options;
            return Task.FromResult(Result(Review(chosen)));
        }, (_, _, _) => throw new InvalidOperationException("Review must not apply"));
        view.ActivateExactReview(OperationSubject.From(chosen));
        Assert.NotNull(received);
        Assert.Equal(chosen.InstallationId, Assert.Single(received.Targets!).InstallationId);
        Assert.False(received.DryRun);
        Assert.False(received.All);
        Assert.Equal(chosen.InstallationId, view.SelectedInstallationForTests);
        Assert.Contains("review required", view.StatusTextForTests);
        Assert.False(view.ApplyButtonForTests.Enabled);
        Assert.Single(view.LoadedSkillNamesForTests);
    }

    [Fact]
    public async Task LeavingWhileExactSelectionLoadsCannotPrepareASiblingOrPublishAReview()
    {
        var chosen = Target("demo", "/chosen");
        var sibling = Target("demo", "/sibling");
        var pending = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? work = null;
        var prepared = 0;
        using var view = View([chosen, sibling], (_, _, _) => { prepared++; return Task.FromResult(Result(Review(chosen))); },
            (_, _, _) => throw new InvalidOperationException(), snapshotLoader: _ => pending.Task,
            runTask: (operation, _) => work = operation());
        view.ActivateExactReview(OperationSubject.From(chosen));
        view.CancelPendingWork();
        view.Visible = false;
        pending.SetResult(InventorySnapshot.Empty with { Skills = [sibling] });
        await work!;
        Assert.Equal(0, prepared);
        Assert.False(view.ApplyButtonForTests.Enabled);
    }
}
