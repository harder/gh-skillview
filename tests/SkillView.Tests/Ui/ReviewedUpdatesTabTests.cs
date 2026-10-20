using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Cli;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class ReviewedUpdatesTabTests
{
    [Theory]
    [InlineData(80, 20)]
    [InlineData(100, 26)]
    public void ReviewActionsFitSupportedTerminalWidths(int width, int height)
    {
        using var view = View([], (_, _, _) => Task.FromResult(Result()), (_, _, _) => Task.FromResult(Result()));
        view.Frame = new System.Drawing.Rectangle(0, 0, width, height);
        view.Layout(new System.Drawing.Size(width, height));
        foreach (var button in view.SubViews.OfType<Button>())
        {
            Assert.InRange(button.Frame.Left, 0, width - 1);
            Assert.InRange(button.Frame.Right, 1, width);
            Assert.InRange(button.Frame.Bottom, 1, height);
            Assert.Equal(1, button.Frame.Height);
            Assert.Null(button.ShadowStyle);
            Assert.Equal(Terminal.Gui.Drawing.Thickness.Empty, button.Margin.Thickness);
        }
        var controls = view.SubViews.Where(child => child.Visible && child is Button or CheckBox).ToArray();
        foreach (var control in controls)
            Assert.DoesNotContain(controls, other => !ReferenceEquals(control, other) && control.Frame.IntersectsWith(other.Frame));
        Assert.NotEqual(view.DryRunButtonForTests.Frame.Y, view.ApplyButtonForTests.Frame.Y);
    }

    private static InstalledSkill Target(string name, string root = "/skills") => new()
    {
        Name = name,
        ResolvedPath = root + "/" + name,
        ScanRoot = root,
        Scope = Scope.User,
        Agents = [new("claude", root + "/" + name, false)],
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };

    private static ReplacementResult Review(InstalledSkill target) => new(new(target.Name == "first" ? '1' : 'a', 32), "planned", true, false, false,
        target.ResolvedPath, "/state/original", "/state/candidate", "/state/displaced", new('b', 64), new('c', 64),
        ["SKILL.md", "script.sh"], "Review before apply", new(target.InstallationId, target.Name, target.Scope,
            target.Agents, target.FrontMatter, []), "/state/" + target.Name + "/plan.json", 448, 493,
        new(new("https://github.com/test/repo", "refs/heads/main", new('d', 40), new('e', 40), "skills/demo"),
            target.FrontMatter, null, ["extra.txt"]));

    private static UpdateResult Result(ReplacementResult? review = null, bool attempted = false) => new()
    {
        DryRun = false,
        Succeeded = true,
        ExitCode = 0,
        StdOut = "",
        StdErr = "",
        ErrorMessage = null,
        CommandLine = [],
        Entries = [],
        SavedPlan = review,
        MutationAttempted = attempted
    };

    private static UpdatesTabView View(InstalledSkill[] targets,
        Func<string, GhSkillUpdateService.Options, CancellationToken, Task<UpdateResult>> prepare,
        Func<ReplacementResult, bool, CancellationToken, Task<UpdateResult>> apply,
        Action? changed = null, Func<Action, Task>? dispatch = null,
        Func<LocalUpdateRequest, bool, CancellationToken, Task<LocalUpdateResponse>>? local = null,
        Func<InstalledSkill, bool, CancellationToken, Task<UpdateResult>>? unpin = null,
        Func<InstalledSkill, string?>? chooseTag = null) => new(
            dispatch ?? (action => { action(); return Task.CompletedTask; }),
            (operation, _) => operation().GetAwaiter().GetResult(),
            _ => Task.FromResult(InventorySnapshot.Empty with { Skills = targets.ToImmutableArray() }),
            prepare, () => "/gh", new Logger(), () => { }, changed ?? (() => { }), applyRunner: apply, localReviewRunner: local, unpinRunner: unpin, chooseTag: chooseTag);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparingSelectionDoesNotApplyAndRequiresFreshRetentionApproval(bool markedShortcut)
    {
        var selected = Target("demo"); var unrelated = Target("demo", "/other");
        var calls = new List<string>(); var review = Review(selected);
        using var view = View([selected, unrelated], (_, options, _) =>
        {
            Assert.Equal(selected.InstallationId, Assert.Single(options.Targets!).InstallationId);
            Assert.False(options.All); calls.Add("prepare"); return Task.FromResult(Result(review));
        }, (saved, _, _) =>
        {
            Assert.Same(review, saved); calls.Add("apply"); return Task.FromResult(Result(saved with { Status = "installed" }, true));
        });
        await view.LoadAsync();
        view.AllBoxForTests.Value = CheckState.Checked;
        view.MarkRowForTests(0);
        if (markedShortcut) await view.RunForTestsAsync(false, true);
        else view.UpdateCurrentRowForTests();
        Assert.Equal(new[] { "prepare" }, calls);
        Assert.False(view.ApplyButtonForTests.Enabled);
        await view.ApplyReviewForTestsAsync();
        Assert.Equal(new[] { "prepare" }, calls);
        var preview = view.PreviewTextForTests;
        Assert.Contains(review.Id, preview); Assert.Contains(review.PlanFingerprint, preview);
        Assert.Contains("extra.txt", preview); Assert.Contains("refs/heads/main", preview);
        Assert.Contains(new string('d', 40), preview); Assert.Contains(selected.ResolvedPath, preview);
        Assert.Contains("448 -> 493", preview);
        // Reload and current-row shortcuts cannot retarget an awaiting review.
        view.SelectRowForTests(1); await view.LoadAsync(); view.UpdateCurrentRowForTests();
        Assert.Equal(preview, view.PreviewTextForTests);
        view.RetainBoxForTests.Value = CheckState.Checked;
        Assert.True(view.ApplyButtonForTests.Enabled);
        await view.ApplyReviewForTestsAsync();
        Assert.Equal(new[] { "prepare", "apply" }, calls);
        Assert.False(view.ApplyButtonForTests.Enabled);
    }

    [Fact]
    public async Task SiblingsPrepareOnlyAfterPriorApplyCompletesAndEachNeedsApproval()
    {
        var first = Target("first"); var second = Target("second");
        var calls = new List<string>();
        var applying = new TaskCompletionSource<UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var view = View([first, second], (_, options, _) =>
        {
            var target = Assert.Single(options.Targets!); calls.Add("prepare:" + target.Name);
            return Task.FromResult(Result(Review(target)));
        }, (saved, _, _) => { calls.Add("apply:" + saved.Subject.Name); return applying.Task; });
        await view.LoadAsync(); view.AllBoxForTests.Value = CheckState.Checked;
        await view.RunForTestsAsync(false, false);
        Assert.Equal(new[] { "prepare:first" }, calls);
        view.RetainBoxForTests.Value = CheckState.Checked;
        var task = view.ApplyReviewForTestsAsync();
        await view.NextReviewForTestsAsync();
        Assert.Equal(new[] { "prepare:first", "apply:first" }, calls);
        applying.SetResult(Result(Review(first) with { Status = "installed" }, true));
        await task;
        Assert.Equal(2, calls.Count);
        await view.NextReviewForTestsAsync();
        Assert.Equal(new[] { "prepare:first", "apply:first", "prepare:second" }, calls);
        Assert.Equal(CheckState.UnChecked, view.RetainBoxForTests.Value);
        Assert.False(view.ApplyButtonForTests.Enabled);
    }

    [Fact]
    public async Task CanceledApplyStillNotifiesMutationWithoutOverwritingReloadedView()
    {
        var target = Target("demo"); var changed = 0;
        var applying = new TaskCompletionSource<UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var view = View([target], (_, _, _) => Task.FromResult(Result(Review(target))),
            (_, _, _) => applying.Task, () => changed++);
        await view.LoadAsync(); view.MarkRowForTests(0); await view.RunForTestsAsync(false, true);
        view.RetainBoxForTests.Value = CheckState.Checked;
        var task = view.ApplyReviewForTestsAsync();
        view.CancelPendingWork(); await view.LoadAsync(); var status = view.StatusTextForTests;
        applying.SetResult(Result(Review(target) with { Status = "needs-recovery", Canceled = true }, true));
        await task;
        Assert.Equal(1, changed); Assert.Equal(status, view.StatusTextForTests);
        Assert.False(view.ApplyButtonForTests.Enabled);
    }

    [Fact]
    public async Task SkippingReviewPreservesPlanAndContinuesCapturedTargets()
    {
        var first = Target("first"); var second = Target("second"); var calls = new List<string>();
        using var view = View([first, second], (_, options, _) =>
        {
            var target = Assert.Single(options.Targets!); calls.Add(target.Name);
            return Task.FromResult(Result(Review(target)));
        }, (_, _, _) => throw new InvalidOperationException("Skip must not apply"));
        await view.LoadAsync(); view.MarkRowForTests(0); view.MarkRowForTests(1);
        await view.RunForTestsAsync(false, true);
        await view.NextReviewForTestsAsync();
        Assert.Equal(new[] { "first", "second" }, calls);
        Assert.Contains("Saved plan", view.PreviewTextForTests);
        Assert.Contains("retained without applying", view.PreviewTextForTests);
    }

    [Fact]
    public async Task AdvancingAfterInterruptedApplyRetainsEarlierRecoveryIdAndManifest()
    {
        var first = Target("first"); var second = Target("second"); var firstReview = Review(first);
        using var view = View([first, second], (_, options, _) => Task.FromResult(Result(Review(Assert.Single(options.Targets!)))),
            (saved, _, _) => Task.FromResult(Result(saved with { Status = "needs-recovery" }, true) with
            {
                Succeeded = false,
                ExitCode = 2,
                ErrorMessage = "Inspect saved recovery evidence",
                Outcomes = [new(first.InstallationId, first.ResolvedPath, "recovery-required", null, null, [], 0, [],
                    $"Saved plan {firstReview.Id}; manifest: {firstReview.PlanPath}")]
            }));
        await view.LoadAsync(); view.AllBoxForTests.Value = CheckState.Checked;
        await view.RunForTestsAsync(false, false);
        view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        await view.NextReviewForTestsAsync();
        Assert.Contains("recovery-required", view.PreviewTextForTests);
        Assert.Contains(firstReview.Id, view.PreviewTextForTests);
        Assert.Contains(firstReview.PlanPath, view.PreviewTextForTests);
        Assert.Contains(Review(second).PlanPath, view.PreviewTextForTests);
    }

    [Fact]
    public void OrdinaryUpdateJsonCarriesExactSavedPlanWithoutClaimingMutation()
    {
        var review = Review(Target("demo"));
        var json = CliDispatcher.RenderUpdateJson(Result(review), new([], false, false, true, false, true,
            AllowHiddenDirectories: true), [], []);
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        Assert.False(root.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(review.Id, root.GetProperty("savedPlan").GetProperty("id").GetString());
        Assert.Equal(review.PlanFingerprint, root.GetProperty("applyArguments")[4].GetString());
        Assert.Contains("retain-original", root.GetProperty("applyArguments").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("--allow-hidden-dirs", root.GetProperty("applyArguments").EnumerateArray().Select(value => value.GetString()));
    }
    [Fact]
    public async Task PriorSuccessfulOutputAndNoticesSurviveNextPreparationFailure()
    {
        var first = Target("first"); var second = Target("second");
        using var view = View([first, second], (_, options, _) =>
        {
            var target = Assert.Single(options.Targets!);
            return Task.FromResult(target.Name == "first" ? Result(Review(target)) : Result() with
            {
                Succeeded = false,
                ExitCode = 2,
                ErrorMessage = "Second remote failed",
                StdOut = "Second preparation output",
                StdErr = "Second remote diagnostic",
                Outcomes = [new(second.InstallationId, second.ResolvedPath, "refused", null, null, [], 0, [], "Second remote failed")]
            });
        }, (saved, _, _) => Task.FromResult(Result(saved with { Status = "installed" }, true) with
        {
            StdOut = "First update completed",
            StdErr = "First update warning",
            Notices = [new("warning", "First notice remains visible")],
            Outcomes = [new(first.InstallationId, first.ResolvedPath, "changed", null, null, ["SKILL.md"], 1, [], "First payload verified")]
        }));
        await view.LoadAsync(); view.AllBoxForTests.Value = CheckState.Checked;
        await view.RunForTestsAsync(false, false);
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync();
        await view.NextReviewForTestsAsync();
        foreach (var text in new[] { "First update completed", "First update warning", "First notice remains visible",
            "Second preparation output", "Second remote diagnostic", "First payload verified", "Second remote failed" })
            Assert.Contains(text, view.PreviewTextForTests);
    }

    [Fact]
    public async Task FreshReviewQueueDoesNotRetainPriorOutputOrApplyTarget()
    {
        var first = Target("first"); var second = Target("second");
        var applied = new List<string>();
        using var view = View([first, second], (_, options, _) =>
        {
            var target = Assert.Single(options.Targets!);
            return Task.FromResult(Result(Review(target)) with { StdOut = "prepared " + target.Name });
        }, (saved, _, _) =>
        {
            applied.Add(saved.Subject.Name);
            return Task.FromResult(Result(saved with { Status = "installed" }, true) with { StdOut = "applied " + saved.Subject.Name });
        });
        await view.LoadAsync(); view.SelectRowForTests(0); view.UpdateCurrentRowForTests();
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync();
        Assert.Contains("applied first", view.PreviewTextForTests);
        await view.NextReviewForTestsAsync();
        view.SelectRowForTests(1); view.UpdateCurrentRowForTests();
        Assert.Contains("prepared second", view.PreviewTextForTests);
        Assert.DoesNotContain("prepared first", view.PreviewTextForTests);
        Assert.DoesNotContain("applied first", view.PreviewTextForTests);
        Assert.False(view.ApplyButtonForTests.Enabled);
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync();
        Assert.Equal(new[] { "first", "second" }, applied);
    }

}
