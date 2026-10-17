using System.Collections.Concurrent;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Preferences;
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
public sealed class ApprovedPayloadScreenTests
{
    [Fact]
    public void InstalledShortcutRoutesActualSelectedRowAndPreservesFilterTyping()
    {
        using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(110, 32);
        var target = LocalReimportScreenTests.Target(); InstalledSkill? captured = null; var shellReceived = false;
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(InventorySnapshot.Empty), (_, _, _) => Task.CompletedTask, () => { }, () => { },
            onApprovedPayload: skill => captured = skill)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        view.LoadSeeded(InventorySnapshot.Empty with { Skills = [target] }); window.Add(view);
        window.KeyDown += (_, key) => { if (!key.Handled && key.AsRune.Value == 'A') shellReceived = true; };
        var session = app.Begin(window);
        try
        {
            view.FocusFilterForTests(); Press(); Assert.Null(captured); Assert.Equal("A", view.GetFilterText());
            view.SetFilterTextForTests(""); view.FocusList(); shellReceived = false;
            Press(); Assert.Same(target, captured); Assert.False(shellReceived);
        }
        finally { app.End(session!); }
        void Press() { app.Keyboard.RaiseKeyDownEvent(new Key('A')); window.Draw(new DrawContext()); }
    }

    [Theory]
    [InlineData("id")]
    [InlineData("fingerprint")]
    [InlineData("hidden")]
    public async Task CaptureAndDesiredIntentRequireSeparateConsentsAndEditsCloseThem(string edit)
    {
        // Presentation protocol only; these response DTOs confer no native
        // archive/plan authority and never enter a filesystem mutator.
        var policy = new DesiredPolicyObservation("absent", "No saved intent", null, new('a', 64));
        var reference = new ApprovedPayloadReference(Path.GetFullPath("/private-approval-store"), new('b', 32), new('c', 64));
        var previewed = new ApprovalResponse(new('d', 64), policy);
        var completed = previewed with { Reference = reference };
        var queued = new ConcurrentQueue<Action>(); var requests = new ConcurrentQueue<ApprovalRequest>();
        var verified = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(120, 38);
            Dialog? dialog = null; var phase = 0;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (queued.TryDequeue(out var callback)) callback();
                var boxes = dialog.SubViews.OfType<CheckBox>().ToArray();
                var captureConsent = boxes.Single(box => box.Text.StartsWith("I approve retaining", StringComparison.Ordinal));
                var policyConsent = boxes.Single(box => box.Text.StartsWith("I separately", StringComparison.Ordinal));
                var capture = Button("Capture private archive"); var setPolicy = Button("Set desired immutable intent");
                if (phase == 0)
                {
                    Assert.False(capture.Enabled); Assert.False(setPolicy.Enabled);
                    Press(Button("Preview installed")); phase = 1;
                }
                else if (phase == 1 && captureConsent.Enabled)
                {
                    Assert.False(policyConsent.Enabled); Assert.False(setPolicy.Enabled);
                    Toggle(captureConsent); Assert.True(capture.Enabled); Assert.False(setPolicy.Enabled);
                    Press(capture); phase = 2;
                }
                else if (phase == 2 && policyConsent.Enabled)
                {
                    Assert.Equal(CheckState.UnChecked, captureConsent.Value); Assert.Equal(CheckState.UnChecked, policyConsent.Value);
                    Assert.False(capture.Enabled); Assert.False(setPolicy.Enabled);
                    Assert.Contains(LocalReimportScreenTests.Target().ResolvedPath, dialog.SubViews.OfType<Editor>().Single().Text);
                    Toggle(policyConsent); Assert.True(setPolicy.Enabled);
                    if (edit == "id") dialog.SubViews.OfType<TextField>().First().Text = new string('e', 32);
                    else if (edit == "fingerprint") dialog.SubViews.OfType<TextField>().Last().Text = new string('e', 64);
                    else Toggle(boxes.Single(box => box.Text == "Include hidden directories"));
                    Assert.Equal(CheckState.UnChecked, captureConsent.Value); Assert.Equal(CheckState.UnChecked, policyConsent.Value);
                    Assert.False(capture.Enabled); Assert.False(setPolicy.Enabled);
                    verified = true; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
                }
                return true;
                Button Button(string text) => dialog.SubViews.OfType<Button>().Single(button => button.Text == text);
                void Press(Button button) { button.SetFocus(); Assert.True(button.HasFocus); app.Keyboard.RaiseKeyDownEvent(Key.Enter); }
                void Toggle(CheckBox box) { box.SetFocus(); Assert.True(box.HasFocus); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space)); }
            });
            var plan = ApprovedPayloadScreen.Show(app, new Logger(), LocalReimportScreenTests.Target(), (request, _) =>
            {
                requests.Enqueue(request);
                if (request.Action == ApprovalAction.Capture) { Assert.True(request.Consent); Assert.Same(previewed, request.Review); }
                else Assert.Equal(ApprovalAction.Preview, request.Action);
                return Task.FromResult(request.Action == ApprovalAction.Preview ? previewed : completed);
            }, TestContext.Current.CancellationToken, value => dialog = value, callback => queued.Enqueue(callback));
            Assert.Null(plan);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(verified); Assert.False(timedOut); Assert.Equal(2, requests.Count);
    }
}
