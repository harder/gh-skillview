using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class UpdateReviewKeyboardTests
{
    [Fact]
    public async Task RealKeyboardReadsDetailsThenApprovesSourceAndRetentionForTheSamePlan()
    {
        var target = CopiesPresentationTests.Copy("/skills/demo");
        var report = new SourceReviewReport([], 0, 0, new("limited", [], 0, [], 0), new("limited", [], 0, [], 0), [], 0, "Frozen evidence");
        var plan = ReviewedUpdatesTabTests.Review(target) with
        { SourceReview = new(1, "installed", "candidate", "i", "c", SourceUpdateReview.Hash(report), report) };
        var prepares = 0; var applies = 0;
        using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new UpdatesTabView(action => { action(); return Task.CompletedTask; },
            (work, _) => work().GetAwaiter().GetResult(), _ => Task.FromResult(InventorySnapshot.Empty with
            { Skills = [applies == 0 ? target : target with { FrontMatter = target.FrontMatter with { GithubRef = "refs/heads/main" } }] }),
            (_, options, _) => { Assert.Same(target, Assert.Single(options.Targets!)); prepares++; return Task.FromResult(Result(plan)); },
            () => "/gh", new Logger(), () => { }, () => { }, applyRunner: (captured, _, _) =>
            {
                Assert.Equal(plan.Id, captured.Id); Assert.Equal(plan.PlanFingerprint, captured.PlanFingerprint);
                Assert.True(captured.AcknowledgeSourceReview); applies++;
                return Task.FromResult(Result(captured with { Status = "installed", MutationObserved = true, SourceReviewAcknowledged = true }) with { MutationAttempted = true });
            })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        var session = app.Begin(window) ?? throw new InvalidOperationException("The test window did not start.");
        try
        {
            await view.LoadForSubjectAsync(OperationSubject.From(target));
            var preview = view.SubViews.OfType<Markdown>().Single();
            Assert.True(preview.HasFocus);
            Press(Key.F2); Assert.True(view.ShowingTechnicalForTests);
            Press(Key.F2); Assert.False(view.ShowingTechnicalForTests);
            var review = view.SubViews.OfType<Button>().Single(button => button.Text == "Review update");
            review.SetFocus(); Assert.True(review.HasFocus); Press(Key.Enter);
            Assert.Equal(1, prepares); Assert.Equal(0, applies);
            Assert.False(view.ApplyButtonForTests.Enabled);
            Press(Key.F2); Assert.True(view.ShowingTechnicalForTests);
            Assert.Contains(plan.SourceReview!.Fingerprint, view.PreviewTextForTests);
            Press(Key.F2); Assert.False(view.ShowingTechnicalForTests);
            view.SourceReviewBoxForTests.SetFocus(); Assert.True(view.SourceReviewBoxForTests.HasFocus); Press(Key.Space);
            Assert.False(view.ApplyButtonForTests.Enabled);
            view.RetainBoxForTests.SetFocus(); Assert.True(view.RetainBoxForTests.HasFocus); Press(Key.Space);
            Assert.True(view.ApplyButtonForTests.Enabled); Assert.Equal(0, applies);
            view.ApplyButtonForTests.SetFocus(); Assert.True(view.ApplyButtonForTests.HasFocus); Press(Key.Enter);
            Assert.Equal(1, prepares); Assert.Equal(1, applies);
            Assert.False(view.ApplyButtonForTests.Visible); Assert.False(view.ShowingTechnicalForTests);
            Assert.Contains("installed", view.PreviewTextForTests);
            Assert.False(view.ChooseVersionButtonForTests.Visible);
            var refresh = view.SubViews.OfType<Button>().Single(button => button.Text == "Refresh inventory");
            refresh.SetFocus(); Assert.True(refresh.HasFocus); Press(Key.Enter);
            Assert.True(view.ChooseVersionButtonForTests.Visible);
            Assert.Contains("refs/heads/main", view.PreviewTextForTests);
        }
        finally { app.End(session); }

        void Press(Key key) { app.Keyboard.RaiseKeyDownEvent(key); window.Draw(new DrawContext()); }
    }

    private static UpdateResult Result(ReplacementResult plan) => new()
    {
        DryRun = false,
        Succeeded = true,
        ExitCode = 0,
        StdOut = "",
        StdErr = "",
        ErrorMessage = null,
        CommandLine = [],
        Entries = [],
        SavedPlan = plan
    };
}
