using SkillView.Maintenance;
using SkillView.Inventory.Models;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class ReviewedUpdatesTabTests
{
    [Fact]
    public async Task PreserveRetainsOnlyTheExactSeededCopyWithoutApplyingOrPreparingItsPeer()
    {
        var first = Target("demo", "/chosen"); var second = Target("demo", "/peer"); var prepared = new List<string>();
        using var view = View([first, second], (_, options, _) =>
        {
            var target = Assert.Single(options.Targets!); prepared.Add(target.ResolvedPath);
            return Task.FromResult(Result(Review(target)));
        }, (_, _, _) => throw new InvalidOperationException("Preserve must not apply"), preserve: (request, _, _) =>
        {
            return Task.FromResult(request with { Status = "preserved", Message = "exact original preserved" });
        });
        await view.LoadForSubjectAsync(OperationSubject.From(first));
        await view.ReviewUpdateAsync();
        await view.PreserveForTestsAsync();
        Assert.Equal(new[] { first.ResolvedPath }, prepared); Assert.False(view.ApplyButtonForTests.Enabled);
        Assert.Contains("preserved", view.StatusTextForTests);
        await view.FinishReviewForTestsAsync(); Assert.Equal(new[] { first.ResolvedPath }, prepared);
        Assert.Equal(first.InstallationId, view.SelectedInstallationForTests);
        Assert.False(view.RetainBoxForTests.Value == CheckState.Checked);
        Assert.False(view.SourceReviewBoxForTests.Value == CheckState.Checked);
    }

    [Fact]
    public async Task PreserveOwnsQueuedCompletionAndCanceledDeliveryCannotOverwriteReloadedView()
    {
        var plan = Review(Target("demo")); Action? queued = null;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercept = false; var calls = 0; CancellationToken operationToken = default;
        using var view = View([Target("demo")], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result()),
            dispatch: action =>
            { if (!intercept) { action(); return Task.CompletedTask; } queued = action; ready.TrySetResult(); return delivered.Task; },
            preserve: (_, _, token) => { calls++; operationToken = token; return Task.FromResult(plan with { Status = "preserved" }); });
        await view.LoadForSubjectAsync(OperationSubject.From(Target("demo"))); await view.ReviewUpdateAsync();
        intercept = true; var pending = view.PreserveForTestsAsync(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.True(view.BusyForTests);
        await view.PreserveForTestsAsync(); await view.FinishReviewForTestsAsync(); Assert.Equal(1, calls);
        view.CancelPendingWork(); Assert.True(operationToken.IsCancellationRequested);
        intercept = false; await view.LoadAsync(); var status = view.StatusTextForTests;
        queued!(); delivered.SetResult(); await pending;
        Assert.Equal(status, view.StatusTextForTests);
        Assert.False(view.ApplyButtonForTests.Enabled);
    }

}
