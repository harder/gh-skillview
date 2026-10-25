using SkillView.Inventory.Models;
using System.Collections.Immutable;
using SkillView.Maintenance;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class ReviewedUpdatesTabTests
{
    private static LocalUpdateReview Local(ReplacementResult plan, string fingerprint = "local-0", string? selection = null)
    {
        var before = new PayloadEntry("SKILL.md", "file", new('1', 64), new('2', 64), 20, 384, null);
        var row = new LocalPathReview("SKILL.md", before, before with { ContentHash = new('3', 64) },
            before with { ContentHash = new('4', 64) }, selection, selection is null ? "both-sides-changed" : "explicit-choice");
        return new(plan.Id, plan.PlanFingerprint, "header", fingerprint, "/private/local-review",
            new([row], new(448, 448, 493, "installed", "preserve-installed-root-mode"), []), [], selection is not null, "Exact private evidence");
    }

    private static ReplacementResult Merged(ReplacementResult plan) => plan with
    {
        PlanFingerprint = new('f', 64),
        LocalDisposition = new("merge-reviewed", plan.PlanFingerprint, "header", "local-1",
            new("payload", "objects", 493, default, [], []), "preserved metadata", "merged-00", "1.5")
    };

    [Fact]
    public async Task LocalReviewClosesReplacementAndBuildRequiresFreshApprovalForExactMergedPlan()
    {
        var plan = Review(Target("demo")); var merged = Merged(plan);
        var actions = new List<LocalUpdateAction>(); var applied = new List<ReplacementResult>();
        using var view = View([Target("demo")], (_, _, _) => Task.FromResult(Result(plan)),
            (saved, _, _) => { applied.Add(saved); return Task.FromResult(Result(saved)); }, local: (request, _, _) =>
            {
                actions.Add(request.Action); Assert.Same(plan, request.Plan);
                if (request.Action == LocalUpdateAction.Review) return Task.FromResult(new LocalUpdateResponse(Review: Local(plan)));
                if (request.Action == LocalUpdateAction.Choose)
                {
                    Assert.Equal("local-0", request.Review!.ReviewFingerprint);
                    Assert.Equal(new LocalPathChoice("SKILL.md", "installed"), Assert.Single(request.Choices));
                    return Task.FromResult(new LocalUpdateResponse(Review: Local(plan, "local-1", "installed")));
                }
                Assert.Equal("local-1", request.Review!.ReviewFingerprint);
                return Task.FromResult(new LocalUpdateResponse(Plan: merged));
            });
        await view.LoadForSubjectAsync(OperationSubject.From(Target("demo"))); await view.ReviewUpdateAsync();
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.LocalActionForTestsAsync(LocalUpdateAction.Review);
        Assert.Equal(CheckState.UnChecked, view.RetainBoxForTests.Value);
        Assert.True(view.LocalPanelVisibleForTests); Assert.False(view.BuildMergeButtonForTests.Enabled);
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync(); Assert.Empty(applied);
        view.SelectLocalPathForTests(1); await view.LocalChoiceForTestsAsync("installed");
        Assert.True(view.BuildMergeButtonForTests.Enabled);
        await view.LocalActionForTestsAsync(LocalUpdateAction.Build);
        Assert.False(view.LocalPanelVisibleForTests); Assert.False(view.ApplyButtonForTests.Enabled);
        Assert.Equal(CheckState.UnChecked, view.RetainBoxForTests.Value);
        Assert.Contains("Apply the reviewed local merge", view.PreviewTextForTests);
        view.ToggleDetailsForTests();
        Assert.Contains("merge-reviewed", view.PreviewTextForTests);
        Assert.Equal(new[] { LocalUpdateAction.Review, LocalUpdateAction.Choose, LocalUpdateAction.Build }, actions);
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync(); Assert.Same(merged, Assert.Single(applied));
    }

    [Fact]
    public async Task PreserveDoesNotApplyOrPrepareAnotherCopy()
    {
        var first = Target("first"); var second = Target("second"); var prepared = new List<string>();
        using var view = View([first, second], (_, options, _) =>
        {
            var target = Assert.Single(options.Targets!); prepared.Add(target.Name);
            return Task.FromResult(Result(Review(target)));
        }, (_, _, _) => throw new InvalidOperationException("Preserve must not apply"), local: (request, _, _) =>
        {
            Assert.Equal(LocalUpdateAction.Preserve, request.Action);
            return Task.FromResult(new LocalUpdateResponse(Plan: request.Plan with { Status = "preserved", Message = "exact original preserved" }));
        });
        await view.LoadForSubjectAsync(OperationSubject.From(first)); await view.ReviewUpdateAsync();
        await view.LocalActionForTestsAsync(LocalUpdateAction.Preserve);
        Assert.Equal(new[] { "first" }, prepared); Assert.False(view.ApplyButtonForTests.Enabled);
        Assert.Contains("preserved", view.StatusTextForTests);
        Assert.Equal(new[] { "first" }, prepared);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task UncertainPrivateWritesRequireRefreshAndCannotRestoreOldApproval(int failingAction)
    {
        var failing = (LocalUpdateAction)failingAction;
        var plan = Review(Target("demo")); var fail = true; var calls = 0;
        using var view = View([Target("demo")], (_, _, _) => Task.FromResult(Result(plan)),
            (_, _, _) => throw new InvalidOperationException("old approval must stay closed"), local: (request, _, _) =>
            {
                calls++;
                if (request.Action == failing && fail) throw new IOException("disk full after private write");
                return Task.FromResult(new LocalUpdateResponse(Review: Local(plan, selection: "installed")));
            });
        await view.LoadForSubjectAsync(OperationSubject.From(Target("demo"))); await view.ReviewUpdateAsync();
        await view.LocalActionForTestsAsync(LocalUpdateAction.Review);
        if (failing == LocalUpdateAction.Choose) { view.SelectLocalPathForTests(1); await view.LocalChoiceForTestsAsync("source"); }
        if (failing == LocalUpdateAction.Build) await view.LocalActionForTestsAsync(LocalUpdateAction.Build);
        Assert.Contains("disk full", view.StatusTextForTests);
        Assert.False(view.BuildMergeButtonForTests.Enabled);
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync(); var before = calls;
        await view.LocalActionForTestsAsync(LocalUpdateAction.Build); Assert.Equal(before, calls);
        fail = false; await view.LocalActionForTestsAsync(LocalUpdateAction.Review);
        Assert.True(view.BuildMergeButtonForTests.Enabled);
    }

    [Fact]
    public async Task ResolutionInputAcceptsPrintableShortcutsAndChoicesRetainOtherExplicitPaths()
    {
        var plan = Review(Target("demo")); LocalUpdateRequest? chosen = null;
        var local = Local(plan, selection: "installed");
        local = local with
        {
            Review = local.Review with
            {
                Paths = local.Review.Paths.Add(local.Review.Paths[0] with { Path = "other.txt", Selection = "resolved", Reason = "explicit-choice" }),
                RootMode = local.Review.RootMode with { Selection = "source", Reason = "explicit-choice" }
            }
        };
        using var view = View([Target("demo")], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result()),
            local: (request, _, _) =>
            { if (request.Action == LocalUpdateAction.Choose) chosen = request; return Task.FromResult(new LocalUpdateResponse(Review: local)); });
        await view.LoadForSubjectAsync(OperationSubject.From(Target("demo"))); await view.ReviewUpdateAsync(); await view.LocalActionForTestsAsync(LocalUpdateAction.Review);
        view.SelectLocalPathForTests(1);
        Assert.True(view.ResolutionFileForTests.SetFocus());
        Assert.True(view.LocalFileInputHasFocus);
        foreach (var key in "aduU") view.ResolutionFileForTests.NewKeyDownEvent(new Key(key));
        Assert.Equal("aduU", view.ResolutionFileForTests.Text.ToString());
        await view.LocalChoiceForTestsAsync("resolved");
        Assert.NotNull(chosen);
        Assert.Equal("local-0", chosen.Review!.ReviewFingerprint);
        Assert.Equal(new LocalResolutionInput("SKILL.md", "aduU"), Assert.Single(chosen.Resolutions));
        Assert.Contains(new LocalPathChoice("other.txt", "resolved"), chosen.Choices);
        Assert.Contains(new LocalPathChoice("/", "source"), chosen.Choices);
        Assert.Equal(3, chosen.Choices.Length);
    }

    [Fact]
    public async Task LocalOperationOwnsQueuedCompletionAndCanceledDeliveryCannotOverwriteReloadedView()
    {
        var plan = Review(Target("demo")); Action? queued = null;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercept = false; var calls = 0; CancellationToken operationToken = default;
        using var view = View([Target("demo")], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result()),
            dispatch: action =>
            { if (!intercept) { action(); return Task.CompletedTask; } queued = action; ready.TrySetResult(); return delivered.Task; },
            local: (_, _, token) => { calls++; operationToken = token; return Task.FromResult(new LocalUpdateResponse(Review: Local(plan))); });
        await view.LoadForSubjectAsync(OperationSubject.From(Target("demo"))); await view.ReviewUpdateAsync();
        intercept = true; var pending = view.LocalActionForTestsAsync(LocalUpdateAction.Review); await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.True(view.BusyForTests);
        await view.LocalActionForTestsAsync(LocalUpdateAction.Preserve); await view.FinishReviewForTestsAsync(); Assert.Equal(1, calls);
        view.CancelPendingWork(); Assert.True(operationToken.IsCancellationRequested);
        intercept = false; await view.LoadForSubjectAsync(OperationSubject.From(Target("demo"))); var status = view.StatusTextForTests;
        queued!(); delivered.SetResult(); await pending;
        Assert.Equal(status, view.StatusTextForTests); Assert.False(view.LocalPanelVisibleForTests);
        Assert.False(view.ApplyButtonForTests.Enabled);
    }

    [Theory]
    [InlineData(80, 20)]
    [InlineData(100, 26)]
    public async Task LocalEditorFitsAndRendersExactSanitizedEvidence(int width, int height)
    {
        var plan = Review(Target("demo")); var local = Local(plan) with { Message = "untrusted\u001b[31m text" };
        using var view = View([Target("demo")], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result()),
            local: (_, _, _) => Task.FromResult(new LocalUpdateResponse(Review: local)));
        await view.LoadForSubjectAsync(OperationSubject.From(Target("demo"))); await view.ReviewUpdateAsync(); await view.LocalActionForTestsAsync(LocalUpdateAction.Review);
        view.Frame = new System.Drawing.Rectangle(0, 0, width, height); view.Layout(new(width, height));
        static void Check(View parent)
        {
            foreach (var child in parent.SubViews.Where(child => child.Visible))
            {
                Assert.InRange(child.Frame.X, 0, parent.Viewport.Width - 1);
                Assert.True(child.Frame.Right > 0 && child.Frame.Right <= parent.Viewport.Width,
                    $"{child.GetType().Name} ({child.Text}) frame {child.Frame} overflows {parent.GetType().Name} viewport {parent.Viewport}");
                Assert.InRange(child.Frame.Y, 0, parent.Viewport.Height - 1);
                Assert.InRange(child.Frame.Bottom, 1, parent.Viewport.Height);
                // Terminal.Gui clips overflow shortcuts inside the shared status bar.
                if (child is not StatusBar) Check(child);
            }
        }
        Check(view); view.SelectLocalPathForTests(1);
        Assert.Contains("SKILL.md", view.LocalDetailForTests); Assert.Contains(new string('3', 64), view.LocalDetailForTests);
        Assert.Contains("mode 600", view.LocalDetailForTests); Assert.Contains("UNRESOLVED", view.LocalDetailForTests);
        Assert.DoesNotContain("\u001b", view.LocalDetailForTests);
    }

    [Fact]
    public async Task LocalRefusalDisplayIsBoundedAndRetainsEvidenceLocation()
    {
        var plan = Review(Target("demo"));
        var local = Local(plan) with { MetadataRefusals = Enumerable.Range(0, 70).Select(i => $"issue-{i:D2}-" + new string('x', 4000)).ToImmutableArray() };
        using var view = View([Target("demo")], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => Task.FromResult(Result()),
            local: (_, _, _) => Task.FromResult(new LocalUpdateResponse(Review: local)));
        await view.LoadForSubjectAsync(OperationSubject.From(Target("demo"))); await view.ReviewUpdateAsync();
        await view.LocalActionForTestsAsync(LocalUpdateAction.Review);
        Assert.Contains("issue-31-", view.LocalDetailForTests); Assert.DoesNotContain("issue-32-", view.LocalDetailForTests);
        Assert.Contains("38; inspect the saved evidence", view.LocalDetailForTests);
        Assert.Contains(local.ReviewPath, view.LocalDetailForTests);
        Assert.InRange(view.LocalDetailForTests.Length, 1, 70_000);
    }
}
