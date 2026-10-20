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
        Assert.Equal(VersionUpdateGuidance.ShortTagChoice, view.TableLabelForTests);
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
        Assert.Contains(accept ? "choose its version again" : "selection canceled; inventory refreshed", view.StatusTextForTests);
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
    [InlineData("unchanged")]
    [InlineData("payload")]
    [InlineData("pin")]
    [InlineData("native-generation")]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    public async Task AcceptedTagSurvivesRefreshOnlyForTheSameObservedInstallation(string change)
    {
        var original = Target("demo") with { PayloadFingerprint = "captured-payload" };
        original = original with
        {
            ObservedSubject = OperationSubject.From(original) with
            {
                NativeObject = new("observed", "inventory-observation", DateTimeOffset.UnixEpoch, "linux", "1", "2", original.ResolvedPath,
                    "10", "0", null, null, null)
            }
        };
        var fresh = original with
        {
            // A new capture timestamp alone must not discard the input.
            ObservedSubject = original.ObservedSubject with { NativeObject = original.ObservedSubject.NativeObject with { CapturedAt = DateTimeOffset.UtcNow } },
            Agents = original.Agents.ToArray().ToImmutableArray()
        };
        fresh = change switch
        {
            "payload" => fresh with { PayloadFingerprint = "changed-payload" },
            "pin" => fresh with { FrontMatter = fresh.FrontMatter with { Pinned = true } },
            "native-generation" => fresh with
            { ObservedSubject = fresh.ObservedSubject! with { NativeObject = fresh.ObservedSubject!.NativeObject with { ChangeTimeSeconds = "11" } } },
            _ => fresh
        };
        var snapshot = InventorySnapshot.Empty with { Skills = ImmutableArray.Create(original) };
        var prepared = new List<GhSkillUpdateService.Options>();
        UpdatesTabView? view = null;
        using var owned = View([original], (_, options, _) =>
        { prepared.Add(options); return Task.FromResult(Result(Review(fresh))); }, (_, _, _) => throw new InvalidOperationException("No apply"),
            chooseTag: _ =>
            {
                snapshot = snapshot with { Skills = change switch { "missing" => [], "ambiguous" => [fresh, fresh], _ => [fresh] } };
                view!.LoadAsync().GetAwaiter().GetResult();
                return "chosen";
            }, snapshotLoader: _ => Task.FromResult(snapshot));
        view = owned;
        await view.LoadAsync(); view.SelectRowForTests(0); view.ChooseVersionForTests();
        if (change == "unchanged")
        {
            var options = Assert.Single(prepared);
            Assert.Equal("chosen", options.Tag); Assert.Same(fresh, Assert.Single(options.Targets!));
        }
        else
        {
            Assert.Empty(prepared);
            Assert.Contains("changed, missing or ambiguous", view.StatusTextForTests);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChooserExceptionOrCanceledLoadDoesNotLeakDeferredSubjectIntoNextChoice(bool throws)
    {
        var target = Target("demo");
        var loads = 0; var choices = 0; var prepares = 0;
        UpdatesTabView? view = null;
        using var owned = View([target], (_, options, _) =>
        { prepares++; Assert.Same(target, Assert.Single(options.Targets!)); return Task.FromResult(Result()); },
            (_, _, _) => throw new InvalidOperationException("No apply"),
            chooseTag: _ =>
            {
                if (choices++ > 0) return "v2";
                view!.LoadForSubjectAsync(OperationSubject.From(Target("unrelated"))).GetAwaiter().GetResult();
                if (throws) throw new IOException("modal failed");
                view.CancelPendingLoad(); return "v1";
            }, snapshotLoader: _ => { loads++; return Task.FromResult(InventorySnapshot.Empty with { Skills = [target] }); });
        view = owned;
        await view.LoadAsync(); view.SelectRowForTests(0);
        if (throws) Assert.Throws<IOException>(() => view.ChooseVersionForTests());
        else view.ChooseVersionForTests();
        Assert.Equal(0, prepares);
        view.ChooseVersionForTests();
        Assert.Equal(1, prepares); Assert.Equal(1, loads);
    }

    [Fact]
    public async Task CancelWithEmptyDeferredInventoryReportsCancellationWithoutRetryGuidance()
    {
        var target = Target("demo"); var empty = false;
        UpdatesTabView? view = null;
        using var owned = View([target], (_, _, _) => throw new InvalidOperationException("No prepare"),
            (_, _, _) => throw new InvalidOperationException("No apply"), chooseTag: _ =>
            { empty = true; view!.LoadAsync().GetAwaiter().GetResult(); return null; },
            snapshotLoader: _ => Task.FromResult(InventorySnapshot.Empty with { Skills = empty ? [] : [target] }));
        view = owned;
        await view.LoadAsync(); view.SelectRowForTests(0); view.ChooseVersionForTests();
        Assert.Empty(view.LoadedSkillNamesForTests);
        Assert.Contains("selection canceled; inventory refreshed", view.StatusTextForTests);
        Assert.DoesNotContain("again", view.StatusTextForTests);
    }

    [Fact]
    public async Task DeferredChoiceExplainsCompetingReviewRatherThanSilentlyDroppingReload()
    {
        var target = Target("demo"); var prepares = 0; var loads = 0;
        Func<Task>? reload = null;
        UpdatesTabView? view = null;
        using var owned = View([target], (_, _, _) =>
        { prepares++; return Task.FromResult(Result(Review(target))); }, (_, _, _) => throw new InvalidOperationException("No apply"),
            chooseTag: _ => { view!.LoadAsync().GetAwaiter().GetResult(); return "v1"; },
            snapshotLoader: _ => { loads++; return Task.FromResult(InventorySnapshot.Empty with { Skills = [target] }); },
            runTask: (task, name) => { if (name == "updates.reload-after-version-choice") reload = task; else task().GetAwaiter().GetResult(); });
        view = owned;
        await view.LoadAsync(); view.SelectRowForTests(0); view.ChooseVersionForTests();
        view.MarkRowForTests(0); await view.RunForTestsAsync(false, true);
        Assert.Equal(1, prepares); Assert.NotNull(reload);
        await reload();
        Assert.Equal(1, prepares); Assert.Equal(1, loads);
        Assert.Contains("finish the current review", view.StatusTextForTests);
        Assert.Contains("Saved update review", view.PreviewTextForTests);
    }

    [Fact]
    public async Task UpdateActionsCannotCompeteInsideTheVersionChooser()
    {
        var target = Target("demo"); var prepares = 0; var unpins = 0;
        UpdatesTabView? view = null;
        using var owned = View([target], (_, options, _) =>
        { prepares++; Assert.Equal("chosen", options.Tag); return Task.FromResult(Result()); },
            (_, _, _) => throw new InvalidOperationException("No apply"),
            unpin: (_, _, _) => { unpins++; return Task.FromResult(Result()); },
            chooseTag: _ =>
            {
                view!.RunForTestsAsync(false, true).GetAwaiter().GetResult();
                view.UpdateCurrentRowForTests(); view.PlanUnpinForTestsAsync().GetAwaiter().GetResult();
                Assert.Equal(0, prepares); Assert.Equal(0, unpins); return "chosen";
            });
        view = owned;
        await view.LoadAsync(); view.SelectRowForTests(0); view.MarkRowForTests(0); view.ChooseVersionForTests();
        Assert.Equal(1, prepares); Assert.Equal(0, unpins);
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
