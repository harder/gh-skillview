using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.PackageChecking;
using SkillView.Tests.Cli;
using SkillView.Ui;
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
public sealed class PackageCheckScreenTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public void InstalledKeyboardChecksSelectedPhysicalRowAndKeepsFilterTyping(int width, int height)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        InstalledSkill? captured = null;
        InventorySnapshot? capturedSnapshot = null;
        string? capturedId = null;
        var first = Target("first"); var second = Target("second");
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(InventorySnapshot.Empty), (_, _, _) => Task.CompletedTask, () => { },
            onPackageCheck: (snapshot, id, target) => { capturedSnapshot = snapshot; capturedId = id; captured = target; })
        { Width = Dim.Fill(), Height = Dim.Fill(), Visible = true };
        var snapshot = InventorySnapshot.Empty with { Skills = [first, second] };
        window.Add(view); view.LoadSeeded(snapshot);
        var shellReceived = false;
        window.KeyDown += (_, key) => { if (!key.Handled && key.AsRune.Value == 'K') shellReceived = true; };
        var session = app.Begin(window);
        try
        {
            view.SetSelectedRowForTests(3); view.FocusList(); Assert.True(view.TableHasFocusForTests);
            app.Keyboard.RaiseKeyDownEvent(new Key('K')); window.Draw(new DrawContext());
            Assert.Same(second, captured); Assert.False(shellReceived);
            Assert.Same(snapshot, capturedSnapshot); Assert.Equal(second.InstallationId, capturedId);
            captured = null; view.FocusFilterForTests(); Assert.True(view.FilterHasFocusForTests);
            app.Keyboard.RaiseKeyDownEvent(new Key('K')); window.Draw(new DrawContext());
            Assert.Null(captured); Assert.True(view.FilterHasFocusForTests);
        }
        finally { app.End(session!); }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("canceled")]
    public async Task QueuedCompletionRetainsOwnershipAndRejectsLateParentCancellation(string outcome)
    {
        var calls = 0; var checkedQueued = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(100, 30);
            using var lifetime = new SkillView.Threading.CancellationSource(_ => { });
            Action? queued = null; Dialog? modal = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (outcome == "canceled" && Volatile.Read(ref calls) > 0) lifetime.TryCancel();
                var commit = Volatile.Read(ref queued);
                if (commit is null || modal is null) return true;
                var check = modal.SubViews.OfType<Button>().Single(button => button.Text == "_Check again");
                Assert.False(check.Enabled);
                Assert.Equal(1, Volatile.Read(ref calls));
                var status = modal.SubViews.OfType<Label>().Single();
                var output = modal.SubViews.OfType<Terminal.Gui.Editor.Editor>().Single();
                var previousStatus = status.Text;
                var previousOutput = output.Text;
                lifetime.TryCancel();
                commit();
                Assert.Equal(previousStatus, status.Text);
                Assert.Equal(previousOutput, output.Text);
                Assert.False(check.Enabled);
                checkedQueued = true;
                return false;
            });
            PackageCheckScreen.Show(app, new Logger(), Target("demo"), async token =>
            {
                Interlocked.Increment(ref calls);
                if (outcome == "canceled") await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                if (outcome == "failure") throw new IOException("Package check fixture failure.");
                return PackageCheckCliTests.Report();
            }, lifetime.Token, dialog => modal = dialog, action => Volatile.Write(ref queued, action));
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(checkedQueued); Assert.False(timedOut); Assert.Equal(1, calls);
    }

    [Fact]
    public void AlreadyCanceledParentDoesNotOpenDialogOrStartCapture()
    {
        using var app = Application.Create().Init("ansi");
        using var lifetime = new SkillView.Threading.CancellationSource(_ => { });
        lifetime.TryCancel();
        var calls = 0; var opened = false; var timedOut = false;
        app.AddTimeout(TimeSpan.FromSeconds(1), () => { timedOut = true; app.RequestStop(); return false; });
        PackageCheckScreen.Show(app, new Logger(), Target("demo"), _ =>
        {
            calls++; return Task.FromResult(PackageCheckCliTests.Report());
        }, lifetime.Token, _ => opened = true);
        Assert.Equal(0, calls); Assert.False(opened); Assert.False(timedOut);
    }

    [Fact]
    public async Task ClosingActivePackageCheckCancelsAndDrainsBeforeReturning()
    {
        var started = 0; var canceled = 0; var closeSent = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(100, 30);
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (Volatile.Read(ref started) == 0 || closeSent) return true;
                closeSent = true; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
            });
            PackageCheckScreen.Show(app, new Logger(), Target("demo"), async token =>
            {
                Interlocked.Increment(ref started);
                try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token); return PackageCheckCliTests.Report(); }
                finally { if (token.IsCancellationRequested) Interlocked.Increment(ref canceled); }
            });
            Assert.Equal(1, canceled);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.Equal(1, started); Assert.True(closeSent); Assert.False(timedOut);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public async Task CompletedDialogShowsIndependentAssessmentsAndUnknownDependencyEvidence(int width, int height)
    {
        var observed = false; var timedOut = false;
        var report = PackageCheckCliTests.Report() with
        {
            ReferenceStatus = "findings",
            HasErrors = true,
            Findings = [new("reference", "reference-missing", "SKILL.md", 5, "assets/missing.png", "Missing asset", "Restore the authored asset")],
            FindingCount = 1,
            AgentAssessment = new("incompatible", "Version-cited fixture rule", "root-sha", ["Known framing rejection"], []),
            Observations = [new("dependency", "dependencies", "package.json", null, "name@^2\x1b[31m", "availability-unknown", "Provide in the selected environment")],
            ObservationCount = 1
        };
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            Dialog? modal = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (modal is null || !modal.SubViews.OfType<Button>().Single(b => b.Text == "_Check again").Enabled) return true;
                var output = modal.SubViews.OfType<Terminal.Gui.Editor.Editor>().Single();
                Assert.True(output.ReadOnly);
                Assert.Contains("Format: valid; agent compatibility: incompatible; payload intact: unknown; source reviewed: not-reviewed", output.Text);
                Assert.Contains("Observed condition: local-defects-observed", output.Text);
                Assert.Contains("availability-unknown", output.Text);
                Assert.Contains("No source-review acknowledgement", output.Text);
                Assert.DoesNotContain('\x1b', output.Text);
                var status = modal.SubViews.OfType<Label>().Single().Text;
                Assert.Contains("agent: incompatible", status);
                Assert.Contains("source: not-reviewed", status);
                observed = true;
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                return false;
            });
            PackageCheckScreen.Show(app, new Logger(), Target("demo"), _ => Task.FromResult(report), readyForTests: dialog => modal = dialog);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(observed); Assert.False(timedOut);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("stale")]
    [InlineData("close")]
    [InlineData("cancel")]
    public async Task BoundFactPublishesOnlyAtOwnedCurrentTerminalCompletion(string state)
    {
        var completed = false; var published = 0; var captures = 0; var timeout = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(80, 24);
            using var lifetime = new SkillView.Threading.CancellationSource(_ => { });
            Action? queued = null; Dialog? modal = null;
            var fact = PackageCheckFactTests.Fact(PackageCheckFactTests.Report());
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timeout = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                var commit = Interlocked.Exchange(ref queued, null);
                if (commit is null || modal is null) return true;
                Assert.Equal(0, published);
                if (state == "cancel") lifetime.TryCancel();
                if (state == "close") app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                commit();
                Assert.Equal(state == "publish" ? 1 : 0, published);
                if (state == "stale")
                    Assert.Contains("Refresh Installed", modal.SubViews.OfType<Terminal.Gui.Editor.Editor>().Single().Text);
                completed = true;
                if (state is "publish" or "stale") app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                return false;
            });
            PackageCheckScreen.ShowBound(app, new Logger(), Target("demo"), _ =>
            {
                captures++;
                return Task.FromResult(new PackageCheckResult(fact.Report, fact));
            }, lifetime.Token, dialog => modal = dialog, action => Volatile.Write(ref queued, action), publish: _ =>
            {
                if (state == "stale") return false;
                published++;
                return true;
            });
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(completed); Assert.False(timeout); Assert.Equal(1, captures);
    }

    [Fact]
    public async Task CheckAgainPublishesOnlyEachRequestsCompletedFact()
    {
        var captures = 0; var publications = 0; var timeout = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(120, 36);
            Dialog? modal = null;
            var fact = PackageCheckFactTests.Fact(PackageCheckFactTests.Report());
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timeout = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (modal is null) return true;
                var check = modal.SubViews.OfType<Button>().Single(b => b.Text == "_Check again");
                if (!check.Enabled) return true;
                if (publications == 1)
                {
                    check.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                    return true;
                }
                if (publications < 2) return true;
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
            });
            PackageCheckScreen.ShowBound(app, new Logger(), Target("demo"), _ =>
            {
                Interlocked.Increment(ref captures);
                return Task.FromResult(new PackageCheckResult(fact.Report, fact with { CheckedAt = DateTimeOffset.UtcNow }));
            }, readyForTests: dialog => modal = dialog, publish: _ => { publications++; return true; });
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timeout); Assert.Equal(2, captures); Assert.Equal(2, publications);
    }

    private static InstalledSkill Target(string name) => new()
    {
        Name = name,
        ResolvedPath = "/skills/" + name,
        ScanRoot = "/skills",
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
