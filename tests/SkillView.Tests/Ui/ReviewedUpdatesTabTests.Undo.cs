using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class ReviewedUpdatesTabTests
{
    [Theory]
    [InlineData(80, 20)]
    [InlineData(120, 32)]
    public async Task UndoNeedsFreshInspectionAndIndependentApprovalThenRefreshesExactLocation(int width, int height)
    {
        var target = Target("demo"); var plan = Review(target);
        var applied = plan with { Status = "installed", MutationObserved = true };
        var inspected = applied with { MutationObserved = false, ReviewFingerprint = new('f', 64) };
        var inspections = 0; var undos = 0; var captures = 0;
        using var view = View([target], (_, _, _) => Task.FromResult(Result(plan)),
            (_, _, _) => Task.FromResult(Result(applied, attempted: true)),
            snapshotLoader: _ => { captures++; return Task.FromResult(InventorySnapshot.Empty with { Skills = [target] }); },
            inspectUndo: (saved, _, _) => { Assert.Same(applied, saved); inspections++; return Task.FromResult(inspected); },
            undo: (saved, _, _) =>
            {
                Assert.Same(inspected, saved); Assert.Equal(new string('f', 64), saved.ReviewFingerprint); undos++;
                return Task.FromResult(Result(saved with { Status = "restored", MutationObserved = true }, attempted: true));
            });
        await view.LoadForSubjectAsync(OperationSubject.From(target));
        Assert.False(view.UndoButtonForTests.Visible);
        await view.RunForTestsAsync(false, false);
        Assert.False(view.UndoButtonForTests.Visible);
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        Assert.True(view.UndoButtonForTests.Visible); Assert.Equal(0, inspections); Assert.Equal(0, undos);
        Layout();
        await view.UndoForTestsAsync(); Assert.Equal(0, undos);
        await view.InspectUndoForTestsAsync();
        Assert.Equal(1, inspections); Assert.False(view.ConfirmUndoButtonForTests.Enabled);
        Assert.Contains(target.ResolvedPath, view.PreviewTextForTests); Assert.Contains("claude", view.PreviewTextForTests);
        Assert.Contains("local edits and extras", view.PreviewTextForTests);
        Layout();
        view.ToggleDetailsForTests(); Assert.Contains(inspected.ReviewFingerprint, view.PreviewTextForTests);
        view.ToggleDetailsForTests(); await view.UndoForTestsAsync(); Assert.Equal(0, undos);
        view.CancelUndoForTests(); Assert.Contains("Undo", view.PreviewTextForTests);
        Assert.True(view.UndoButtonForTests.Visible); Assert.Equal(0, undos);
        await view.InspectUndoForTestsAsync(); Assert.Equal(2, inspections);
        view.UndoApprovalForTests.Value = CheckState.Checked;
        Assert.True(view.ConfirmUndoButtonForTests.Enabled);
        await view.UndoForTestsAsync(); Assert.Equal(1, undos);
        Assert.Contains("Original restored", view.PreviewTextForTests); Assert.DoesNotContain("Replace this copy", view.PreviewTextForTests);
        Assert.False(view.UndoButtonForTests.Visible);
        await view.NextReviewForTestsAsync(); Assert.Equal(2, captures);
        Assert.Equal(target.InstallationId, view.SelectedInstallationForTests);

        void Layout()
        {
            view.Frame = new(0, 0, width, height); view.Layout(new(width, height));
            var controls = view.SubViews.Where(child => child.Visible && child is Button or CheckBox).ToArray();
            foreach (var control in controls)
            {
                Assert.InRange(control.Frame.Right, 1, width); Assert.InRange(control.Frame.Bottom, 1, height);
                Assert.DoesNotContain(controls, other => !ReferenceEquals(other, control) && control.Frame.IntersectsWith(other.Frame));
                Assert.False(control.Frame.IntersectsWith(view.SubViews.OfType<Markdown>().Single().Frame));
            }
        }
    }

    [Theory]
    [InlineData("id")]
    [InlineData("plan")]
    [InlineData("address")]
    [InlineData("subject")]
    [InlineData("state")]
    public async Task UndoInspectionRefusesDifferentPlanOrAddressWithoutEnablingApproval(string changed)
    {
        var target = Target("demo"); var plan = Review(target); var applied = plan with { Status = "installed", MutationObserved = true };
        var different = changed switch
        {
            "id" => applied with { Id = new('f', 32) },
            "plan" => applied with { PlanFingerprint = new('e', 64) },
            "address" => applied with { OriginalPath = "/other/demo" },
            "subject" => applied with { Subject = applied.Subject with { InstallationId = "other" } },
            _ => applied with { Status = "restored" }
        };
        using var view = View([target], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result(applied, true)),
            inspectUndo: (_, _, _) => Task.FromResult(different), undo: (_, _, _) => throw new InvalidOperationException("No undo admitted"));
        await view.LoadAsync(); view.MarkRowForTests(0); await view.RunForTestsAsync(false, false);
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        await view.InspectUndoForTestsAsync();
        Assert.False(view.ConfirmUndoButtonForTests.Enabled); Assert.Contains("inspection refused", view.StatusTextForTests);
        Assert.Contains("Undo unavailable", view.PreviewTextForTests);
        Assert.True(view.UndoButtonForTests.Enabled); await view.UndoForTestsAsync();
    }

    [Fact]
    public async Task UndoKeepsOwnershipThroughQueuedCompletionAndCanceledDeliveryStillInvalidatesInventory()
    {
        var target = Target("demo"); var plan = Review(target); var applied = plan with { Status = "installed", MutationObserved = true };
        var hold = false; Action? queued = null; var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0; var invalidations = 0;
        using var view = View([target], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result(applied, true)),
            changed: () => invalidations++, dispatch: action =>
            { if (!hold) { action(); return Task.CompletedTask; } queued = action; return delivery.Task; },
            inspectUndo: (_, _, _) => Task.FromResult(applied with { MutationObserved = false, ReviewFingerprint = new('f', 64) }),
            undo: (saved, _, _) => { writes++; return Task.FromResult(Result(saved with { Status = "restored" }, true)); });
        await view.LoadAsync(); view.MarkRowForTests(0); await view.RunForTestsAsync(false, false);
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        await view.InspectUndoForTestsAsync(); view.UndoApprovalForTests.Value = CheckState.Checked;
        hold = true;
        var pending = view.UndoForTestsAsync();
        Assert.NotNull(queued); Assert.False(pending.IsCompleted); Assert.Equal(1, writes);
        await view.UndoForTestsAsync(); await view.InspectUndoForTestsAsync(); Assert.Equal(1, writes);
        Assert.False(view.ConfirmUndoButtonForTests.Enabled);
        view.CancelPendingWork(); var afterCancel = view.PreviewTextForTests;
        queued(); delivery.SetResult(); await pending;
        Assert.Equal(2, invalidations); Assert.Equal(afterCancel, view.PreviewTextForTests);
        Assert.False(view.UndoButtonForTests.Visible); Assert.False(view.ConfirmUndoButtonForTests.Enabled);
    }

    [Fact]
    public async Task UndoRefusalClosesApprovalAndRetainsEvidenceWithoutAutomaticRetry()
    {
        var target = Target("demo"); var plan = Review(target); var applied = plan with { Status = "installed", MutationObserved = true };
        var calls = 0;
        using var view = View([target], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result(applied, true)),
            inspectUndo: (_, _, _) => Task.FromResult(applied with { ReviewFingerprint = new('f', 64) }),
            undo: (saved, _, _) =>
            {
                calls++; return Task.FromResult(Result(saved, true) with
                {
                    Succeeded = false,
                    ErrorMessage = "Native evidence changed",
                    Outcomes = [new(saved.Subject.InstallationId, saved.OriginalPath, "refused", null, null, [], 0, [], "Native evidence changed")]
                });
            });
        await view.LoadAsync(); view.MarkRowForTests(0); await view.RunForTestsAsync(false, false);
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        await view.InspectUndoForTestsAsync(); view.UndoApprovalForTests.Value = CheckState.Checked; await view.UndoForTestsAsync();
        Assert.Equal(1, calls); Assert.False(view.UndoButtonForTests.Visible); Assert.False(view.ConfirmUndoButtonForTests.Enabled);
        await view.UndoForTestsAsync(); Assert.Equal(1, calls);
        Assert.Contains("Native evidence changed", view.PreviewTextForTests);
        Assert.Contains("**`Outcome`:** `refused`", view.PreviewTextForTests);
        Assert.DoesNotContain("Current disk state", view.PreviewTextForTests);
        view.ToggleDetailsForTests(); Assert.Contains(applied.PlanPath, view.PreviewTextForTests);
    }
}
