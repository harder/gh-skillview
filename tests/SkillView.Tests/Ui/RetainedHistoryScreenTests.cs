using System.Collections.Concurrent;
using SkillView.Logging;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Threading;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class RetainedHistoryScreenTests
{
    private static readonly string First = new('a', 32);
    private static readonly string Second = new('b', 32);
    private static ReplacementHistory Sample(bool reversed = false) => new(reversed
        ? [new(Second, "Protected — Inspect to verify", true, "second"), new(First, "Unapplied review (hint) — Inspect to verify", false, "first")]
        : [new(First, "Unapplied review (hint) — Inspect to verify", false, "first"), new(Second, "Protected — Inspect to verify", true, "second")], 2, false);

    private static ReplacementHistory UnknownRootHistory() => new(
        [new("unknown-entry", "Protected — Inspect to verify", false, Detail: "Unknown state entry/type; preserved without following it.")], 1, false);

    [Fact]
    public void DetailLiteralsReplaceControlsAndPreserveTrustedMultilineAndSourceValues()
    {
        var source = "literal\0\v\f\u007f\u0085\r\nnext\rlast\ttab\u001b[31m red\u001b[0m";
        var entry = new ReplacementHistoryEntry(First, "Protected — Inspect to verify", false,
            Source: source, OriginalPath: source, Detail: source);
        var rendered = RetainedHistoryScreen.RenderEntry(entry);
        Assert.Contains("literal\ufffd\ufffd\ufffd\ufffd\ufffd\nnext\nlast\ttab red", rendered);
        Assert.Contains("\nID: " + First + "\nSkill:", rendered);
        Assert.EndsWith("Inspect to verify. Retirement is offered only after fresh verification; recovery and Undo use their specialist workflows.", rendered);
        Assert.All(rendered, character => Assert.False(char.IsControl(character) && character is not ('\n' or '\t')));
        Assert.Equal(source, entry.Source); Assert.Equal(source, entry.OriginalPath); Assert.Equal(source, entry.Detail);
        var exception = new IOException(source);
        Assert.Equal(InstallFailureDetails.Literal(exception.Message), RetainedHistoryScreen.Literal(exception.Message));
        Assert.Equal(source, exception.Message);
        Assert.Equal("```\n[link](https://invalid.example/)\n\tstill plain", RetainedHistoryScreen.Literal("```\n[link](https://invalid.example/)\n\tstill plain"));
    }

    [Fact]
    public void DetailLiteralBoundsSourceBeforeSanitizingWithoutSplittingSurrogatePair()
    {
        var source = new string('x', 31999) + "\U0001f642tail";
        var rendered = RetainedHistoryScreen.Literal(source);
        Assert.Equal(new string('x', 31999) + " [truncated]", rendered);
        Assert.Equal(32011, rendered.Length);
        Assert.True(char.IsHighSurrogate(source[31999]));
        var controls = new string('\0', 32001);
        Assert.Equal(new string('\ufffd', 32000) + " [truncated]", RetainedHistoryScreen.Literal(controls));
        Assert.Equal(32001, controls.Length);
        Assert.Equal("1234567 [truncated]", RetainedHistoryScreen.Literal("1234567\U0001f642tail", 8));
    }

    [Fact]
    public void ProtectedAttentionDoesNotBecomeRecoveryOrHideEitherKind()
    {
        var unknown = UnknownRootHistory();
        Assert.Equal("History: protected (F4)", SkillViewApp.HistoryHintText(unknown));
        Assert.False(Assert.Single(unknown.Entries).MayNeedRecovery);
        Assert.Contains("0 shown may need recovery", unknown.Summary);
        var recovery = new ReplacementHistory([new(First, "May need recovery (hint) — Inspect to verify", true)], 1, false);
        Assert.Equal("History: 1 recovery hints F4", SkillViewApp.HistoryHintText(recovery));
        var both = unknown with { Entries = [.. unknown.Entries, .. recovery.Entries], ObservedCount = 2 };
        Assert.Equal("History: protected +hint F4", SkillViewApp.HistoryHintText(both));
        Assert.Equal(1, both.Entries.Count(entry => entry.MayNeedRecovery));
        Assert.Contains("1 shown may need recovery", both.Summary);
        Assert.InRange(SkillViewApp.HistoryHintText(both).Length, 1, 28);
        Assert.Equal("History ? (F4)", SkillViewApp.HistoryHintText(unknown with { Error = "lock busy" }));
        Assert.Equal("History 16+ (F4)", SkillViewApp.HistoryHintText(new([], 17, true)));
        Assert.Equal("History (F4)", SkillViewApp.HistoryHintText(new([], 0, false)));
    }

    [Theory]
    [InlineData(80, 24, false)]
    [InlineData(120, 36, false)]
    [InlineData(80, 24, true)]
    [InlineData(120, 36, true)]
    public async Task KeyboardRefreshKeepsSelectedIdAndInspectResultsStayReadOnly(int width, int height, bool success)
    {
        var tasks = new List<Task>(); var stage = 0; var loads = 0; var inspections = 0; var timedOut = false;
        Dialog? dialog = null;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            app.AddTimeout(TimeSpan.FromSeconds(10), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                var list = dialog.SubViews.OfType<ListView>().Single();
                var detail = dialog.SubViews.OfType<Editor>().Single();
                var inspect = dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Inspect");
                if (!inspect.Enabled) return true;
                dialog.Draw(new DrawContext());
                if (stage == 0)
                {
                    list.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.CursorDown);
                    Assert.Equal(1, list.SelectedItem);
                    var refresh = dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Refresh");
                    refresh.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); stage++;
                }
                else if (stage == 1 && loads == 2 && detail.Text.Contains("second", StringComparison.Ordinal))
                {
                    Assert.Equal(0, list.SelectedItem);
                    Assert.Equal(0, detail.CaretOffset); Assert.Equal(0, detail.Viewport.Y);
                    inspect.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); stage++;
                }
                else if (stage == 2 && detail.Text.Contains("fresh inspection fixture", StringComparison.Ordinal))
                {
                    Assert.Equal(1, inspections); Assert.DoesNotContain("Apply", string.Join(' ', dialog.SubViews.OfType<Button>().Select(button => button.Text)));
                    if (success) Assert.Contains("Current inspection:", detail.Text);
                    dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Refresh").SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(Key.Enter); stage++;
                }
                else if (stage == 3 && loads == 3 && detail.Text.Contains("second", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("Current inspection:", detail.Text);
                    Assert.Contains("Inspect to verify", detail.Text);
                    app.Keyboard.RaiseKeyDownEvent(Key.Esc); stage++;
                }
                return true;
            });
            RetainedHistoryScreen.Show(app, new Logger(), _ => Task.FromResult(Sample(Interlocked.Increment(ref loads) > 1)),
                (id, _) =>
                {
                    Assert.Equal(Second, id); Interlocked.Increment(ref inspections);
                    if (!success) throw new IOException("fresh inspection fixture");
                    return Task.FromResult(new ReplacementResult(id, "ready", true, false, false, "/fixture/second", "/state/original", "/state/candidate", "/state/displaced",
                        new('c', 64), new('d', 64), [], "fresh inspection fixture",
                        new("installation", "second", Scope.Custom, [], SkillFrontMatter.Empty, []), "/state/plan.json", 448, 448));
                },
                (work, _) => { tasks.Add(Task.Run(work)); return true; }, TestContext.Current.CancellationToken, readyForTests: value => dialog = value);
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.Equal(4, stage); Assert.Equal(3, loads); Assert.Equal(1, inspections);
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("shutdown")]
    [InlineData("reload")]
    public async Task QueuedOldCompletionsCannotPublishAfterLeaveShutdownOrReload(string mode)
    {
        var tasks = new List<Task>();
        var queue = new ConcurrentQueue<Action>();
        var observed = 0; var calls = 0; var checkedLate = false; var timedOut = false;
        string? hint = null;
        Action? stale = null; string? before = null;
        Dialog? dialog = null;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            using var lifetime = new CancellationSource(_ => { });
            app.Driver!.SetScreenSize(80, 24);
            app.AddTimeout(TimeSpan.FromSeconds(10), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (stale is null)
                {
                    if (!queue.TryDequeue(out stale)) return true;
                    before = dialog.SubViews.OfType<Editor>().Single().Text;
                    if (mode == "shutdown") lifetime.TryCancel();
                    else if (mode == "leave") app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                    else
                    {
                        dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Refresh").SetFocus();
                        app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                    }
                }
                if (mode != "reload")
                {
                    stale(); Assert.Equal(0, observed); Assert.Null(hint); Assert.Equal(before, dialog.SubViews.OfType<Editor>().Single().Text);
                    checkedLate = true; return false;
                }
                if (!queue.TryDequeue(out var fresh)) return true;
                fresh(); Assert.Equal(1, observed); Assert.Equal("History: protected (F4)", hint);
                var text = dialog.SubViews.OfType<Editor>().Single().Text;
                stale(); Assert.Equal(1, observed); Assert.Equal("History: protected (F4)", hint); Assert.Equal(text, dialog.SubViews.OfType<Editor>().Single().Text);
                checkedLate = true; app.Keyboard.RaiseKeyDownEvent(Key.Esc); return false;
            });
            RetainedHistoryScreen.Show(app, new Logger(), _ => Task.FromResult(Interlocked.Increment(ref calls) > 1 ? UnknownRootHistory() : Sample()),
                (_, _) => throw new InvalidOperationException("No inspect requested"),
                (work, _) => { tasks.Add(Task.Run(work)); return true; }, lifetime.Token, history => { observed++; hint = SkillViewApp.HistoryHintText(history); }, value => dialog = value,
                action => queue.Enqueue(action));
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.True(checkedLate);
    }

    [Fact]
    public void AlreadyCanceledHistoryDoesNotOpenOrRead()
    {
        using var app = Application.Create().Init("ansi");
        RetainedHistoryScreen.Show(app, new Logger(), _ => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(), new CancellationToken(true));
    }

    [Theory]
    [InlineData(80, 24, "abandon-review", false)]
    [InlineData(120, 36, "abandon-review", true)]
    [InlineData(80, 24, "discard-undo", true)]
    [InlineData(120, 36, "discard-undo", false)]
    public void RetirementApprovalIsReadableAndDefaultsToCancel(int width, int height, string kind, bool approve)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        Dialog? dialog = null;
        var timedOut = false;
        var review = new RetirementReview(First, kind, new string('f', 64), false, MissingInstalledCapture: kind == "abandon-review");
        app.AddTimeout(TimeSpan.FromSeconds(10), () => { timedOut = true; app.RequestStop(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
        {
            if (dialog is null) return true;
            dialog.Draw(new DrawContext());
            var text = dialog.SubViews.OfType<Editor>().Single();
            Assert.Contains(review.Consequence, text.Text);
            if (kind == "abandon-review") Assert.Contains("It may be the only remaining copy.", text.Text);
            Assert.Contains(First, text.Text); Assert.Contains(review.Fingerprint, text.Text);
            var buttons = dialog.Buttons.ToArray();
            Assert.True(buttons.Single(button => button.Text == "_Cancel").IsDefault);
            var confirmation = buttons.Single(button => button.Text == review.Action);
            Assert.False(confirmation.IsDefault);
            if (approve) confirmation.SetFocus();
            app.Keyboard.RaiseKeyDownEvent(Key.Enter);
            return false;
        });
        var accepted = RetainedHistoryScreen.ConfirmRetirement(app, review, value => dialog = value);
        Assert.False(timedOut); Assert.Equal(approve, accepted);
    }

    [Fact]
    public async Task LateInspectionCannotEnableRetirementForAnotherSelectedIdAndRefreshClearsIt()
    {
        var queue = new ConcurrentQueue<Action>(); var tasks = new List<Task>();
        Dialog? dialog = null; var stage = 0; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(80, 24);
            app.AddTimeout(TimeSpan.FromSeconds(10), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null || !queue.TryDequeue(out var completion)) return true;
                var inspect = dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Inspect");
                var action = dialog.SubViews.OfType<Button>().Last();
                if (stage == 0)
                {
                    completion(); inspect.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                }
                else if (stage == 1)
                {
                    var list = dialog.SubViews.OfType<ListView>().Single();
                    list.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.CursorDown);
                    completion(); Assert.False(action.Visible); Assert.False(action.Enabled);
                    Assert.Contains(Second, dialog.SubViews.OfType<Editor>().Single().Text);
                    inspect.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                }
                else if (stage == 2)
                {
                    completion(); Assert.True(action.Visible); Assert.True(action.Enabled);
                    Assert.Contains(Second, dialog.SubViews.OfType<Editor>().Single().Text);
                    dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Refresh").SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                    Assert.False(action.Visible); Assert.False(action.Enabled);
                }
                else
                {
                    completion(); Assert.False(action.Visible); app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                }
                stage++; return stage < 4;
            });
            RetainedHistoryScreen.Show(app, new Logger(), _ => Task.FromResult(Sample()), (id, _) =>
                Task.FromResult(new ReplacementResult(id, "ready", true, false, false, "/fixture/target", "/state/original", "/state/candidate", "/state/displaced",
                    new('c', 64), new('d', 64), [], "verified fixture", new("installation", "fixture", Scope.Custom, [], SkillFrontMatter.Empty, []), "/state/plan.json", 448, 448)
                { Retirement = new(id, "abandon-review", new('f', 64), false) }),
                (work, _) => { tasks.Add(Task.Run(work)); return true; }, TestContext.Current.CancellationToken,
                readyForTests: value => dialog = value, dispatchForTests: value => queue.Enqueue(value),
                retire: (_, _) => throw new InvalidOperationException("No retirement was approved"));
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.Equal(4, stage);
    }
}
