using SkillView.Inventory.Models;
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
public sealed class ApprovedRestoreReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedApprovedRestoreRequiresIndependentSourceAndRetentionControls(bool editBeforeApply)
    {
        // Presentation-only response DTO. No schema8 original, native plan,
        // archive writer or filesystem lifecycle API is constructed/invoked.
        var target = LocalReimportScreenTests.Target();
        var view = LocalReimportScreenTests.Plan() with { LocalImport = null, LocalSource = null };
        var hash = new string('d', 64);
        var evidence = new ReplacementEvidence(hash, hash, 448, default, [], []);
        var header = new ApprovedPayloadHeader(1, new('a', 32), DateTimeOffset.UnixEpoch, "captured-custom", hash,
            new(target.InstallationId, target.ResolvedPath, "presentation-source", "presentation-owners"), view.Subject,
            evidence, hash, "presentation-only", [], ApprovedPayloadStore.SourceReviewContract, hash);
        var report = new SourceReviewReport([], 0, 0, new("limited", [], 0, [], 0), new("limited", [], 0, [], 0), [], 0, "Frozen presentation fixture");
        view = view with
        {
            Approval = new(new(Path.GetFullPath("/presentation-store"), header.Id, header.Fingerprint), header, hash, null),
            SourceReview = new(1, "review-installed-candidate", "review-source-candidate", hash, hash, SourceUpdateReview.Hash(report), report)
        };
        Assert.Contains("skillview approved-ack-source", SourceReviewScreen.RenderText(view));
        var called = 0; var verified = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(120, 38);
            Dialog? dialog = null; var phase = 0;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                var retain = dialog.SubViews.OfType<CheckBox>().Single(box => box.Text.StartsWith("Restore approved archive", StringComparison.Ordinal));
                var source = dialog.SubViews.OfType<CheckBox>().Single(box => box.Text.StartsWith("I reviewed", StringComparison.Ordinal));
                var apply = dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Apply reviewed");
                if (phase == 0)
                {
                    Assert.False(dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Prepare").Enabled);
                    Assert.False(apply.Enabled); Toggle(retain); Assert.False(apply.Enabled); Toggle(source); Assert.True(apply.Enabled);
                    if (editBeforeApply)
                    {
                        dialog.SubViews.OfType<TextField>().Single().Text = new string('e', 32);
                        Assert.False(apply.Enabled); Assert.Equal(CheckState.UnChecked, retain.Value); Assert.Equal(CheckState.UnChecked, source.Value);
                        verified = true; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
                    }
                    apply.SetFocus(); Assert.True(apply.HasFocus); app.Keyboard.RaiseKeyDownEvent(Key.Enter); phase = 1;
                }
                else if (called == 1 && !retain.Enabled) { verified = true; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false; }
                return true;
                void Toggle(CheckBox box) { box.SetFocus(); Assert.True(box.HasFocus); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space)); }
            });
            LocalReimportScreen.Show(app, new Logger(), target, (request, _) =>
            {
                Assert.Equal(LocalReimportAction.Apply, request.Action); Assert.Equal(view.Id, request.Id);
                Assert.Equal(target.InstallationId, request.Review!.Subject.InstallationId); Assert.True(request.Review.AcknowledgeSourceReview);
                called++; return Task.FromResult(new LocalReimportResponse(Plan: view with { Status = "installed" }));
            }, TestContext.Current.CancellationToken, value => dialog = value, approvedRestore: true, initialPlan: view);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(verified); Assert.False(timedOut); Assert.Equal(editBeforeApply ? 0 : 1, called);
    }
}
