using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class CleanupSuppressionScreenTests
{
    [Fact]
    public void RetiredSelectionCannotOpenACleanupReviewOrStartWork()
    {
        using var app = Application.Create().Init("ansi"); var logger = new Logger();
        var screen = new CleanupScreen(app, new RemoveService(logger), logger,
            [new(CleanupClassifier.CandidateKind.EmptyDirectory, "/fixture/empty", "fixture", null)], [], [],
            lifetimeToken: new CancellationToken(true));
        screen.Show(_ => throw new InvalidOperationException("A retired selection cannot publish a review."));
        Assert.Null(screen.LastReport); Assert.Equal(0, screen.RemovedCount);
    }

    [Fact]
    public async Task ChangedNativeTargetRefusalSurvivesLeavingTheFocusedReview()
    {
        var root = Directory.CreateTempSubdirectory("skillview-cleanup-changed-review-");
        var original = Directory.CreateDirectory(Path.Combine(root.FullName, "empty")).FullName;
        var retained = Path.Combine(root.FullName, "retained-original");
        var submitted = false; var completed = false; var timedOut = false;
        try
        {
            await Task.Run(() =>
            {
                using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
                Dialog? dialog = null; var logger = new Logger();
                var screen = new CleanupScreen(app, new RemoveService(logger), logger,
                    [new(CleanupClassifier.CandidateKind.EmptyDirectory, original, "empty", null)], [new(root.FullName, Scope.Custom, null)], [],
                    confirmBatchRemoval: _ => { Directory.Move(original, retained); Directory.CreateDirectory(original); return 1; });
                app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (dialog is null) return true;
                    var review = dialog.SubViews.OfType<Button>().Single(button => button.Text == "Review removal");
                    if (!submitted && review.Enabled) { submitted = true; review.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); }
                    else if (submitted && screen.LastReport is not null && screen.OutcomeSummary.StartsWith("Refused/failed", StringComparison.Ordinal))
                    { completed = true; app.Keyboard.RaiseKeyDownEvent(Key.Esc); return false; }
                    return true;
                });
                screen.Show(value => dialog = value);
                Assert.StartsWith("Refused/failed", screen.OutcomeSummary);
                Assert.Contains("failed/refused 1", screen.OutcomeSummary);
                Assert.Equal(0, screen.RemovedCount); Assert.True(Directory.Exists(original)); Assert.True(Directory.Exists(retained));
            }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
            Assert.True(completed); Assert.False(timedOut);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task FocusedReviewPreservesKeyboardCancelDetailsAndEventLoopConfirmation(int width, int height)
    {
        var reviews = 0; var sent = false; var timedOut = false;
        await Task.Run(() =>
        {
            var thread = System.Environment.CurrentManagedThreadId;
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
            Dialog? dialog = null; var logger = new Logger();
            var screen = new CleanupScreen(app, new RemoveService(logger), logger,
                [new(CleanupClassifier.CandidateKind.BrokenSymlink, "/fixture/demo", "target missing", null)], [], [],
                confirmBatchRemoval: text =>
                {
                    Assert.Equal(thread, System.Environment.CurrentManagedThreadId);
                    Assert.Contains("Removes only this link. Target files stay.", text);
                    Assert.Contains("Deletion has no automatic undo.", text);
                    reviews++; return 0;
                });
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null || sent || !dialog.SubViews.OfType<Button>().Single(button => button.Text == "Review removal").Enabled) return true;
                sent = true;
                Assert.Empty(dialog.SubViews.OfType<TableView>());
                var detail = dialog.SubViews.OfType<Markdown>().Single();
                var buttons = dialog.SubViews.OfType<Button>().ToArray();
                Assert.All(buttons, button => Assert.True(dialog.ViewportToScreen().Contains(button.FrameToScreen())));
                detail.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(Key.Enter); Assert.Equal(0, reviews);
                app.Keyboard.RaiseKeyDownEvent(Key.F2); Assert.Contains("## Technical details", detail.Text);
                app.Keyboard.RaiseKeyDownEvent(Key.F2); Assert.Contains("## Broken link", detail.Text);
                buttons.Single(button => button.Text == "Review removal").SetFocus();
                app.Keyboard.RaiseKeyDownEvent(Key.Enter); Assert.Equal(1, reviews);
                Assert.Null(screen.LastReport);
                buttons.Single(button => button.Text == "Suppress suggestion").InvokeCommand(Command.Accept);
                Assert.Contains(dialog.SubViews.OfType<Label>(), label => label.Text.Contains("Fresh inventory unavailable", StringComparison.Ordinal));
                app.Keyboard.RaiseKeyDownEvent(Key.Esc); return false;
            });
            screen.Show(value => dialog = value);
            Assert.Null(screen.LastReport); Assert.Equal(0, screen.RemovedCount);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.True(sent); Assert.Equal(1, reviews);
    }

    [Fact]
    public async Task ReviewOnlyAndIncompleteReviewNeverEnableRemoval()
    {
        foreach (var reviewOnly in new[] { true, false })
        {
            var sent = false; var inspected = false; var timedOut = false;
            await Task.Run(() =>
            {
                using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
                Dialog? window = null; var logger = new Logger();
                var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "skillview-review-only-fixture"));
                var skill = CapturedFindingTests.Copy(path, "v1") with
                {
                    Agents = [new("fixture", path, false, Context: new string('x', MaintenanceQueueFormatter.MaxDetailCharacters + 1))]
                };
                var candidate = new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.EmptyDirectory,
                    path, "review", reviewOnly ? null : skill, reviewOnly);
                var screen = new CleanupScreen(app, new RemoveService(logger), logger, [candidate], [], []);
                app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (window is null || sent || !window.SubViews.OfType<Button>().Single(button => button.Text == "Suppress suggestion").Enabled) return true;
                    sent = true;
                    var button = window.SubViews.OfType<Button>().Single(button => button.Text == "Review removal");
                    if (reviewOnly) { Assert.False(button.Enabled); inspected = true; app.RequestStop(); }
                    else if (button.Enabled) { button.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); app.RequestStop(); }
                    else { sent = false; return true; }
                    return false;
                });
                app.AddTimeout(TimeSpan.FromMilliseconds(15), () =>
                {
                    if (inspected || app.TopRunnableView is not Dialog dialog || dialog.Title != "Review cleanup removal") return true;
                    inspected = true;
                    var remove = dialog.SubViews.OfType<Button>().Single(button => button.Text == "Remove");
                    Assert.False(remove.Enabled);
                    for (var i = 0; i < 4; i++)
                    {
                        app.Keyboard.RaiseKeyDownEvent(Key.F2); Assert.False(remove.Enabled);
                        Assert.True(dialog.SubViews.OfType<Markdown>().Single().Text.Length <= MaintenanceQueueFormatter.MaxDetailCharacters);
                    }
                    remove.InvokeCommand(Command.Accept); Assert.Same(dialog, app.TopRunnableView);
                    app.Keyboard.RaiseKeyDownEvent(Key.Esc); return false;
                });
                screen.Show(value => window = value); Assert.Null(screen.LastReport);
            }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
            Assert.True(inspected); Assert.False(timedOut);
        }
    }

    [Fact]
    public async Task NativeEmptyDirectoryRemovalRequiresSeparateConfirmationWithCancelDefault()
    {
        var root = Directory.CreateTempSubdirectory("skillview-focused-cleanup-");
        var empty = Directory.CreateDirectory(Path.Combine(root.FullName, "empty")).FullName;
        var sent = false; var confirmed = false; var timedOut = false;
        try
        {
            await Task.Run(() =>
            {
                using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
                Dialog? window = null; var logger = new Logger();
                var screen = new CleanupScreen(app, new RemoveService(logger), logger,
                    [new(CleanupClassifier.CandidateKind.EmptyDirectory, empty, "empty", null)], [new(root.FullName, Scope.Custom, null)], []);
                app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (window is null || sent || !window.SubViews.OfType<Button>().Single(button => button.Text == "Review removal").Enabled) return true;
                    sent = true;
                    window.SubViews.OfType<Button>().Single(button => button.Text == "Review removal").SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(Key.Enter); return false;
                });
                app.AddTimeout(TimeSpan.FromMilliseconds(15), () =>
                {
                    if (!confirmed && app.TopRunnableView is Dialog dialog && dialog.Title == "Review cleanup removal")
                    {
                        confirmed = true;
                        Assert.True(Directory.Exists(empty));
                        Assert.True(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Cancel").HasFocus);
                        app.Keyboard.RaiseKeyDownEvent(Key.Tab);
                        Assert.True(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Remove").HasFocus);
                        app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                    }
                    else if (confirmed && screen.LastReport is not null && ReferenceEquals(app.TopRunnableView, window))
                    { app.Keyboard.RaiseKeyDownEvent(Key.Esc); return false; }
                    return true;
                });
                screen.Show(value => window = value); Assert.Equal(1, screen.RemovedCount);
            }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(empty)); Assert.True(confirmed); Assert.False(timedOut);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task CancelDuringSuppressionDrainsOwnedCaptureBeforeClosing()
    {
        var started = 0; var drained = 0; var sent = false; var canceled = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
            Dialog? dialog = null; var logger = new Logger();
            var screen = new CleanupScreen(app, new RemoveService(logger), logger,
                [new(CleanupClassifier.CandidateKind.EmptyDirectory, "/fixture/demo", "fixture", null)], [], [],
                ownershipRunner: new ProcessRunner(logger), captureInventory: async token =>
                {
                    Interlocked.Increment(ref started);
                    try { return await new TaskCompletionSource<InventorySnapshot>().Task.WaitAsync(token); }
                    finally { if (token.IsCancellationRequested) Interlocked.Increment(ref drained); }
                });
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (!sent && dialog.SubViews.OfType<Button>().Single(button => button.Text == "Suppress suggestion").Enabled)
                { sent = true; dialog.SubViews.OfType<Button>().Single(button => button.Text == "Suppress suggestion").SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); }
                else if (Volatile.Read(ref started) > 0 && !canceled)
                { canceled = true; dialog.SubViews.OfType<Markdown>().Single().SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Esc); }
                return true;
            });
            screen.Show(value => dialog = value);
            Assert.Equal(1, drained); Assert.False(screen.PreferenceMutationObserved); Assert.Equal(0, screen.IgnoredCount);
            Assert.True(screen.CancellationObserved);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.Equal(1, started); Assert.True(canceled); Assert.False(timedOut);
    }
}
