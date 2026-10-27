using SkillView.Inventory.Models;
using SkillView.Cli;
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
                Assert.Contains("1 Problem · 0 Reviews", status);
                Assert.Contains("Local package check", status);
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

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task CheckAgainPublishesOnlyEachRequestsCompletedFactAndResetsReportTop(int width, int height)
    {
        var captures = 0; var publications = 0; var timeout = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            Dialog? modal = null;
            var fact = PackageCheckFactTests.Fact(PackageCheckFactTests.Report());
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timeout = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (modal is null) return true;
                var check = modal.SubViews.OfType<Button>().Single(b => b.Text == "_Check again");
                if (!check.Enabled) return true;
                modal.Layout(); modal.Draw(new DrawContext());
                var output = modal.SubViews.OfType<Terminal.Gui.Editor.Editor>().Single();
                Assert.Equal(0, output.CaretOffset);
                Assert.Equal(0, output.Viewport.Y);
                Assert.Equal(0, output.Viewport.X);
                Assert.StartsWith("Package check summary", output.Text);
                if (publications == 1)
                {
                    output.CaretOffset = output.Document!.TextLength;
                    Assert.True(output.Viewport.Y > 0);
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

    [Theory]
    [InlineData(80, 24, false)]
    [InlineData(120, 36, false)]
    [InlineData(80, 24, true)]
    [InlineData(120, 36, true)]
    public async Task RetainedReportOpensAtSummaryTopAfterLayoutWithLiteralCopyIdentity(int width, int height, bool maliciousPath)
    {
        var observed = false; var timeout = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            Dialog? modal = null;
            var path = maliciousPath ? "/skills/```\n## forged\rScope: forged\t\x1b[31m" + new string('x', 500) + "/exact-copy" : "/skills/exact-copy";
            var fact = PackageCheckFactTests.Fact(PackageCheckFactTests.Report() with { Path = path });
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timeout = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (modal is null) return true;
                modal.Layout(); modal.Draw(new DrawContext());
                var output = modal.SubViews.OfType<Terminal.Gui.Editor.Editor>().Single();
                Assert.Equal(0, output.CaretOffset); Assert.Equal(0, output.Viewport.Y); Assert.Equal(0, output.Viewport.X);
                Assert.StartsWith("Last explicit package check", output.Text);
                var header = output.Text[..output.Text.IndexOf("Package check summary", StringComparison.Ordinal)];
                var headerLines = header.Split('\n');
                Assert.Equal(4, headerLines.Length);
                Assert.Contains(fact.CheckedAt.ToString("O"), headerLines[0]);
                Assert.Equal("Copy: " + InstalledOverview.Text(path, 240), headerLines[1]);
                Assert.InRange(headerLines[1].Length, 1, 246);
                Assert.Equal("Scope: User · captured local package only", headerLines[2]);
                Assert.DoesNotContain('\x1b', header);
                Assert.DoesNotContain('\r', header); Assert.DoesNotContain('\t', header);
                Assert.DoesNotContain("\n## forged", header);
                if (maliciousPath) Assert.Contains("```\\n## forged\\rScope: forged\\t", headerLines[1]);
                else Assert.Equal("Copy: /skills/exact-copy", headerLines[1]);
                Assert.Equal(path, fact.Request.Target.ResolvedPath);
                Assert.True(output.Text.IndexOf("Package check summary", StringComparison.Ordinal) < output.Text.IndexOf("Full package evidence", StringComparison.Ordinal));
                Assert.Contains(PackageCheckFact.InventoryLifetimeNotice, output.Text);
                // Later layouts must preserve deliberate browsing, rather than
                // forcing a retained report back to the top on every draw.
                output.CaretOffset = output.Document!.TextLength;
                var offset = output.Viewport.Y;
                Assert.True(offset > 0);
                modal.SetNeedsLayout(); modal.Layout(); modal.Draw(new DrawContext());
                Assert.Equal(offset, output.Viewport.Y);
                observed = true; app.Keyboard.RaiseKeyDownEvent(Key.Esc); return false;
            });
            PackageCheckScreen.ShowRetained(app, fact, TestContext.Current.CancellationToken, dialog => modal = dialog);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(observed); Assert.False(timeout);
    }

    [Theory]
    [InlineData(0, "0 Problems · 0 Reviews · 0 Info")]
    [InlineData(1, "1 Problem · 1 Review · 1 Info")]
    [InlineData(2, "2 Problems · 2 Reviews · 2 Info")]
    public void PackageCountLabelsUseSingularForOneWithoutChangingClassification(int count, string expected)
    {
        var report = PackageCheckFactTests.Report() with
        {
            Findings = [.. Enumerable.Range(0, count).Select(_ => new PackageCheckFinding("reference", "reference-missing", "SKILL.md", 1, "references/missing.md", "Fixture", "Review"))],
            FindingCount = count,
            Observations = [.. Enumerable.Range(0, count).SelectMany(_ => new[]
            {
                new PackageObservation("declaration", "tool", "SKILL.md", 2, "literal", "review", "Review it"),
                new PackageObservation("dependency", "dependency", "package.json", null, "literal", "availability-unknown", "Unknown")
            })],
            ObservationCount = 2 * count
        };
        Assert.Contains(expected, PackageCheckScreen.RenderReport(report));
        var fact = PackageCheckFactTests.Fact(report);
        Assert.Contains(expected, fact.Detail);
        Assert.Equal(count, fact.Problems); Assert.Equal(count, fact.Reviews); Assert.Equal(count, fact.Infos);
    }

    [Fact]
    public void SummaryReusesStructuredClassificationAndKeepsFullBoundedEvidenceBelow()
    {
        var report = PackageCheckFactTests.Report() with
        {
            HasErrors = false,
            Findings = [new("metadata", "invalid", "SKILL.md", null, null, "Problem misleading", "Review metadata"),
                new("Reference", "reference-missing", "SKILL.md", 4, "assets/case.png", "Missing", "Review"),
                .. Enumerable.Range(0, 12).Select(i => new PackageCheckFinding("reference", "reference-missing", "SKILL.md", i + 1,
                    "references/```\n## forged\x1b[31m-" + i + ".md", "Fixture missing " + i, "Fixture guidance " + i))],
            FindingCount = 17,
            OmittedFindingCount = 3,
            Observations = [new("declaration", "tool", "other.md", 2, "literal", "review", "Review it"),
                new("dependency", "dependency", "package.json", null, "name@^2", "availability-unknown", "Unknown"),
                .. Enumerable.Range(0, 5).Select(i => new PackageObservation("network", "literal-url", "other.md", i, "URL " + i, "text-only", "No request"))],
            ObservationCount = 9
        };
        var rendered = PackageCheckScreen.RenderReport(report);
        var summary = rendered[..rendered.IndexOf("Full package evidence", StringComparison.Ordinal)];
        Assert.Contains("12 Problems · 3 Reviews · 6 Info", summary);
        Assert.Contains("Package checks incomplete", summary);
        Assert.True(summary.IndexOf("Problem: Missing file", StringComparison.Ordinal) < summary.IndexOf("Review: other.md", StringComparison.Ordinal));
        Assert.DoesNotContain("Problem misleading", summary);
        Assert.DoesNotContain("\n## forged", summary);
        Assert.DoesNotContain('\x1b', rendered);
        Assert.Contains("9 retained findings/observations", summary);
        Assert.Contains("omitted 3 findings and 2 observations", summary);
        Assert.EndsWith(CliDispatcher.RenderPackageCheckText(report), rendered);
        Assert.Contains("Fixture missing 11", rendered);
        Assert.Contains("Fixture guidance 11", rendered);
        Assert.Contains("URL 4", rendered);
        Assert.Contains("availability-unknown", rendered);
        Assert.Contains("Source integrity, dependency availability and effective agent compatibility remain unknown", summary);
        var noFindings = PackageCheckScreen.RenderReport(PackageCheckFactTests.Report() with { HasErrors = true });
        Assert.Contains("0 Problems · 0 Reviews", noFindings);
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
