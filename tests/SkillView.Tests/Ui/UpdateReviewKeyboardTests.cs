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
    public async Task ActualKeyboardReviewsCancelsAndApprovesUndoWithoutReusingApplyApproval()
    {
        var target = CopiesPresentationTests.Copy("/skills/demo");
        var plan = ReviewedUpdatesTabTests.Review(target);
        var applied = plan with { Status = "installed", MutationObserved = true };
        var inspected = applied with { MutationObserved = false, ReviewFingerprint = new('f', 64) };
        var inspections = 0; var undos = 0; var leaves = 0; var captures = 0;
        using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new UpdatesTabView(action => { action(); return Task.CompletedTask; },
            (work, _) => work().GetAwaiter().GetResult(), _ =>
            { captures++; return Task.FromResult(InventorySnapshot.Empty with { Skills = [target] }); },
            (_, _, _) => Task.FromResult(Result(plan)), () => "/gh", new Logger(), () => leaves++, () => { },
            applyRunner: (_, _, _) => Task.FromResult(Result(applied) with { MutationAttempted = true }),
            inspectUndoRunner: (_, _, _) => { inspections++; return Task.FromResult(inspected); },
            undoRunner: (saved, _, _) =>
            {
                Assert.Same(inspected, saved); undos++;
                return Task.FromResult(Result(saved with { Status = "restored", MutationObserved = true }) with { MutationAttempted = true });
            })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        var session = app.Begin(window) ?? throw new InvalidOperationException("The test window did not start.");
        try
        {
            await view.LoadForSubjectAsync(OperationSubject.From(target)); Press(new Key('u'));
            view.RetainBoxForTests.SetFocus(); Press(Key.Space);
            view.ApplyButtonForTests.SetFocus(); Press(Key.Enter);
            Assert.True(view.UndoButtonForTests.Visible); Assert.Equal(0, undos);
            Inspect(); Assert.Equal(1, inspections);
            Press(Key.F2); Assert.Contains(inspected.ReviewFingerprint, view.PreviewTextForTests); Press(Key.F2);
            Assert.False(view.ConfirmUndoButtonForTests.Enabled);
            Press(Key.Esc); Assert.Equal(0, leaves); Assert.Equal(0, undos);
            window.Layout(new(80, 24));
            Assert.InRange(view.UndoButtonForTests.Frame.Left, 0, 79);
            Assert.InRange(view.UndoButtonForTests.Frame.Right, 1, 80);
            Assert.True(view.UndoButtonForTests.Frame.Width > 0);
            Inspect(); Assert.Equal(2, inspections); Assert.False(view.ConfirmUndoButtonForTests.Enabled);
            Press(Key.Tab); Assert.True(view.DetailsButtonForTests.HasFocus);
            Press(Key.Tab); Assert.True(view.UndoApprovalForTests.HasFocus); Press(Key.Space);
            Assert.True(view.ConfirmUndoButtonForTests.Enabled);
            Press(Key.Tab); Assert.True(view.ConfirmUndoButtonForTests.HasFocus); Press(Key.Enter);
            Assert.Equal(1, undos); Assert.Contains("Original restored", view.PreviewTextForTests);
            var refresh = view.SubViews.OfType<Button>().Single(button => button.Text == "Refresh inventory");
            refresh.SetFocus(); Press(Key.Enter); Assert.Equal(2, captures);
            Assert.Equal(target.InstallationId, view.SelectedInstallationForTests);
        }
        finally { app.End(session); }

        void Inspect() { view.UndoButtonForTests.SetFocus(); Assert.True(view.UndoButtonForTests.HasFocus); Press(Key.Enter); }
        void Press(Key key) { app.Keyboard.RaiseKeyDownEvent(key); window.Draw(new DrawContext()); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task RealKeyboardReadsDetailsThenApprovesSourceAndRetentionForTheSamePlan(int width, int height)
    {
        var target = CopiesPresentationTests.Copy("/skills/demo");
        var report = new SourceReviewReport([], 0, 0, new("limited", [], 0, [], 0), new("limited", [], 0, [], 0), [], 0, "Frozen evidence");
        var plan = ReviewedUpdatesTabTests.Review(target) with
        { SourceReview = new(1, "installed", "candidate", "i", "c", SourceUpdateReview.Hash(report), report) };
        var prepares = 0; var applies = 0;
        using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new UpdatesTabView(action => { action(); return Task.CompletedTask; },
            (work, _) => work().GetAwaiter().GetResult(), _ => Task.FromResult(InventorySnapshot.Empty with
            { Skills = [applies == 0 ? target : target with { FrontMatter = target.FrontMatter with { GithubRef = "refs/heads/main" } }] }),
            (_, options, _) => { Assert.Same(target, Assert.Single(options.Targets!)); prepares++; return Task.FromResult(Result(plan)); },
            () => "/gh", new Logger(), () => { }, () => { }, applyRunner: (captured, _, _) =>
            {
                Assert.Equal(plan.Id, captured.Id); Assert.Equal(plan.PlanFingerprint, captured.PlanFingerprint);
                Assert.True(captured.AcknowledgeSourceReview); applies++;
                return Task.FromResult(Result(captured with { Status = "installed", MutationObserved = true, SourceReviewAcknowledged = true }) with
                { MutationAttempted = true, Outcomes = [new(target.InstallationId, target.ResolvedPath, "installed", null, null, [], 0, [], new('x', 2000))] });
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
            Assert.Contains(view.SubViews.OfType<Button>(), button => button.Text == "_Review update");
            Press(new Key('r').WithAlt);
            Assert.Equal(1, prepares); Assert.Equal(0, applies);
            Assert.False(view.ApplyButtonForTests.Enabled);
            Press(new Key('d'));
            Assert.True(view.DiffPreviewForTests.Visible);
            Assert.Equal(CheckState.UnChecked, view.SourceReviewBoxForTests.Value);
            Assert.Equal(CheckState.UnChecked, view.RetainBoxForTests.Value);
            Assert.False(view.ApplyButtonForTests.Enabled);
            Assert.InRange(view.DiffButtonForTests.Frame.Right, 1, width);
            Press(Key.Esc); Assert.False(view.DiffPreviewForTests.Visible);
            Assert.True(view.DiffButtonForTests.HasFocus);
            Press(Key.Enter); Assert.True(view.DiffPreviewForTests.Visible);
            Press(new Key('d')); Assert.False(view.DiffPreviewForTests.Visible);
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
            Assert.Equal(0, preview.Viewport.Y);
            preview.SetFocus(); Press(Key.PageDown);
            var scrolled = preview.Viewport.Y;
            Assert.True(scrolled > 0);
            window.Draw(new DrawContext());
            Assert.Equal(scrolled, preview.Viewport.Y);
            Assert.False(view.ChooseVersionButtonForTests.Visible);
            var refresh = view.SubViews.OfType<Button>().Single(button => button.Text == "Refresh inventory");
            refresh.SetFocus(); Assert.True(refresh.HasFocus); Press(Key.Enter);
            Assert.True(view.ChooseVersionButtonForTests.Visible);
            Assert.Contains("refs/heads/main", view.PreviewTextForTests);
        }
        finally { app.End(session); }

        void Press(Key key) { app.Keyboard.RaiseKeyDownEvent(key); window.Draw(new DrawContext()); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task DiffEditorTabNavigationWrapsThroughVisibleControlsWithoutChangingApprovals(int width, int height)
    {
        var target = CopiesPresentationTests.Copy("/skills/demo");
        var report = new SourceReviewReport([], 0, 0, new("limited", [], 0, [], 0), new("limited", [], 0, [], 0), [], 0, "Frozen evidence");
        var plan = ReviewedUpdatesTabTests.Review(target) with
        { SourceReview = new(1, "installed", "candidate", "i", "c", SourceUpdateReview.Hash(report), report) };
        var applies = 0; var preserves = 0; var leaves = 0;
        using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new UpdatesTabView(action => { action(); return Task.CompletedTask; },
            (work, _) => work().GetAwaiter().GetResult(), _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [target] }),
            (_, _, _) => Task.FromResult(Result(plan)), () => "/gh", new Logger(), () => leaves++, () => { },
            applyRunner: (_, _, _) => { applies++; return Task.FromResult(Result(plan)); },
            preserveRunner: (_, _, _) => { preserves++; return Task.FromResult(plan); })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        var session = app.Begin(window) ?? throw new InvalidOperationException("The test window did not start.");
        try
        {
            await view.LoadForSubjectAsync(OperationSubject.From(target)); Press(new Key('u')); Press(new Key('d'));
            Assert.True(view.DiffPreviewForTests.HasFocus);
            var text = view.DiffPreviewForTests.Text;
            var finish = view.SubViews.OfType<Button>().Single(button => button.Text == "Skip review");
            var preserve = view.SubViews.OfType<Button>().Single(button => button.Text == "Preserve installed");
            Press(Key.Tab);
            Assert.True(view.DetailsButtonForTests.HasFocus, "Tab from Diff editor must reach the first visible enabled control.");
            AssertApprovals(false);
            foreach (var control in new View[] { view.SourceReviewBoxForTests, view.RetainBoxForTests, finish, preserve, view.DiffButtonForTests, view.DiffPreviewForTests })
                Navigate(Key.Tab, control, false);
            foreach (var control in new View[] { view.DiffButtonForTests, preserve, finish, view.RetainBoxForTests, view.SourceReviewBoxForTests, view.DetailsButtonForTests, view.DiffPreviewForTests })
                Navigate(Key.Tab.WithShift, control, false);
            Assert.Equal(text, view.DiffPreviewForTests.Text);

            // Only explicit Space changes either approval; Tab merely visits it.
            Press(Key.Tab); Press(Key.Tab); Assert.True(view.SourceReviewBoxForTests.HasFocus); Press(Key.Space);
            Assert.Equal(CheckState.Checked, view.SourceReviewBoxForTests.Value);
            Assert.Equal(CheckState.UnChecked, view.RetainBoxForTests.Value);
            Assert.False(view.ApplyButtonForTests.Enabled);
            Press(Key.Tab); Assert.True(view.RetainBoxForTests.HasFocus); Press(Key.Space);
            AssertApprovals(true);
            foreach (var control in new View[] { view.ApplyButtonForTests, finish, preserve, view.DiffButtonForTests, view.DiffPreviewForTests })
                Navigate(Key.Tab, control, true);
            foreach (var control in new View[] { view.DiffButtonForTests, preserve, finish, view.ApplyButtonForTests, view.RetainBoxForTests, view.SourceReviewBoxForTests, view.DetailsButtonForTests, view.DiffPreviewForTests })
                Navigate(Key.Tab.WithShift, control, true);

            Press(Key.Esc); Assert.True(view.DiffButtonForTests.HasFocus); Assert.False(view.DiffPreviewForTests.Visible);
            AssertApprovals(true); Assert.Equal(0, leaves);
            Press(new Key('d')); Assert.True(view.DiffPreviewForTests.HasFocus);
            Press(new Key('d')); Assert.True(view.DiffButtonForTests.HasFocus); AssertApprovals(true);
            Press(Key.Tab.WithShift); Assert.True(preserve.HasFocus);
            Press(Key.Tab.WithShift); Assert.True(finish.HasFocus); Press(Key.Enter);
            Assert.Contains("Review closed", view.PreviewTextForTests);
            Assert.Equal(0, applies); Assert.Equal(0, preserves); Assert.Equal(0, leaves);
        }
        finally { app.End(session); }

        void Press(Key key) { app.Keyboard.RaiseKeyDownEvent(key); window.Draw(new DrawContext()); }
        void AssertApprovals(bool approved)
        {
            var state = approved ? CheckState.Checked : CheckState.UnChecked;
            Assert.Equal(state, view.SourceReviewBoxForTests.Value); Assert.Equal(state, view.RetainBoxForTests.Value);
            Assert.Equal(approved, view.ApplyButtonForTests.Enabled); Assert.Equal(0, applies); Assert.Equal(0, preserves);
        }
        void Navigate(Key key, View control, bool approved)
        {
            Press(key); Assert.True(control.Visible); Assert.True(control.Enabled);
            Assert.True(control.HasFocus, "Keyboard navigation must reach " + control.GetType().Name + ": " + control.Text);
            AssertApprovals(approved);
        }
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
