using System.Collections.Immutable;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui.Tabs;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class ReviewedUpdatesTabTests
{
    [Fact]
    public async Task TagChoiceUsesCurrentPhysicalRowWithoutChangingMarksOrBroadOptionsAndKeepsApprovals()
    {
        var marked = Target("demo");
        var current = Target("demo", "/other") with
        { Agents = [new("claude", "/other/demo", false), new("codex", "/codex/demo", true)] };
        var report = new SourceReviewReport([], 0, 0, new("limited", [], 0, [], 0), new("limited", [], 0, [], 0), [], 0, "Frozen literal evidence; no execution");
        var original = Review(current);
        var review = original with
        {
            Revision = original.Revision! with { Source = original.Revision.Source with { Ref = "refs/tags/v1.2.3" } },
            SourceReview = new(1, "review-installed-candidate", "review-source-candidate", new('1', 64), new('2', 64), SourceUpdateReview.Hash(report), report)
        };
        var prepared = new List<GhSkillUpdateService.Options>();
        ReplacementResult? applied = null;
        var choices = 0;
        using var view = View([marked, current], (_, options, _) =>
        { prepared.Add(options); return Task.FromResult(options.DryRun ? Result() : Result(review)); },
            (saved, _, _) => { applied = saved; return Task.FromResult(Result(saved with { Status = "installed" }, true)); },
            chooseTag: selected => { Assert.Same(current, selected); choices++; return "v1.2.3"; });
        await view.LoadAsync();
        view.MarkRowForTests(0); view.SelectRowForTests(1);
        view.AllBoxForTests.Value = CheckState.Checked; view.UnpinBoxForTests.Value = CheckState.Checked;
        view.ChooseVersionForTests();
        var request = Assert.Single(prepared);
        Assert.Same(current, Assert.Single(request.Targets!));
        Assert.Equal("v1.2.3", request.Tag); Assert.False(request.All); Assert.False(request.DryRun); Assert.False(request.Unpin);
        Assert.Equal(CheckState.Checked, view.AllBoxForTests.Value); Assert.Equal(CheckState.Checked, view.UnpinBoxForTests.Value);
        Assert.Contains("refs/tags/v1.2.3", view.PreviewTextForTests);
        Assert.Contains(new string('d', 40), view.PreviewTextForTests);
        Assert.Contains("claude", view.PreviewTextForTests); Assert.Contains("codex", view.PreviewTextForTests);
        Assert.Contains("later ordinary Update follows latest", view.PreviewTextForTests, StringComparison.OrdinalIgnoreCase);
        Assert.False(view.ApplyButtonForTests.Enabled);
        Assert.Contains("One-time tag choice", view.TableLabelForTests);
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync(); Assert.Null(applied);
        view.SourceReviewBoxForTests.Value = CheckState.Checked;
        Assert.True(view.ApplyButtonForTests.Enabled);
        // Neither another current row nor a second choice can replace the pending review.
        view.SelectRowForTests(0); view.ChooseVersionForTests(); Assert.Equal(1, choices);
        await view.ApplyReviewForTestsAsync();
        Assert.NotNull(applied); Assert.Equal(review.PlanFingerprint, applied.PlanFingerprint); Assert.True(applied.AcknowledgeSourceReview);
        Assert.Contains("Space marks", view.TableLabelForTests);
        await view.NextReviewForTestsAsync();
        await view.RunForTestsAsync(true, true);
        var ordinary = prepared[1];
        Assert.Null(ordinary.Tag); Assert.Same(marked, Assert.Single(ordinary.Targets!));
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("refused")]
    [InlineData("exception")]
    public async Task TagPreparationWithoutReviewRestoresOrdinaryShortcutHeader(string outcome)
    {
        using var view = View([Target("demo")], (_, _, _) => outcome switch
        {
            "exception" => Task.FromException<UpdateResult>(new IOException("source unavailable")),
            "refused" => Task.FromResult(Result() with { Succeeded = false, ErrorMessage = "tag missing" }),
            _ => Task.FromResult(Result())
        }, (_, _, _) => throw new InvalidOperationException("No saved review"), chooseTag: _ => "v1");
        await view.LoadAsync(); view.SelectRowForTests(0);
        view.ChooseVersionForTests();
        Assert.Contains("Space marks", view.TableLabelForTests);
        Assert.DoesNotContain("One-time tag choice", view.TableLabelForTests);
        Assert.True(view.ChooseVersionButtonForTests.Enabled);
        Assert.False(view.ApplyButtonForTests.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshDuringChooserReloadsLatestSubjectAndRequiresFreshChoice(bool accept)
    {
        var original = Target("old"); var fresh = Target("fresh", "/new");
        var snapshot = InventorySnapshot.Empty with { Skills = ImmutableArray.Create(original) };
        var prepared = new List<GhSkillUpdateService.Options>();
        var choices = 0;
        UpdatesTabView? view = null;
        using var owned = View([original], (_, options, _) =>
        { prepared.Add(options); return Task.FromResult(Result(Review(fresh))); },
            (_, _, _) => throw new InvalidOperationException("No apply"),
            chooseTag: selected =>
            {
                if (choices++ > 0) { Assert.Same(fresh, selected); return "v2"; }
                Assert.Same(original, selected);
                snapshot = snapshot with { Skills = ImmutableArray.Create(fresh) };
                view!.LoadForSubjectAsync(OperationSubject.From(original)).GetAwaiter().GetResult();
                view.LoadForSubjectAsync(OperationSubject.From(fresh)).GetAwaiter().GetResult();
                return accept ? "v1" : null;
            }, snapshotLoader: _ => Task.FromResult(snapshot));
        view = owned;
        await view.LoadAsync(); view.SelectRowForTests(0);
        view.ChooseVersionForTests();
        Assert.Empty(prepared);
        Assert.Equal([fresh.Name], view.LoadedSkillNamesForTests);
        Assert.Contains("choose its version again", view.StatusTextForTests);
        view.SelectRowForTests(0); view.ChooseVersionForTests();
        Assert.Same(fresh, Assert.Single(Assert.Single(prepared).Targets!));
        Assert.Equal("v2", prepared[0].Tag);
    }

    [Fact]
    public async Task DeactivationDuringChooserDoesNotPrepareCapturedRow()
    {
        var prepares = 0;
        UpdatesTabView? view = null;
        using var owned = View([Target("demo")], (_, _, _) =>
        { prepares++; return Task.FromResult(Result()); },
            (_, _, _) => throw new InvalidOperationException("No apply"),
            chooseTag: _ => { view!.CancelPendingWork(); return "v1"; });
        view = owned;
        await view.LoadAsync(); view.SelectRowForTests(0); view.ChooseVersionForTests();
        Assert.Equal(0, prepares);
        Assert.Contains("Space marks", view.TableLabelForTests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledOrPinnedChoiceNeverPreparesCandidate(bool pinned)
    {
        var target = Target("demo") with { FrontMatter = SkillFrontMatter.Empty with { Pinned = pinned, PinnedRef = pinned ? "v1" : null } };
        var choices = 0; var prepared = 0;
        using var view = View([target], (_, _, _) => { prepared++; return Task.FromResult(Result()); },
            (_, _, _) => throw new InvalidOperationException("No review to apply"),
            chooseTag: _ => { choices++; return null; });
        await view.LoadAsync(); view.SelectRowForTests(0); view.MarkRowForTests(0);
        view.AllBoxForTests.Value = CheckState.Checked; view.UnpinBoxForTests.Value = CheckState.Checked;
        view.ChooseVersionForTests();
        Assert.Equal(pinned ? 0 : 1, choices); Assert.Equal(0, prepared);
        Assert.False(view.ApplyButtonForTests.Enabled);
        Assert.Contains(pinned ? "separate unpin" : "canceled", view.StatusTextForTests);
        Assert.Equal(CheckState.Checked, view.AllBoxForTests.Value); Assert.Equal(CheckState.Checked, view.UnpinBoxForTests.Value);
        if (!pinned)
        {
            await view.RunForTestsAsync(true, true);
            Assert.Equal(1, prepared); // Cancel left the original mark actionable.
        }
    }
}
