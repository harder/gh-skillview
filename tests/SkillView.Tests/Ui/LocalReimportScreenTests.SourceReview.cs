using System.Collections.Concurrent;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class LocalReimportScreenTests
{
    [Theory]
    [InlineData(80, 24, false)]
    [InlineData(80, 24, true)]
    [InlineData(120, 36, false)]
    [InlineData(120, 36, true)]
    public async Task ReviewBodyTabTraversalRequiresSeparateConsentInEitherOrder(int columns, int rows, bool retentionFirst)
    {
        var report = new SourceReviewReport([], 0, 0, new("limited", [], 0, [], 0), new("limited", [], 0, [], 0), [], 0, "Frozen presentation fixture; no native authority");
        var plan = Plan() with
        {
            LocalImport = null,
            LocalSource = null,
            Purpose = ReplacementPlanStore.RegistryRestorePurpose,
            RegistryRestore = new(SkillView.Inventory.Models.ValidityState.MissingSkillMd,
                SkillView.Inventory.Models.SkillFrontMatter.Empty with { Name = "demo" }, null, false),
            SourceReview = new(1, "review-installed-candidate", "review-source-candidate", new('1', 64), new('2', 64), SourceUpdateReview.Hash(report), report)
        };
        var queued = new ConcurrentQueue<Action>();
        var actions = new List<LocalReimportAction>();
        var verified = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(columns, rows);
            Dialog? dialog = null; var stage = 0;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (queued.TryDequeue(out var completion)) completion();
                var body = dialog.SubViews.OfType<Editor>().Single();
                var source = dialog.SubViews.OfType<CheckBox>().Single(value => value.Text.StartsWith("I reviewed", StringComparison.Ordinal));
                var retention = dialog.SubViews.OfType<CheckBox>().Single(value => value.Text.StartsWith("Replace from", StringComparison.Ordinal));
                var hidden = dialog.SubViews.OfType<CheckBox>().Single(value => value.Text == "Include hidden directories");
                var id = dialog.SubViews.OfType<TextField>().Single();
                Button Button(string text) => dialog.SubViews.OfType<Button>().Single(value => value.Text == text);
                var prepare = Button("_Prepare"); var inspect = Button("_Inspect saved ID");
                var apply = Button("_Apply reviewed"); var undo = Button("_Undo reviewed"); var close = Button("_Close / cancel");
                void Press(KeyCode key) => app.Keyboard.RaiseKeyDownEvent(new Key(key));
                void Move(KeyCode key, View destination) { Press(key); Assert.True(destination.HasFocus, $"{key} should focus {destination.GetType().Name}: {destination.Text}"); }
                void Unapproved()
                {
                    Assert.Equal(CheckState.UnChecked, source.Value); Assert.Equal(CheckState.UnChecked, retention.Value);
                    Assert.False(apply.Enabled); Assert.False(undo.Enabled);
                    Assert.DoesNotContain(actions, action => action is LocalReimportAction.Apply or LocalReimportAction.Recover or LocalReimportAction.Undo);
                }
                if (stage == 0)
                {
                    stage = 1; prepare.SetFocus(); Press(KeyCode.Enter);
                }
                else if (stage == 1 && source.Enabled && retention.Enabled)
                {
                    Unapproved(); body.SetFocus(); Assert.True(body.HasFocus);
                    var text = body.Text;
                    Press(KeyCode.End | KeyCode.CtrlMask); Assert.Equal(text.Length, body.CaretOffset);
                    Press(KeyCode.Home | KeyCode.CtrlMask); Assert.Equal(0, body.CaretOffset);
                    Press(KeyCode.CursorDown); Press(KeyCode.Home); Press(KeyCode.End);
                    Press(KeyCode.Home | KeyCode.CtrlMask); Assert.Equal(0, body.CaretOffset);
                    Press(KeyCode.Space); Press(KeyCode.Enter); Press(KeyCode.A | KeyCode.AltMask);
                    Assert.Equal(text, body.Text); Unapproved();
                    Move(KeyCode.Tab, source); Unapproved();
                    Move(KeyCode.Tab | KeyCode.ShiftMask, body);
                    Move(KeyCode.Tab | KeyCode.ShiftMask, hidden);
                    Move(KeyCode.Tab, body); Assert.Equal(text, body.Text); Unapproved();
                    Move(KeyCode.Tab, source); Move(KeyCode.Tab, retention);
                    Move(KeyCode.Tab, prepare); Move(KeyCode.Tab, inspect);
                    Move(KeyCode.Tab, close); // Disabled mutation controls are skipped.
                    Move(KeyCode.Tab, id); Move(KeyCode.Tab | KeyCode.ShiftMask, close);
                    Move(KeyCode.Tab | KeyCode.ShiftMask, inspect); Move(KeyCode.Tab | KeyCode.ShiftMask, prepare);
                    Move(KeyCode.Tab | KeyCode.ShiftMask, retention); Unapproved();
                    if (!retentionFirst) Move(KeyCode.Tab | KeyCode.ShiftMask, source);
                    Press(KeyCode.Space); Assert.False(apply.Enabled);
                    Press(KeyCode.A | KeyCode.AltMask); Assert.Single(actions);
                    Move(retentionFirst ? KeyCode.Tab | KeyCode.ShiftMask : KeyCode.Tab, retentionFirst ? source : retention);
                    Press(KeyCode.Space); Assert.True(apply.Enabled);
                    Assert.Equal(CheckState.Checked, source.Value); Assert.Equal(CheckState.Checked, retention.Value);
                    Assert.Single(actions); // Focus and consent alone never launch a mutation.
                    stage = 2; Press(KeyCode.I | KeyCode.AltMask); Unapproved();
                }
                else if (stage == 2 && source.Enabled && retention.Enabled)
                {
                    Unapproved(); Assert.Equal(2, actions.Count);
                    body.SetFocus(); Move(KeyCode.Tab, source); Unapproved();
                    Move(KeyCode.Tab | KeyCode.ShiftMask, body);
                    Press(KeyCode.U | KeyCode.AltMask); Assert.Equal(2, actions.Count);
                    verified = true; Press(KeyCode.Esc); return false;
                }
                return true;
            });
            LocalReimportScreen.Show(app, new Logger(), Target(), (request, _) =>
            {
                actions.Add(request.Action);
                Assert.True(request.Action is LocalReimportAction.Prepare or LocalReimportAction.Inspect);
                return Task.FromResult(new LocalReimportResponse(Plan: plan));
            }, TestContext.Current.CancellationToken, value => dialog = value, callback => queued.Enqueue(callback), registryRestore: true);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(verified); Assert.False(timedOut);
        Assert.Equal(new[] { LocalReimportAction.Prepare, LocalReimportAction.Inspect }, actions);
    }

    [Theory]
    [InlineData(false, "id")]
    [InlineData(false, "hidden")]
    [InlineData(true, "id")]
    [InlineData(true, "source")]
    [InlineData(true, "hidden")]
    [InlineData(false, "id", true, 80, 24)]
    [InlineData(false, "hidden", true, 120, 36)]
    public async Task SourceAndRetentionApprovalAreIndependentAndEditingClosesBoth(bool sourceSwitch, string edit, bool registryRestore = false, int columns = 110, int rows = 32)
    {
        // Presentation evidence only; no synthetic saved mutation authority.
        var report = new SourceReviewReport([], 0, 0, new("limited", [], 0, [], 0), new("limited", [], 0, [], 0), [], 0, "Frozen literal fixture; no execution");
        var plan = (sourceSwitch ? SourceSwitchPlan() : Plan()) with
        { SourceReview = new(1, "review-installed-candidate", "review-source-candidate", new('1', 64), new('2', 64), SourceUpdateReview.Hash(report), report) };
        if (registryRestore) plan = plan with
        {
            LocalImport = null,
            Purpose = ReplacementPlanStore.RegistryRestorePurpose,
            RegistryRestore = new(SkillView.Inventory.Models.ValidityState.MissingSkillMd,
                SkillView.Inventory.Models.SkillFrontMatter.Empty with { Name = "demo" }, null, false)
        };
        var rendered = SourceReviewScreen.RenderText(plan);
        Assert.Contains(registryRestore ? "skillview registry-restore-ack-source" : sourceSwitch ? "skillview source-switch-ack-source" : "skillview reimport-ack-source", rendered);
        if (sourceSwitch) Assert.DoesNotContain("skillview reimport-ack-source", rendered);
        var queued = new ConcurrentQueue<Action>();
        var prepared = 0; var verified = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(columns, rows);
            Dialog? dialog = null; var started = false;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (queued.TryDequeue(out var completion)) completion();
                var retention = dialog.SubViews.OfType<CheckBox>().Single(value => value.Text.StartsWith("Replace from", StringComparison.Ordinal));
                var source = dialog.SubViews.OfType<CheckBox>().Single(value => value.Text.StartsWith("I reviewed", StringComparison.Ordinal));
                var apply = dialog.SubViews.OfType<Button>().Single(value => value.Text == "_Apply reviewed");
                if (!started)
                {
                    started = true;
                    var prepare = dialog.SubViews.OfType<Button>().Single(value => value.Text == "_Prepare");
                    prepare.SetFocus(); Assert.True(prepare.HasFocus); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                }
                else if (retention.Enabled && source.Enabled)
                {
                    Toggle(retention); Assert.False(apply.Enabled);
                    Toggle(source); Assert.True(apply.Enabled);
                    Toggle(retention); Assert.False(apply.Enabled);
                    Toggle(retention); Assert.True(apply.Enabled);
                    if (edit == "id") dialog.SubViews.OfType<TextField>().First().Text = new string('d', 32);
                    else if (edit == "source") dialog.SubViews.OfType<TextField>().Last().Text = NewSourcePath + "-changed";
                    else Toggle(dialog.SubViews.OfType<CheckBox>().Single(value => value.Text == "Include hidden directories"));
                    Assert.Equal(CheckState.UnChecked, retention.Value); Assert.Equal(CheckState.UnChecked, source.Value);
                    Assert.False(retention.Enabled); Assert.False(source.Enabled); Assert.False(apply.Enabled);
                    verified = true; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
                }
                return true;
                void Toggle(CheckBox box)
                { box.SetFocus(); Assert.True(box.HasFocus); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space)); }
            });
            LocalReimportScreen.Show(app, new Logger(), Target(), (_, _) =>
            { prepared++; return Task.FromResult(new LocalReimportResponse(Plan: plan)); }, TestContext.Current.CancellationToken,
                value => { dialog = value; if (sourceSwitch) value.SubViews.OfType<TextField>().Last().Text = NewSourcePath; },
                callback => queued.Enqueue(callback), sourceSwitch: sourceSwitch, registryRestore: registryRestore);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(verified); Assert.False(timedOut); Assert.Equal(1, prepared);
    }
}
