using System.Collections.Concurrent;
using System.Text.Json;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class LocalReimportScreenTests
{
    private static InstalledSkill Target() => new()
    {
        Name = "demo",
        ResolvedPath = "/skills/demo",
        ScanRoot = "/skills",
        Scope = Scope.Custom,
        Agents = [new("claude", "/claude/demo", true), new("codex", "/codex/demo", true)],
        FrontMatter = SkillFrontMatter.Empty with { LocalPath = "/source/demo" },
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };

    private static ReplacementResult Plan()
    {
        var target = Target();
        return new(new('a', 32), "planned", true, false, false, target.ResolvedPath, "/state/original", "/state/candidate", "/state/displaced",
            new('b', 64), new('c', 64), ["SKILL.md", "deleted-extra"], "Frozen local reimport", new(target.InstallationId, target.Name, target.Scope, target.Agents, target.FrontMatter, []),
            "/state/plan.json", 448, 448, LocalImport: new("/source/demo", "/canonical/source/demo", default, new('d', 64), new('e', 64), target.FrontMatter,
                "I root privacy; C descendants", [], new("/tools/gh", new('f', 64), "2.102.0", "/preview/input", "/preview/output", new('1', 64), new('2', 64), [], "child-only")))
        { LocalSource = new("current", "Source capture matches.") };
    }

    [Fact]
    public void JsonAndReviewExposeLocalEvidenceWithoutRevisionAndRejectOtherFamilies()
    {
        var plan = Plan();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(plan, ReplacementJsonContext.Default.ReplacementResult));
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("localImport", out var local));
        Assert.Equal("replace-local-retain-original", local.GetProperty("disposition").GetString());
        Assert.Equal("current", root.GetProperty("localSource").GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("revision", out _));
        Assert.False(root.TryGetProperty("pinPolicy", out _));
        Assert.False(root.TryGetProperty("localDisposition", out _));
        Assert.True(LocalReimportScreen.IsLocalReimport(plan));
        Assert.False(LocalReimportScreen.IsLocalReimport(plan with { LocalImport = null }));
        var rendered = LocalReimportScreen.RenderText(plan);
        foreach (var fact in new[] { "Installed archive I", "Source archive C", "Baseline-less", "claude", "codex", "deleted-extra", "first retain", "frozen evidence", "root privacy" })
            Assert.Contains(fact, rendered);
        Assert.DoesNotContain("**Commit:**", rendered);
    }

    [Fact]
    public void ActualKeyboardRoutesSelectedRowAndLeavesFilterTypingWithFilter()
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(100, 26);
        var target = Target(); InstalledSkill? selected = null; InstalledSkill? checkedSkill = null; var shellReceived = false;
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(InventorySnapshot.Empty), (_, _, _) => Task.CompletedTask, () => { }, () => { },
            onPackageCheck: skill => checkedSkill = skill, onLocalReimport: skill => selected = skill)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        view.LoadSeeded(InventorySnapshot.Empty with { Skills = [target] });
        window.Add(view);
        window.KeyDown += (_, key) => { if (!key.Handled && key.AsRune.Value is 'R' or 'K') shellReceived = true; };
        var session = app.Begin(window);
        try
        {
            view.FocusFilterForTests(); Assert.True(view.FilterHasFocusForTests);
            Press('R'); Assert.Null(selected); Assert.Null(checkedSkill); Assert.Equal("R", view.GetFilterText());
            Press('K'); Assert.Null(selected); Assert.Null(checkedSkill); Assert.Equal("RK", view.GetFilterText());
            view.SetFilterTextForTests(""); view.FocusList(); Assert.False(view.FilterHasFocusForTests);
            shellReceived = false; Press('R'); Assert.Same(target, selected); Assert.False(shellReceived);
            Assert.Null(checkedSkill); selected = null;
            Press('K'); Assert.Same(target, checkedSkill); Assert.Null(selected); Assert.False(shellReceived);
        }
        finally { app.End(session!); }
        void Press(char value) { app.Keyboard.RaiseKeyDownEvent(new Key(value)); window.Draw(new DrawContext()); }
    }

    [Fact]
    public Task KeyboardApplyRequiresFreshRetentionApproval() => KeyboardApprovalAsync("Apply");
    [Fact]
    public Task KeyboardRecoverRequiresFreshRetentionApproval() => KeyboardApprovalAsync("Recover");
    [Fact]
    public Task KeyboardUndoRequiresFreshRetentionApproval() => KeyboardApprovalAsync("Undo");

    private async Task KeyboardApprovalAsync(string actionName)
    {
        var action = Enum.Parse<LocalReimportAction>(actionName);
        var requests = new ConcurrentQueue<LocalReimportRequest>(); var timedOut = false; var exactReview = false;
        var trace = new ConcurrentQueue<string>();
        var logger = new Logger(LogLevel.Debug);
        try
        {
            await Task.Run(() =>
            {
                using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(100, 30);
                using var lifetime = new CancellationTokenSource();
                Dialog? dialog = null; var phase = 0;
                var plan = Plan() with { Status = action == LocalReimportAction.Apply ? "planned" : "original-retained" };
                var actionLabel = action == LocalReimportAction.Apply ? "_Apply reviewed" : action == LocalReimportAction.Recover ? "_Recover reviewed" : "_Undo reviewed";
                app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; lifetime.Cancel(); app.RequestStop(); return true; });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (dialog is null) return true;
                    if (timedOut) { app.RequestStop(); return true; }
                    var button = dialog.SubViews.OfType<Button>().Single(value => value.Text == actionLabel);
                    if (trace.Count < 100) trace.Enqueue($"phase={phase}; requests={requests.Count}; actionEnabled={button.Enabled}; status={dialog.SubViews.OfType<Label>().Last().Text}");
                    if (phase == 0)
                    {
                        phase = 1;
                        if (action != LocalReimportAction.Apply) dialog.SubViews.OfType<TextField>().Single().Text = plan.Id;
                        Press(action == LocalReimportAction.Apply ? "_Prepare" : "_Inspect saved ID");
                    }
                    else if (phase == 1 && dialog.SubViews.OfType<CheckBox>().Single(value => value.Text.StartsWith("Replace from", StringComparison.Ordinal)).Enabled)
                    {
                        Assert.False(button.Enabled); Assert.Single(requests);
                        var retention = dialog.SubViews.OfType<CheckBox>().Single(value => value.Text.StartsWith("Replace from", StringComparison.Ordinal));
                        retention.SetFocus(); Assert.True(retention.HasFocus);
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                        Assert.True(button.Enabled); phase = 2; Press(actionLabel);
                    }
                    else if (phase == 2 && requests.Count == 2 && dialog.SubViews.OfType<Button>().Single(value => value.Text == "_Prepare").Enabled)
                    {
                        Assert.False(button.Enabled); phase = 3; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
                    }
                    return true;
                });
                LocalReimportScreen.Show(app, logger, Target(), (request, _) =>
                {
                    trace.Enqueue("executor: " + request.Action);
                    requests.Enqueue(request);
                    if (request.Action is LocalReimportAction.Prepare or LocalReimportAction.Inspect)
                        return Task.FromResult(new LocalReimportResponse(Plan: plan));
                    exactReview = request.Action == action && ReferenceEquals(plan, request.Review) && request.Id == plan.Id;
                    return Task.FromResult(new LocalReimportResponse(Plan: plan with { Status = "installed" }));
                }, lifetime.Token, readyForTests: value => dialog = value);
                void Press(string label)
                {
                    var button = dialog!.SubViews.OfType<Button>().Single(value => value.Text == label);
                    button.SetFocus(); Assert.True(button.HasFocus); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                }
            }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(actionName + " keyboard trace: " + string.Join(" | ", trace.TakeLast(15))
                + " | logs: " + string.Join(" | ", logger.Snapshot().Select(entry => entry.Message)), exception);
        }
        Assert.Equal(2, requests.Count); Assert.True(exactReview); Assert.False(timedOut);
    }

    [Fact]
    public async Task ClosingActivePreparationCancelsAndDrainsBeforeReturning()
    {
        var started = 0; var drained = 0; var closing = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(100, 30);
            Dialog? dialog = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (Volatile.Read(ref started) == 0) Press("_Prepare");
                else if (!closing) { closing = true; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); }
                return true;
            });
            LocalReimportScreen.Show(app, new Logger(), Target(), async (_, token) =>
            {
                Interlocked.Increment(ref started);
                try { return await new TaskCompletionSource<LocalReimportResponse>().Task.WaitAsync(token); }
                finally { Interlocked.Increment(ref drained); }
            }, readyForTests: value => dialog = value);
            Assert.Equal(1, Volatile.Read(ref drained));
            void Press(string label)
            { dialog!.SubViews.OfType<Button>().Single(button => button.Text == label).SetFocus(); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter)); }
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.Equal(1, started); Assert.True(closing); Assert.False(timedOut);
    }

    [Fact]
    public async Task ParentCancellationAfterWorkerCompletionRejectsQueuedReview()
    {
        var queued = new ConcurrentQueue<Action>(); var committed = false; var lateStatusMutation = false; var timedOut = false;
        var logger = new Logger();
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(100, 30);
            using var parent = new CancellationTokenSource();
            Dialog? dialog = null; var started = false;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (!started)
                {
                    started = true; dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Prepare").SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                }
                else if (queued.TryDequeue(out var completion))
                {
                    var priorStatus = dialog.SubViews.OfType<Label>().Single(value => value.Text != "Saved plan ID:").Text;
                    parent.Cancel(); completion();
                    lateStatusMutation = dialog.SubViews.OfType<Label>().Single(value => value.Text != "Saved plan ID:").Text != priorStatus;
                    committed = dialog.SubViews.OfType<TextField>().Single().Text.Length > 0;
                    return false;
                }
                return true;
            });
            LocalReimportScreen.Show(app, logger, Target(), (_, _) => Task.FromResult(new LocalReimportResponse(Plan: Plan())),
                parent.Token, value => dialog = value, action => queued.Enqueue(action));
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(committed); Assert.False(lateStatusMutation); Assert.False(timedOut);
        Assert.Contains(logger.Snapshot(), entry => entry.Message.Contains(new string('a', 32), StringComparison.Ordinal) && entry.Message.Contains("/state/plan.json", StringComparison.Ordinal));
    }
}
