using System.Collections.Concurrent;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed partial class LocalReimportScreenTests
{
    [Theory]
    [InlineData(false, "id")]
    [InlineData(false, "hidden")]
    [InlineData(true, "id")]
    [InlineData(true, "source")]
    [InlineData(true, "hidden")]
    public async Task SourceAndRetentionApprovalAreIndependentAndEditingClosesBoth(bool sourceSwitch, string edit)
    {
        // Presentation evidence only; no synthetic saved mutation authority.
        var report = new SourceReviewReport([], 0, 0, new("limited", [], 0, [], 0), new("limited", [], 0, [], 0), [], 0, "Frozen literal fixture; no execution");
        var plan = (sourceSwitch ? SourceSwitchPlan() : Plan()) with
        { SourceReview = new(1, "review-installed-candidate", "review-source-candidate", new('1', 64), new('2', 64), SourceUpdateReview.Hash(report), report) };
        var rendered = SourceReviewScreen.RenderText(plan);
        Assert.Contains(sourceSwitch ? "skillview source-switch-ack-source" : "skillview reimport-ack-source", rendered);
        if (sourceSwitch) Assert.DoesNotContain("skillview reimport-ack-source", rendered);
        var queued = new ConcurrentQueue<Action>();
        var prepared = 0; var verified = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(110, 32);
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
                callback => queued.Enqueue(callback), sourceSwitch: sourceSwitch);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(verified); Assert.False(timedOut); Assert.Equal(1, prepared);
    }
}
