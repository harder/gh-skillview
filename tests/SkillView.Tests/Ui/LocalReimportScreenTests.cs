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
public sealed partial class LocalReimportScreenTests
{
    private static string NewSourcePath => Path.Combine(Path.GetTempPath(), "skillview-ui-source", "demo");

    internal static InstalledSkill Target() => new()
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

    internal static ReplacementResult Plan()
    {
        var target = Target();
        return new(new('a', 32), "planned", true, false, false, target.ResolvedPath, "/state/original", "/state/candidate", "/state/displaced",
            new('b', 64), new('c', 64), ["SKILL.md", "deleted-extra"], "Frozen local reimport", new(target.InstallationId, target.Name, target.Scope, target.Agents, target.FrontMatter, []),
            "/state/plan.json", 448, 448, LocalImport: new("/source/demo", "/canonical/source/demo", default, new('d', 64), new('e', 64), target.FrontMatter,
                "I root privacy; C descendants", [], new("/tools/gh", new('f', 64), "2.102.0", "/preview/input", "/preview/output", new('1', 64), new('2', 64), [], "child-only")))
        { LocalSource = new("current", "Source capture matches.") };
    }

    internal static ReplacementResult SourceSwitchPlan()
    {
        var plan = Plan();
        return plan with
        {
            LocalImport = plan.LocalImport! with
            {
                SourcePath = NewSourcePath,
                CanonicalSourcePath = NewSourcePath,
                DesiredMetadata = plan.Subject.Metadata with { LocalPath = NewSourcePath },
                Disposition = ReplacementPlanStore.LocalSourceSwitchDisposition
            },
            LocalSourceSwitch = new("/source/demo", new("/source/demo", "/source/demo", default, []), "/copied/old/demo")
        };
    }

