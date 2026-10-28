using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class QuarantineReviewTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task ExplicitStandaloneEffectAndSeparateUndoApprovalKeepBackendContracts(int width, int height)
    {
        var (request, _) = InlineCopyWorkflowTests.Request();
        var native = new SecureFileIdentity(1, 1, 0, request.Target.ResolvedPath, true, false);
        var preview = new QuarantineRecord(1, new('a', 32), "preview", DateTimeOffset.UtcNow, request.Subject.InstallationId,
            request.Target.Name, request.Subject.Path, "/fixture", "payload", "objects", "context", new('b', 64), 10, 1, 493, [], native, native, native);
        var applyCalls = 0; var undoPreviews = 0; var undoApplies = 0; var step = 0; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
            Dialog? dialog = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                var buttons = dialog.SubViews.OfType<Button>().ToArray();
                var boxes = dialog.SubViews.OfType<CheckBox>().ToArray();
                var confirm = buttons.Single(button => button.Text is "Move aside" or "Restore original");
                if (step == 0 && boxes[0].Enabled)
                {
                    Assert.False(confirm.Enabled); Assert.Equal(0, applyCalls);
                    boxes[0].SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Space); Assert.False(confirm.Enabled);
                    boxes[1].SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Space); Assert.True(confirm.Enabled);
                    confirm.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); step = 1;
                }
                else if (step == 1 && buttons.Single(button => button.Text == "Inspect retained").Enabled)
                {
                    Assert.Equal(1, applyCalls); Assert.Equal(0, undoApplies);
                    buttons.Single(button => button.Text == "Inspect retained").SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); step = 2;
                }
                else if (step == 2 && buttons.Single(button => button.Text == "Review Undo").Enabled && undoPreviews == 1)
                {
                    Assert.Equal(0, undoApplies); Assert.False(confirm.Enabled);
                    buttons.Single(button => button.Text == "Review Undo").SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); step = 3;
                }
                else if (step == 3 && boxes[2].Visible && boxes[2].Enabled)
                {
                    Assert.Equal(2, undoPreviews); Assert.False(confirm.Enabled);
                    boxes[2].SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Space); Assert.True(confirm.Enabled);
                    confirm.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); step = 4;
                }
                else if (step == 4 && undoApplies == 1 && dialog.SubViews.OfType<Markdown>().Single().Text.Contains("restored", StringComparison.Ordinal))
                {
                    Assert.False(confirm.Enabled);
                    Assert.Contains("verification incomplete", dialog.SubViews.OfType<Markdown>().Single().Text);
                    app.Keyboard.RaiseKeyDownEvent(Key.Esc); step = 5; return false;
                }
                return true;
            });
            QuarantineReview.Show(app, new Logger(), request.Subject, TestContext.Current.CancellationToken,
                _ => Task.FromResult(preview),
                (review, standalone, effect, _) =>
                {
                    Assert.Same(preview, review); Assert.Equal(new string('b', 64), review.ReviewFingerprint);
                    Assert.True(standalone); Assert.True(effect); applyCalls++;
                    return Task.FromResult(new StandaloneMoveResult(new(preview.Id, "quarantined", true, true, false, preview.OriginalPath,
                        "/retained/payload", preview.ReviewFingerprint, "Native directory moved; files verified"), "Inventory complete within configured bounds"));
                },
                (id, execute, _) =>
                {
                    Assert.Equal(preview.Id, id);
                    if (execute) undoApplies++; else undoPreviews++;
                    return Task.FromResult(new StandaloneMoveResult(new(id, execute ? "restored" : "quarantined", true, execute, false,
                        preview.OriginalPath, "/retained/payload", preview.ReviewFingerprint, execute ? "Native original restored" : "Undo preview verified"),
                        execute ? "Native restoration succeeded; inventory verification incomplete" : "Preview only"));
                }, value => dialog = value);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.Equal(5, step); Assert.Equal(1, applyCalls); Assert.Equal(1, undoApplies);
    }
}
