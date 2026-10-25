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
    [InlineData(80, 20, true)]
    [InlineData(100, 26, false)]
    [InlineData(120, 36, true)]
    public async Task SelectedCopyAndReviewUseFullWidthAndContextualControls(int width, int height, bool sourceReview)
    {
        var target = Target("demo"); var plan = Review(target);
        if (sourceReview)
        {
            var report = new SourceReviewReport([], 0, 0, new("limited", [], 0, [], 0), new("limited", [], 0, [], 0), [], 0, "Frozen evidence");
            plan = plan with { SourceReview = new(1, "installed", "candidate", "i", "c", SourceUpdateReview.Hash(report), report) };
        }
        using var view = View([target], (_, _, _) => Task.FromResult(Result(plan)), (_, _, _) => throw new InvalidOperationException("No implicit apply"),
            local: (_, _, _) => throw new InvalidOperationException("No implicit local review"));
        await view.LoadForSubjectAsync(OperationSubject.From(target));
        Layout();
        var preview = view.SubViews.OfType<Markdown>().Single();
        Assert.Equal(0, preview.Frame.X); Assert.True(preview.Frame.Width >= width - 2);
        Assert.True(preview.Frame.Height >= height - 7);
        Assert.False(view.RetainBoxForTests.Visible); Assert.False(view.ApplyButtonForTests.Visible);
        Assert.Contains("Nothing is applied on entry", view.PreviewTextForTests);
        await view.RunForTestsAsync(false, false);
        Layout();
        Assert.True(view.RetainBoxForTests.Visible); Assert.True(view.ApplyButtonForTests.Visible);
        Assert.False(view.DryRunButtonForTests.Visible); Assert.False(view.ChooseVersionButtonForTests.Visible);
        Assert.Equal(sourceReview, view.SourceReviewBoxForTests.Visible); Assert.False(view.BuildMergeButtonForTests.Visible);
        Assert.Contains("Preserve installed", view.SubViews.OfType<Button>().Where(button => button.Visible).Select(button => button.Text.ToString()));
        Assert.False(view.ApplyButtonForTests.Enabled);

        void Layout()
        {
            view.Frame = new(0, 0, width, height); view.Layout(new(width, height));
            var controls = view.SubViews.Where(child => child.Visible && child is Button or CheckBox).ToArray();
            foreach (var control in controls)
            {
                Assert.InRange(control.Frame.Left, 0, width - 1); Assert.InRange(control.Frame.Right, 1, width);
                Assert.InRange(control.Frame.Top, 0, height - 1); Assert.InRange(control.Frame.Bottom, 1, height);
                Assert.DoesNotContain(controls, other => !ReferenceEquals(control, other) && control.Frame.IntersectsWith(other.Frame));
                Assert.False(control.Frame.IntersectsWith(view.SubViews.OfType<Markdown>().Single().Frame));
            }
        }
    }

    [Fact]
    public async Task SummaryAndTechnicalViewsKeepTheSamePlanAndNeverGrantApproval()
    {
        var target = Target("demo"); var plan = Review(target); var prepares = 0; var applies = 0;
        using var view = View([target], (_, _, _) => { prepares++; return Task.FromResult(Result(plan)); },
            (_, _, _) => { applies++; return Task.FromResult(Result()); });
        await view.LoadForSubjectAsync(OperationSubject.From(target));
        await view.RunForTestsAsync(false, false);
        var summary = view.PreviewTextForTests;
        Assert.Contains("Review this change", summary); Assert.Contains(target.ResolvedPath, summary);
        Assert.Contains("extra.txt", summary); Assert.Contains("Every consumer", summary);
        Assert.DoesNotContain(plan.PlanFingerprint, summary); Assert.DoesNotContain(plan.PlanPath, summary);
        view.ToggleDetailsForTests();
        Assert.True(view.ShowingTechnicalForTests);
        Assert.Contains(plan.PlanFingerprint, view.PreviewTextForTests); Assert.Contains(plan.PlanPath, view.PreviewTextForTests);
        view.ToggleDetailsForTests();
        Assert.Equal(summary, view.PreviewTextForTests);
        Assert.False(view.ApplyButtonForTests.Enabled); Assert.Equal(1, prepares); Assert.Equal(0, applies);
    }

    [Fact]
    public async Task MutationRequiresFreshInventoryAndAFailedRefreshCanBeRetriedWithoutRetargeting()
    {
        var target = Target("demo"); var plan = Review(target); var loads = 0; var prepares = 0; var choices = 0;
        var fresh = target with { FrontMatter = target.FrontMatter with { GithubRef = "refs/heads/main" } };
        using var view = View([target], (_, _, _) => { prepares++; return Task.FromResult(Result(plan)); },
            (_, _, _) => Task.FromResult(Result(plan with { Status = "installed" }, attempted: true)),
            chooseTag: _ => { choices++; return "v1"; }, snapshotLoader: _ =>
            {
                loads++;
                if (loads == 2) throw new IOException("temporary capture failure");
                return Task.FromResult(InventorySnapshot.Empty with { Skills = [loads == 1 ? target : fresh] });
            });
        await view.LoadAsync(); view.MarkRowForTests(0); await view.RunForTestsAsync(false, false);
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        Assert.False(view.ChooseVersionButtonForTests.Visible);
        view.ChooseVersionForTests(); view.UpdateCurrentRowForTests(); await view.RunForTestsAsync(true, false);
        Assert.Equal(0, choices); Assert.Equal(1, prepares);
        view.ToggleDetailsForTests(); Assert.Contains(plan.PlanPath, view.PreviewTextForTests);
        await view.NextReviewForTestsAsync();
        Assert.Contains("temporary capture failure", view.StatusTextForTests);
        Assert.True(view.SubViews.OfType<Button>().Single(button => button.Text == "Refresh inventory").Enabled);
        Assert.False(view.ChooseVersionButtonForTests.Visible);
        await view.NextReviewForTestsAsync();
        Assert.Equal(3, loads); Assert.True(view.ChooseVersionButtonForTests.Visible);
        Assert.Equal(fresh.InstallationId, view.SelectedInstallationForTests);
        Assert.Contains("refs/heads/main", view.PreviewTextForTests);
    }

    [Fact]
    public async Task SelectionReflowsPinOnlyControlsWhenPinStateChanges()
    {
        var target = Target("ordinary"); var pinned = Target("pinned") with { FrontMatter = SkillFrontMatter.Empty with { Pinned = true } };
        using var view = View([target, pinned], (_, _, _) => throw new InvalidOperationException("No implicit prepare"),
            (_, _, _) => throw new InvalidOperationException("No implicit apply"),
            unpin: (_, _, _) => throw new InvalidOperationException("No implicit unpin"));
        await view.LoadAsync();
        Assert.False(view.UnpinOnlyButtonForTests.Visible);
        view.SelectRowForTests(1);
        Assert.True(view.UnpinOnlyButtonForTests.Visible); Assert.Contains("pinned", view.PreviewTextForTests);
        view.SelectRowForTests(0);
        Assert.False(view.UnpinOnlyButtonForTests.Visible); Assert.Contains("ordinary", view.PreviewTextForTests);
    }

    [Fact]
    public async Task LockOnlyConflictingAndUnknownPinsStayVisibleWithoutPreparingAChange()
    {
        var owner = new SkillPackage("test/repo", "github", null, null, null) { PinnedRef = "v1" };
        var locked = Target("locked") with { Package = owner };
        var conflict = Target("conflict") with { OwnershipClaims = [owner, owner with { PinnedRef = "v2" }] };
        var unknown = Target("unknown") with { Validity = ValidityState.UnparsableFrontMatter };
        var malformed = Target("malformed") with { Package = owner with { PinnedRef = " " } };
        using var view = View([locked, conflict, unknown, malformed], (_, _, _) => throw new InvalidOperationException("No implicit prepare"),
            (_, _, _) => throw new InvalidOperationException("No implicit apply"),
            unpin: (_, _, _) => throw new InvalidOperationException("No implicit unpin"));
        await view.LoadAsync();
        var table = view.SubViews.OfType<TableView>().Single().Table!;
        Assert.Equal("p-", table[0, 3]);
        Assert.Equal("!-", table[1, 3]);
        Assert.Equal("?-", table[2, 3]);
        Assert.Equal("?-", table[3, 3]);
        Assert.True(view.UnpinOnlyButtonForTests.Visible);
        Assert.Contains("v1", view.PreviewTextForTests);
        view.SelectRowForTests(1);
        Assert.True(view.UnpinOnlyButtonForTests.Visible);
        Assert.Contains("conflicting pin records", view.PreviewTextForTests);
        Assert.Contains("Pin records disagree", view.PreviewTextForTests);
        foreach (var row in new[] { 2, 3 })
        {
            view.SelectRowForTests(row);
            Assert.False(view.UnpinOnlyButtonForTests.Visible);
            Assert.Contains("pin state unknown", view.PreviewTextForTests);
            Assert.Contains("Pin state could not be verified", view.PreviewTextForTests);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalSkipExplainsWhetherInventoryRefreshIsRequired(bool attemptedWrite)
    {
        var first = Target("first"); var second = Target("second"); var captures = 0;
        using var view = View([first, second], (_, options, _) => Task.FromResult(Result(Review(Assert.Single(options.Targets!)))),
            (plan, _, _) => Task.FromResult(Result(plan with { Status = "installed" }, attempted: attemptedWrite)),
            snapshotLoader: _ => { captures++; return Task.FromResult(InventorySnapshot.Empty with { Skills = [first, second] }); });
        await view.LoadAsync(); view.MarkRowForTests(0); view.MarkRowForTests(1);
        await view.RunForTestsAsync(false, true);
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        await view.NextReviewForTestsAsync(); // Prepare the second captured target.
        await view.NextReviewForTestsAsync(); // Skip the final review.
        var table = view.SubViews.OfType<TableView>().Single();
        Assert.Equal(!attemptedWrite, table.Visible);
        Assert.Equal(!attemptedWrite, view.ChooseVersionButtonForTests.Visible);
        if (attemptedWrite)
        {
            Assert.Contains("Refresh inventory before another review", view.PreviewTextForTests);
            Assert.Contains("Refresh inventory before another review", view.StatusTextForTests);
            Assert.DoesNotContain("Choose an installation", view.PreviewTextForTests);
            view.ToggleDetailsForTests();
            Assert.Contains(Review(second).PlanPath, view.PreviewTextForTests);
            await view.NextReviewForTestsAsync();
            Assert.Equal(2, captures); Assert.True(view.ChooseVersionButtonForTests.Visible);
        }
        else
        {
            Assert.Contains("Choose an installation", view.PreviewTextForTests);
            Assert.Equal(1, captures);
        }
    }

    [Fact]
    public void AppliedSummaryShowsEachOutcomeOnceAndKeepsItsNoticeAndRecovery()
    {
        var target = Target("demo"); var plan = Review(target) with { Status = "installed" };
        var result = Result(plan, attempted: true) with
        { Outcomes = [new(target.InstallationId, target.ResolvedPath, "changed", null, null, [], 0, [], "verified files")] };
        var summary = UpdateReviewPresentation.Result(result, false, false);
        Assert.Single(summary.Split('\n'), line => line.StartsWith("- **`Outcome`:**", StringComparison.Ordinal));
        Assert.Single(summary.Split('\n'), line => line.StartsWith("- **`Location`:**", StringComparison.Ordinal));
        Assert.Contains("changed", summary); Assert.Contains("verified files", summary); Assert.Contains("Undo", summary);
        Assert.True(summary.IndexOf("Outcome", StringComparison.Ordinal) < summary.IndexOf("Local changes", StringComparison.Ordinal));
        result = result with { Outcomes = result.Outcomes.Add(new("peer", "/other/demo", "refused", null, null, [], 0, [], "peer notice")) };
        summary = UpdateReviewPresentation.Result(result, false, false);
        Assert.Equal(2, summary.Split('\n').Count(line => line.StartsWith("- **`Outcome`:**", StringComparison.Ordinal)));
        Assert.Contains("/other/demo", summary); Assert.Contains("peer notice", summary);
        result = result with
        {
            Outcomes = System.Collections.Immutable.ImmutableArray.CreateRange(Enumerable.Range(0, 10)
            .Select(index => new InstallationUpdateOutcome(index.ToString(), "/copies/" + index, "status" + index, null, null, [], 0, [], "notice" + index)))
        };
        summary = UpdateReviewPresentation.Result(result, false, false);
        Assert.Equal(8, summary.Split('\n').Count(line => line.StartsWith("- **`Outcome`:**", StringComparison.Ordinal)));
        Assert.Contains("status9", summary); Assert.Contains("notice9", summary);
        Assert.Contains("**`More outcomes`:** `2`", summary);
        Assert.True(summary.IndexOf("Undo", StringComparison.Ordinal) < summary.IndexOf("status0", StringComparison.Ordinal));
    }

    [Fact]
    public void SummaryBoundsAuthoredTextAndDoesNotInferAvailabilityFromEmptyOutput()
    {
        var target = Target("demo") with { Name = "untrusted\n## pretend\u001b[31m", ResolvedPath = new('x', 20_000) };
        var text = UpdateReviewPresentation.Selection(target);
        Assert.DoesNotContain('\u001b', text); Assert.DoesNotContain("\n## pretend", text);
        Assert.Contains("fields omitted", text); Assert.True(text.Length <= UpdateReviewPresentation.MaxCharacters);
        var result = Result() with { DryRun = true, StdOut = "nothing structured" };
        Assert.Contains("No structured installation outcome", UpdateReviewPresentation.Result(result, true, false));
        result = result with { Succeeded = false, MutationAttempted = true, ReconciliationPartial = true, ErrorMessage = "unavailable" };
        text = UpdateReviewPresentation.Result(result, false, false);
        Assert.Contains("write may have started", text); Assert.Contains("Incomplete", text); Assert.Contains("unavailable", text);
    }

    [Fact]
    public void UnpinSummaryDescribesPinOnlyChangeAndEveryAffectedConsumer()
    {
        var target = Target("demo");
        var plan = Review(target) with
        {
            Revision = null,
            PinPolicy = new(target.FrontMatter, "archive", "policy", [])
        };
        var summary = UpdateReviewPresentation.Plan(plan, true);
        Assert.Contains("Clear the verified pin", summary); Assert.Contains("claude", summary);
        Assert.Contains("unpin-only", summary); Assert.DoesNotContain("replacement uses the saved candidate", summary);
    }
}