    [Fact]
    public void SourceSwitchJsonAndUiKeepFamiliesDistinctAndOldFieldsOmitted()
    {
        var plan = SourceSwitchPlan();
        Assert.True(Path.IsPathFullyQualified(plan.LocalImport!.SourcePath));
        Assert.True(LocalReimportScreen.IsLocalSourceSwitch(plan)); Assert.False(LocalReimportScreen.IsLocalReimport(plan));
        Assert.False(LocalReimportScreen.IsLocalSourceSwitch(Plan()));
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(plan, ReplacementJsonContext.Default.ReplacementResult));
        Assert.Equal("/source/demo", data.RootElement.GetProperty("localSourceSwitch").GetProperty("previousRecordedPath").GetString());
        Assert.Equal("/copied/old/demo", data.RootElement.GetProperty("localSourceSwitch").GetProperty("capturedSourceRecordedPath").GetString());
        Assert.Equal(NewSourcePath, data.RootElement.GetProperty("localImport").GetProperty("desiredMetadata").GetProperty("localPath").GetString());
        foreach (var field in new[] { "revision", "pinPolicy", "localDisposition" }) Assert.False(data.RootElement.TryGetProperty(field, out _));
        var legacy = JsonSerializer.Serialize(Plan(), ReplacementJsonContext.Default.ReplacementResult);
        Assert.DoesNotContain("localSourceSwitch", legacy);
        var text = LocalReimportScreen.RenderText(plan);
        foreach (var label in new[] { "Previous recorded source", "Captured C recorded source", "no ancestry", "Git-managed sources remain unsupported" }) Assert.Contains(label, text);
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
        var target = Target(); InstalledSkill? selected = null; InstalledSkill? checkedSkill = null; InstalledSkill? switched = null; var shellReceived = false;
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(InventorySnapshot.Empty), (_, _, _) => Task.CompletedTask, () => { }, () => { },
            onPackageCheck: skill => checkedSkill = skill, onLocalReimport: skill => selected = skill, onLocalSourceSwitch: skill => switched = skill)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        view.LoadSeeded(InventorySnapshot.Empty with { Skills = [target] });
        window.Add(view);
        window.KeyDown += (_, key) => { if (!key.Handled && key.AsRune.Value is 'R' or 'K' or 'T') shellReceived = true; };
        var session = app.Begin(window);
        try
        {
            view.FocusFilterForTests(); Assert.True(view.FilterHasFocusForTests);
            Press('R'); Assert.Null(selected); Assert.Null(checkedSkill); Assert.Equal("R", view.GetFilterText());
            Press('K'); Assert.Null(selected); Assert.Null(checkedSkill); Assert.Equal("RK", view.GetFilterText());
            Press('T'); Assert.Null(switched); Assert.Null(selected); Assert.Null(checkedSkill); Assert.Equal("RKT", view.GetFilterText());
            view.SetFilterTextForTests(""); view.FocusList(); Assert.False(view.FilterHasFocusForTests);
            shellReceived = false; Press('R'); Assert.Same(target, selected); Assert.False(shellReceived);
            Assert.Null(checkedSkill); selected = null;
            Press('K'); Assert.Same(target, checkedSkill); Assert.Null(selected); Assert.False(shellReceived);
            checkedSkill = null; Press('T'); Assert.Same(target, switched); Assert.Null(checkedSkill); Assert.Null(selected); Assert.False(shellReceived);
        }
        finally { app.End(session!); }
        void Press(char value) { app.Keyboard.RaiseKeyDownEvent(new Key(value)); window.Draw(new DrawContext()); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task KeyboardApplyRequiresFreshRetentionApproval(bool sourceSwitch) => KeyboardApprovalAsync("Apply", sourceSwitch);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task KeyboardRecoverRequiresFreshRetentionApproval(bool sourceSwitch) => KeyboardApprovalAsync("Recover", sourceSwitch);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task KeyboardUndoRequiresFreshRetentionApproval(bool sourceSwitch) => KeyboardApprovalAsync("Undo", sourceSwitch);

    private async Task KeyboardApprovalAsync(string actionName, bool sourceSwitch)
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
                var plan = (sourceSwitch ? SourceSwitchPlan() : Plan()) with { Status = action == LocalReimportAction.Apply ? "planned" : "original-retained" };
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
                        var fields = dialog.SubViews.OfType<TextField>().ToArray();
                        if (sourceSwitch) fields[1].Text = plan.LocalImport!.SourcePath;
                        if (action != LocalReimportAction.Apply) fields[0].Text = plan.Id;
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
                    exactReview = request.Action == action && ReferenceEquals(plan, request.Review) && request.Id == plan.Id
                        && (!sourceSwitch || request.NewSourcePath == plan.LocalImport!.SourcePath);
                    return Task.FromResult(new LocalReimportResponse(Plan: plan with { Status = "installed" }));
                }, lifetime.Token, readyForTests: value => dialog = value, sourceSwitch: sourceSwitch);
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
    public async Task EditingNewSourceWithActualFocusInvalidatesSavedApproval()
    {
        var timedOut = false; var invalidated = false; var prepared = 0;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(100, 30);
            using var lifetime = new CancellationTokenSource();
            Dialog? dialog = null; var phase = 0;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; lifetime.Cancel(); app.RequestStop(); return true; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                var retention = dialog.SubViews.OfType<CheckBox>().Single(value => value.Text.StartsWith("Replace from", StringComparison.Ordinal));
                if (phase == 0)
                {
                    phase = 1;
                    dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Prepare").SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                }
                else if (phase == 1 && retention.Enabled)
                {
                    retention.SetFocus(); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                    var apply = dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Apply reviewed");
                    Assert.True(apply.Enabled);
                    var input = dialog.SubViews.OfType<TextField>().Last(); input.SetFocus(); Assert.True(input.HasFocus);
                    app.Keyboard.RaiseKeyDownEvent(new Key('x'));
                    Assert.False(apply.Enabled); Assert.False(retention.Enabled); Assert.Equal(CheckState.UnChecked, retention.Value);
                    invalidated = true; phase = 2; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
                }
                return true;
            });
            LocalReimportScreen.Show(app, new Logger(), Target(), (_, _) =>
            { prepared++; return Task.FromResult(new LocalReimportResponse(Plan: SourceSwitchPlan())); }, lifetime.Token,
                readyForTests: value => { dialog = value; value.SubViews.OfType<TextField>().Last().Text = NewSourcePath; }, sourceSwitch: true);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(invalidated); Assert.Equal(1, prepared); Assert.False(timedOut);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingActivePreparationCancelsAndDrainsBeforeReturning(bool sourceSwitch)
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
            }, readyForTests: value => { dialog = value; if (sourceSwitch) value.SubViews.OfType<TextField>().Last().Text = NewSourcePath; }, sourceSwitch: sourceSwitch);
            Assert.Equal(1, Volatile.Read(ref drained));
            void Press(string label)
            { dialog!.SubViews.OfType<Button>().Single(button => button.Text == label).SetFocus(); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter)); }
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.Equal(1, started); Assert.True(closing); Assert.False(timedOut);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentCancellationAfterWorkerCompletionRejectsQueuedReview(bool sourceSwitch)
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
                    var priorStatus = dialog.SubViews.OfType<Label>().Single(value => value.Text != "Saved plan ID:" && value.Text != "New source:").Text;
                    parent.Cancel(); completion();
                    lateStatusMutation = dialog.SubViews.OfType<Label>().Single(value => value.Text != "Saved plan ID:" && value.Text != "New source:").Text != priorStatus;
                    committed = dialog.SubViews.OfType<TextField>().First().Text.Length > 0;
                    return false;
                }
                return true;
            });
            LocalReimportScreen.Show(app, logger, Target(), (_, _) => Task.FromResult(new LocalReimportResponse(Plan: sourceSwitch ? SourceSwitchPlan() : Plan())),
                parent.Token, value => { dialog = value; if (sourceSwitch) value.SubViews.OfType<TextField>().Last().Text = NewSourcePath; }, action => queued.Enqueue(action), sourceSwitch: sourceSwitch);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(committed); Assert.False(lateStatusMutation); Assert.False(timedOut);
        Assert.Contains(logger.Snapshot(), entry => entry.Message.Contains(new string('a', 32), StringComparison.Ordinal) && entry.Message.Contains("/state/plan.json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Apply", false, true)]
    [InlineData("Recover", false, true)]
    [InlineData("Undo", false, true)]
    [InlineData("Apply", true, true)]
    [InlineData("Recover", true, true)]
    [InlineData("Undo", true, true)]
    [InlineData("Apply", false, false)]
    [InlineData("Apply", true, false)]
    public async Task QueuedFailedLifecycleDisplaysDiagnosticsEvenWithCurrentSavedPlan(string actionName, bool sourceSwitch, bool includeSavedPlan)
    {
        var action = Enum.Parse<LocalReimportAction>(actionName);
        var queued = new ConcurrentQueue<Action>();
        var requests = new ConcurrentQueue<LocalReimportRequest>();
        var timedOut = false; var verified = false;
        var plan = (sourceSwitch ? SourceSwitchPlan() : Plan()) with { Status = action == LocalReimportAction.Apply ? "planned" : "original-retained" };
        var explanation = "Retained original needs exact recovery " + "\u001b[31m" + new string('x', 9000) + "DIAGNOSTIC_TAIL";
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(100, 30);
            using var parent = new CancellationTokenSource();
            Dialog? dialog = null; var phase = 0;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; parent.Cancel(); app.RequestStop(); return true; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (queued.TryDequeue(out var completion)) completion();
                var retention = dialog.SubViews.OfType<CheckBox>().Single(value => value.Text.StartsWith("Replace from", StringComparison.Ordinal));
                if (phase == 0)
                {
                    phase = 1;
                    if (sourceSwitch) dialog.SubViews.OfType<TextField>().Last().Text = plan.LocalImport!.SourcePath;
                    if (action != LocalReimportAction.Apply) dialog.SubViews.OfType<TextField>().First().Text = plan.Id;
                    Press(action == LocalReimportAction.Apply ? "_Prepare" : "_Inspect saved ID");
                }
                else if (phase == 1 && retention.Enabled)
                {
                    retention.SetFocus(); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
                    phase = 2; Press("_" + actionName + " reviewed");
                }
                else if (phase == 2 && requests.Count == 2 && dialog.SubViews.OfType<Button>().Single(value => value.Text == "_Prepare").Enabled)
                {
                    var text = dialog.SubViews.OfType<Editor>().Single().Text;
                    var status = dialog.SubViews.OfType<Label>().Single(value => value.Text != "Saved plan ID:" && value.Text != "New source:").Text;
                    Assert.Contains("Retained original needs exact recovery", text);
                    Assert.Contains("Failed/refused", status);
                    Assert.DoesNotContain('\u001b', text); Assert.DoesNotContain("DIAGNOSTIC_TAIL", text);
                    Assert.Contains("omitted", text);
                    if (action == LocalReimportAction.Apply)
                    {
                        Assert.Contains(sourceSwitch ? "Local source switch results" : "Local reimport results", text);
                        Assert.Contains("Reconciliation needs fresh inventory", text);
                        Assert.Contains("Child stderr retained", text);
                    }
                    Assert.Equal(plan.Id, dialog.SubViews.OfType<TextField>().First().Text);
                    Assert.Equal(CheckState.UnChecked, retention.Value);
                    foreach (var name in new[] { "_Apply reviewed", "_Recover reviewed", "_Undo reviewed" })
                        Assert.False(dialog.SubViews.OfType<Button>().Single(value => value.Text == name).Enabled);
                    verified = true; phase = 3; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
                }
                return true;
                void Press(string name)
                {
                    var button = dialog.SubViews.OfType<Button>().Single(value => value.Text == name);
                    button.SetFocus(); Assert.True(button.HasFocus); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                }
            });
            LocalReimportScreen.Show(app, new Logger(), Target(), (request, _) =>
            {
                requests.Enqueue(request);
                if (request.Action is LocalReimportAction.Prepare or LocalReimportAction.Inspect)
                    return Task.FromResult(new LocalReimportResponse(Plan: plan));
                Assert.Same(plan, request.Review); Assert.Equal(plan.Id, request.Id); Assert.Equal(action, request.Action);
                if (action != LocalReimportAction.Apply)
                    return Task.FromResult(new LocalReimportResponse(Plan: plan with { Succeeded = false, Status = "needs-recovery", Message = explanation }));
                var failed = UpdateResult.Failure(false, 2, "Child stderr retained", []) with
                {
                    SavedPlan = includeSavedPlan ? plan : null,
                    ErrorMessage = explanation,
                    ReconciliationPartial = true,
                    Notices = [new("reconciliation", "Reconciliation needs fresh inventory")]
                };
                return Task.FromResult(new LocalReimportResponse(Preparation: failed));
            }, parent.Token, value => dialog = value, callback => queued.Enqueue(callback), sourceSwitch: sourceSwitch);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(verified); Assert.False(timedOut); Assert.Equal(2, requests.Count);
    }
}
