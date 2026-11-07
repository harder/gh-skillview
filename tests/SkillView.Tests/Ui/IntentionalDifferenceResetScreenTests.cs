using SkillView.Logging;
using SkillView.Preferences;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class IntentionalDifferenceResetScreenTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeRunningCannotOpenOrConfirm(bool duringReviewPublication)
    {
        using var app = Application.Create().Init("ansi");
        using var canceled = new CancellationTokenSource();
        if (!duringReviewPublication) canceled.Cancel();
        var calls = 0; var opened = false; var timeout = false;
        app.AddTimeout(TimeSpan.FromSeconds(1), () => { timeout = true; app.RequestStop(); return false; });
        IntentionalDifferenceResetScreen.Show(app, new Logger(), null, "/captured/preferences", "refused", (_, _) =>
        {
            calls++; throw new IOException("Must not start");
        }, canceled.Token, _ => { opened = true; canceled.Cancel(); });
        Assert.Equal(duringReviewPublication, opened); Assert.Equal(0, calls); Assert.False(timeout);
    }

    [Theory]
    [InlineData(80, 24, false)]
    [InlineData(120, 36, false)]
    [InlineData(80, 24, true)]
    [InlineData(120, 36, true)]
    public async Task ResetRequiresSeparateConfirmationAndCancelIsDefault(int width, int height, bool confirm)
    {
        var calls = 0; var timeout = false; var inspected = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
            Dialog? modal = null; var acted = false;
            var store = "/captured/home/state/SkillView/intentional-differences";
            var review = new DifferenceResetReview(store, "captured-context", "records-" + new string('a', 32), null,
                default, default, default, [], [], null, "review");
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timeout = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (modal is null) return true;
                modal.Layout(); modal.Draw(new DrawContext());
                var cancel = modal.SubViews.OfType<Button>().Single(button => button.Text is "Cancel" or "Close");
                var accept = modal.SubViews.OfType<Button>().Single(button => button.Text == "Confirm Reset");
                var text = modal.SubViews.OfType<Terminal.Gui.Editor.Editor>().Single();
                if (!acted)
                {
                    Assert.True(cancel.IsDefault); Assert.Equal(0, calls);
                    Assert.Contains(store, text.Text); Assert.Contains("only one backup", text.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("512", text.Text); Assert.Contains("acknowledgements", text.Text);
                    Assert.DoesNotContain('\x1b', text.Text);
                    inspected = true; acted = true;
                    (confirm ? accept : cancel).SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                    return confirm;
                }
                if (cancel.Text != "Close") return true;
                Assert.Equal(1, calls); Assert.Contains("Raw backup: /captured/backup", text.Text);
                Assert.False(accept.Enabled); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
            });
            IntentionalDifferenceResetScreen.Show(app, new Logger(), review, store, null, (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new DifferenceResetResult("reset", "/captured/backup", "Raw backup: /captured/backup"));
            }, TestContext.Current.CancellationToken, dialog => modal = dialog);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(inspected); Assert.False(timeout); Assert.Equal(confirm ? 1 : 0, calls);
    }
}
