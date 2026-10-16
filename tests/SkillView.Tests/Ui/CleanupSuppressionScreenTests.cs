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
