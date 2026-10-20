using System.Collections.Immutable;
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
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task CleanupReviewKeysWorkFromListAndFocusedDetailsWithoutRemovingFiles(int width, int height)
    {
        var confirmed = false; var sent = false; var timedOut = false;
        var reviews = 0;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
            Window? window = null; var logger = new Logger(); var skill = Fixture();
            var screen = new CleanupScreen(app, new RemoveService(logger), logger,
                [new(CleanupClassifier.CandidateKind.BrokenSymlink, skill.ResolvedPath, "target missing", skill)], [], [skill],
                confirmBatchRemoval: text =>
                {
                    reviews++;
                    Assert.Contains("Removes only this link. Target files stay.", text);
                    Assert.Contains("Deletion has no automatic undo.", text);
                    confirmed = true;
                    return 0;
                });
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (window is null || sent) return true;
                sent = true;
                var table = window.SubViews.OfType<TableView>().Single();
                var detail = window.SubViews.OfType<Markdown>().Single();
                table.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.F2));
                Assert.Contains("## Technical details", detail.Text.ToString());
                detail.SetFocus();
                Assert.True(detail.HasFocus);
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.F2));
                Assert.Contains("## Broken link", detail.Text.ToString());
                Assert.DoesNotContain("## Technical details", detail.Text.ToString());
                table.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                Assert.True(confirmed);
                Assert.Null(screen.LastReport);
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                return false;
            });
            screen.Show(ready => window = ready);
            Assert.Null(screen.LastReport);
            Assert.Equal(0, screen.RemovedCount);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(confirmed); Assert.Equal(1, reviews); Assert.False(timedOut);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task FooterActionsShareKeyboardBehaviorAndEnterOnlyReviewsFromTheList(int width, int height)
    {
        var reviews = 0; var sent = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
            Window? window = null; var logger = new Logger(); var skill = Fixture();
            var screen = new CleanupScreen(app, new RemoveService(logger), logger,
                [new(CleanupClassifier.CandidateKind.BrokenSymlink, skill.ResolvedPath, "missing", skill)], [], [skill],
                confirmBatchRemoval: _ => { reviews++; return 0; });
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (window is null || sent) return true;
                sent = true;
                var table = window.SubViews.OfType<TableView>().Single();
                var detail = window.SubViews.OfType<Markdown>().Single();
                var bar = window.SubViews.OfType<StatusBar>().Single();
                var shortcuts = bar.SubViews.OfType<Shortcut>().ToArray();
                Assert.Equal(6, shortcuts.Length);
                Assert.All(shortcuts, shortcut => Assert.True(shortcut.Frame.X + shortcut.Frame.Width <= bar.Viewport.Width,
                    $"Footer action {shortcut.HelpText} is clipped at {width} columns."));
                table.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                detail.SetFocus(); Assert.True(detail.HasFocus);
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                Assert.Equal(0, reviews); // Enter in the reading pane never opens removal review.
                shortcuts[1].InvokeCommand(Command.Accept);
                Assert.Equal(1, reviews); // Deliberate footer acceptance does.
                shortcuts[2].InvokeCommand(Command.Accept);
                Assert.Contains("## Technical details", detail.Text.ToString());
                Assert.Equal("Summary", shortcuts[2].HelpText);
                detail.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.F2));
                Assert.DoesNotContain("## Technical details", detail.Text.ToString());
                Assert.Equal("Details", shortcuts[2].HelpText);
                shortcuts[0].InvokeCommand(Command.Activate); // Uncheck through the clickable footer action.
                table.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                Assert.Equal(1, reviews);
                shortcuts[0].InvokeCommand(Command.Accept); // Select again through its accepting path.
                table.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                Assert.Equal(2, reviews); // One Enter dispatches exactly one review.
                detail.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.R));
                Assert.Equal(3, reviews); // The explicit review alias remains available.
                shortcuts[3].InvokeCommand(Command.Accept);
                Assert.Contains("fresh inventory unavailable", window.SubViews.OfType<Label>().Single(
                    label => label.Text.ToString().Contains("fresh inventory unavailable", StringComparison.Ordinal)).Text.ToString());
                shortcuts[5].InvokeCommand(Command.Accept);
                return false;
            });
            screen.Show(ready => window = ready);
            Assert.Null(screen.LastReport); Assert.Equal(0, screen.RemovedCount);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.Equal(3, reviews);
    }

    [Fact]
    public async Task ReviewOnlyConfirmationKeepsItsRefusalVisibleOutsideTheScrolledDetails()
    {
        var opened = false; var sent = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
            Window? window = null; var logger = new Logger(); var skill = Fixture();
            var screen = new CleanupScreen(app, new RemoveService(logger), logger,
                [new(CleanupClassifier.CandidateKind.EmptyDirectory, skill.ResolvedPath,
                    "Other consumers use this directory. " + new string('x', 1000), skill, true)], [], [skill]);
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (window is null || sent) return true;
                sent = true;
                window.SubViews.OfType<TableView>().Single().SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                return false;
            });
            app.AddTimeout(TimeSpan.FromMilliseconds(15), () =>
            {
                if (opened || app.TopRunnableView is not Dialog dialog) return true;
                opened = true;
                var review = dialog.SubViews.OfType<Markdown>().Single();
                var explanation = dialog.SubViews.OfType<Label>().Single(label => label.Text.ToString().StartsWith("Removal unavailable", StringComparison.Ordinal));
                Assert.False(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Remove").Enabled);
                Assert.True(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Cancel").HasFocus);
                Assert.True(explanation.Frame.Width >= explanation.Text.ToString().Length);
                Assert.True(explanation.Frame.Y >= review.Frame.Y + review.Frame.Height);
                Assert.True(explanation.Frame.Y < dialog.Viewport.Height);
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.F2));
                Assert.Contains(dialog.SubViews.OfType<Label>(), label => label.Text.ToString().Contains("F2 Summary", StringComparison.Ordinal));
                Assert.True(explanation.Visible);
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter)); // Cancel remains the default.
                return false;
            });
            screen.Show(ready => window = ready);
            Assert.Null(screen.LastReport); Assert.Equal(0, screen.RemovedCount);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(opened); Assert.False(timedOut);
    }

    [Fact]
    public async Task RealConfirmationTabsFromCancelToRemoveAndEnterAppliesOnlyAfterReview()
    {
        var root = Directory.CreateTempSubdirectory("skillview-cleanup-confirm-");
        var empty = Directory.CreateDirectory(Path.Combine(root.FullName, "empty")).FullName;
        var opened = false; var sent = false; var cancelFocused = false; var removeFocused = false; var timedOut = false;
        try
        {
            await Task.Run(() =>
            {
                using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
                Window? window = null; var logger = new Logger();
                var screen = new CleanupScreen(app, new RemoveService(logger), logger,
                    [new(CleanupClassifier.CandidateKind.EmptyDirectory, empty, "empty", null)],
                    [new(root.FullName, Scope.Custom, null)], []);
                app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (window is null || sent) return true;
                    sent = true;
                    window.SubViews.OfType<TableView>().Single().SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                    return false;
                });
                // A separate timer runs inside the nested confirmation loop.
                app.AddTimeout(TimeSpan.FromMilliseconds(15), () =>
                {
                    if (!opened && app.TopRunnableView is Dialog dialog)
                    {
                        opened = true;
                        var buttons = dialog.SubViews.OfType<Button>().ToArray();
                        cancelFocused = buttons.Single(button => button.Text == "Cancel").HasFocus;
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
                        removeFocused = buttons.Single(button => button.Text == "Remove").HasFocus;
                        app.Keyboard.RaiseKeyDownEvent(new Key(removeFocused ? KeyCode.Enter : KeyCode.Esc));
                    }
                    else if (opened && ReferenceEquals(app.TopRunnableView, window)
                        && (screen.LastReport is not null || !removeFocused))
                    {
                        app.RequestStop();
                        return false;
                    }
                    return true;
                });
                screen.Show(ready => window = ready);
                Assert.Equal(1, screen.RemovedCount);
            }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
            Assert.True(opened); Assert.True(cancelFocused); Assert.True(removeFocused); Assert.False(timedOut);
            Assert.False(Directory.Exists(empty));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task RemovalEscapeFromFocusedDetailsDrainsBeforeClosingAndRetainsPartialCounts()
    {
        var root = Directory.CreateTempSubdirectory("skillview-cleanup-cancel-");
        using var release = new ManualResetEventSlim();
        var deleting = 0; var drained = 0; var sent = false; var canceled = false; var timedOut = false;
        var paths = new[] { "one", "two" }.Select(name => Directory.CreateDirectory(Path.Combine(root.FullName, name)).FullName).ToArray();
        try
        {
            await Task.Run(() =>
            {
                using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
                Window? window = null; var logger = new Logger();
                var remove = new RemoveService(logger, null, (_, _) =>
                {
                    if (Interlocked.Increment(ref deleting) != 2) return;
                    try { Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "UI did not release the paused removal."); }
                    finally { Interlocked.Increment(ref drained); }
                });
                var screen = new CleanupScreen(app, remove, logger,
                    paths.Select(path => new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.EmptyDirectory,
                        path, "empty", null)).ToImmutableArray(),
                    [new(root.FullName, Scope.Custom, null)], [], confirmBatchRemoval: _ => 1);
                app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; release.Set(); app.RequestStop(); return false; });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (window is null) return true;
                    if (!sent)
                    {
                        sent = true;
                        window.SubViews.OfType<TableView>().Single().SetFocus();
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.CursorDown));
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                    }
                    else if (Volatile.Read(ref deleting) == 2 && !canceled)
                    {
                        canceled = true;
                        var detail = window.SubViews.OfType<Markdown>().Single();
                        detail.SetFocus(); Assert.True(detail.HasFocus);
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                        release.Set();
                    }
                    return true;
                });
                screen.Show(ready => window = ready);
                Assert.Equal(1, Volatile.Read(ref drained));
                Assert.True(screen.LastReport!.IsCanceled);
                Assert.True(screen.CancellationObserved);
                Assert.Equal(1, screen.RemovedCount);
                Assert.Equal(1, screen.RemovedDirectoryCount); // Coordinator must refresh after partial cancellation.
                Assert.Single(Directory.EnumerateDirectories(root.FullName));
            }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
            Assert.True(canceled); Assert.False(timedOut);
        }
        finally { release.Set(); root.Delete(recursive: true); }
    }

    [Fact]
    public async Task IgnoreCancellationDrainsFreshInventoryBeforeClosing()
    {
        var started = 0; var drained = 0; var sent = false; var canceled = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(120, 40);
            Window? window = null; var logger = new Logger();
            var skill = Fixture();
            var screen = new CleanupScreen(app, new RemoveService(logger), logger,
                [new(CleanupClassifier.CandidateKind.Untracked, skill.ResolvedPath, "fixture", skill)], [], [skill],
                ownershipRunner: new ProcessRunner(logger), captureInventory: async token =>
                {
                    Interlocked.Increment(ref started);
                    try { return await new TaskCompletionSource<InventorySnapshot>().Task.WaitAsync(token); }
                    finally { if (token.IsCancellationRequested) Interlocked.Increment(ref drained); }
                });
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (window is null) return true;
                if (!sent)
                {
                    sent = true; window.SubViews.OfType<TableView>().Single().SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                    app.Keyboard.RaiseKeyDownEvent(new Key('i'));
                }
                else if (Volatile.Read(ref started) > 0 && !canceled)
                { canceled = true; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); }
                return true;
            });
            screen.Show(ready => window = ready);
            Assert.Equal(1, Volatile.Read(ref drained)); Assert.False(screen.PreferenceMutationObserved); Assert.Equal(0, screen.IgnoredCount);
            Assert.True(screen.CancellationObserved);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.Equal(1, started); Assert.True(canceled); Assert.False(timedOut);
    }

    [Fact]
    public async Task RemovalConfirmationRunsOnTheEventLoopThread()
    {
        var confirmed = false; var sent = false; var timedOut = false;
        await Task.Run(() =>
        {
            var uiThread = System.Environment.CurrentManagedThreadId;
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(120, 40);
            Window? window = null; var logger = new Logger(); var skill = Fixture();
            var screen = new CleanupScreen(app, new RemoveService(logger), logger,
                [new(CleanupClassifier.CandidateKind.Untracked, skill.ResolvedPath, "fixture", skill)], [], [skill],
                confirmBatchRemoval: _ => { Assert.Equal(uiThread, System.Environment.CurrentManagedThreadId); confirmed = true; return 0; });
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (window is null) return true;
                if (!sent)
                {
                    sent = true; window.SubViews.OfType<TableView>().Single().SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                    app.Keyboard.RaiseKeyDownEvent(new Key('r'));
                }
                else if (confirmed) app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                return true;
            });
            screen.Show(ready => window = ready); Assert.Null(screen.LastReport);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(confirmed); Assert.False(timedOut);
    }

    private static InstalledSkill Fixture() => new()
    {
        Name = "demo",
        ResolvedPath = Path.Combine(Path.GetTempPath(), "skillview-no-write-fixture", "demo"),
        ScanRoot = Path.Combine(Path.GetTempPath(), "skillview-no-write-fixture"),
        Scope = Scope.Custom,
        Agents = [],
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };
}
