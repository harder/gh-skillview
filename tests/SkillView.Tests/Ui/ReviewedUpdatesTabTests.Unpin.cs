using SkillView.Gh.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class ReviewedUpdatesTabTests
{
    private static ReplacementResult UnpinReview(SkillView.Inventory.Models.InstalledSkill target) => Review(target) with
    {
        Revision = null,
        ChangedPaths = ["SKILL.md"],
        PinPolicy = new(target.FrontMatter with { Pinned = false, PinnedRef = null }, new('a', 64), "captured native attributes", [])
    };

    [Fact]
    public async Task UnpinReviewsOnlySelectedPhysicalRowAndRequiresFreshRetentionApproval()
    {
        var first = Target("first");
        var second = Target("second") with
        {
            Agents = [new("claude", "/claude/second", true), new("codex", "/codex/second", true)],
            FrontMatter = SkillView.Inventory.Models.SkillFrontMatter.Empty with
            { Pinned = true, PinnedRef = "release-1", GithubRef = "refs/heads/main", GithubTreeSha = new('d', 40) }
        };
        var plan = UnpinReview(second); var prepared = 0; var applied = 0;
        using var view = View([first, second], (_, _, _) => throw new InvalidOperationException("No update/source resolution"),
            (captured, _, _) =>
            {
                Assert.Same(plan, captured); Assert.Equal(plan.PlanFingerprint, captured.PlanFingerprint);
                applied++; return Task.FromResult(Result(captured with { Status = "installed" }, attempted: true));
            }, local: (_, _, _) => throw new InvalidOperationException("Unpin has no local merge review"),
            unpin: (target, _, _) =>
            { Assert.Same(second, target); prepared++; return Task.FromResult(Result(plan)); });
        await view.LoadAsync(); view.SelectRowForTests(1); view.MarkRowForTests(0);
        view.AllBoxForTests.Value = CheckState.Checked; view.UnpinBoxForTests.Value = CheckState.Checked;
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.PlanUnpinForTestsAsync();
        Assert.Equal(1, prepared); Assert.Equal(0, applied);
        Assert.Equal(CheckState.UnChecked, view.RetainBoxForTests.Value);
        Assert.Equal("Apply unpin only", view.ApplyButtonForTests.Text.ToString());
        Assert.False(view.ApplyButtonForTests.Enabled);
        Assert.Contains("unpin-only", view.PreviewTextForTests);
        view.ToggleDetailsForTests();
        Assert.Contains(plan.PlanFingerprint, view.PreviewTextForTests);
        Assert.Contains("claude", view.PreviewTextForTests); Assert.Contains("codex", view.PreviewTextForTests);
        Assert.Contains("new active object IDs and timestamps", view.PreviewTextForTests);
        Assert.Contains("does not detach", view.PreviewTextForTests);
        await view.LocalActionForTestsAsync(LocalUpdateAction.Review);
        Assert.False(view.LocalPanelVisibleForTests);
        await view.ApplyReviewForTestsAsync(); Assert.Equal(0, applied);
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync(); Assert.Equal(1, applied);
    }

    [Fact]
    public async Task VerifiedUnpinNoOpLeavesNoApprovalAndAllowsAnotherSelection()
    {
        var calls = 0;
        using var view = View([Target("demo")], (_, _, _) => throw new InvalidOperationException("No update"),
            (_, _, _) => throw new InvalidOperationException("No plan to apply"), unpin: (_, _, _) =>
            {
                calls++;
                return Task.FromResult(Result() with
                { Outcomes = [new("demo-id", "/skills/demo", "unchanged", null, null, [], 0, [], "Verified unpinned; no retained-plan quota consumed.")] });
            });
        await view.LoadAsync(); await view.PlanUnpinForTestsAsync();
        Assert.False(view.ApplyButtonForTests.Enabled); Assert.False(view.RetainBoxForTests.Enabled);
        Assert.Contains("unchanged", view.StatusTextForTests);
        Assert.Contains("no retained-plan quota", view.PreviewTextForTests);
        await view.PlanUnpinForTestsAsync(); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UnpinOwnsQueuedCompletionAndCanceledDeliveryCannotRestoreApproval()
    {
        Action? queued = null; var calls = 0; var intercept = false; CancellationToken captured = default;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var view = View([Target("demo")], (_, _, _) => throw new InvalidOperationException("No update"),
            (_, _, _) => throw new InvalidOperationException("No stale apply"), dispatch: action =>
            {
                if (!intercept) { action(); return Task.CompletedTask; }
                queued = action; ready.TrySetResult(); return delivered.Task;
            }, unpin: (target, _, token) =>
            { calls++; captured = token; return Task.FromResult(Result(UnpinReview(target))); });
        await view.LoadAsync(); intercept = true;
        var pending = view.PlanUnpinForTestsAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await view.PlanUnpinForTestsAsync(); Assert.Equal(1, calls); Assert.True(view.BusyForTests);
        view.CancelPendingWork(); Assert.True(captured.IsCancellationRequested);
        intercept = false; await view.LoadAsync(); var status = view.StatusTextForTests;
        queued!(); delivered.SetResult(); await pending;
        Assert.Equal(status, view.StatusTextForTests); Assert.False(view.ApplyButtonForTests.Enabled);
        Assert.Equal(CheckState.UnChecked, view.RetainBoxForTests.Value);
    }
}
