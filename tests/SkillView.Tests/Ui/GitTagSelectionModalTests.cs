using SkillView.Inventory.Models;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class GitTagSelectionModalTests
{
    [Theory]
    [InlineData(80, 24, true)]
    [InlineData(80, 24, false)]
    [InlineData(120, 36, true)]
    [InlineData(80, 24, true, " ")]
    [InlineData(80, 24, true, "\n")]
    public async Task ActualModalRoutingPreservesTypedShortcutCharactersAndFitsSmallTerminal(int width, int height, bool accept, string pastedSuffix = "")
    {
        var timedOut = false; var interacted = false; string? result = null;
        const string chosen = "uUpacd/release-1";
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
            Dialog? dialog = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                var input = dialog.SubViews.OfType<TextField>().Single();
                input.SetFocus(); Assert.True(input.HasFocus);
                foreach (var character in chosen) app.Keyboard.RaiseKeyDownEvent(new Key(character));
                Assert.Equal(chosen, input.Text.ToString());
                if (pastedSuffix.Length > 0) input.Text = " " + chosen + pastedSuffix;
                var notes = dialog.SubViews.OfType<Label>().ToArray();
                Assert.Contains(notes, note => note.Text.Contains("later ordinary Update follows latest", StringComparison.Ordinal));
                Assert.Contains(notes, note => note.Text.Contains("sharing this physical installation", StringComparison.Ordinal));
                Assert.Contains(notes, note => note.Text.Contains("separate reviewed unpin", StringComparison.Ordinal));
                foreach (var control in dialog.SubViews.Where(child => child is Label or TextField or Button))
                {
                    Assert.InRange(control.Frame.Left, 0, dialog.Viewport.Width - 1);
                    Assert.InRange(control.Frame.Right, 1, dialog.Viewport.Width);
                    Assert.InRange(control.Frame.Top, 0, dialog.Viewport.Height - 1);
                    Assert.InRange(control.Frame.Bottom, 1, dialog.Viewport.Height);
                }
                interacted = true;
                if (accept)
                {
                    Assert.True(input.HasFocus);
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                }
                else app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                return false;
            });
            result = GitTagSelectionModal.Show(app, RemoteTarget(), readyForTests: value => dialog = value);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.True(interacted); Assert.Equal(accept ? chosen : null, result);
    }

    [Fact]
    public async Task InvalidGitRefStaysInInputAndLifetimeCancellationClosesWithoutSelection()
    {
        var timedOut = false; var checkedInvalid = false; string? result = "unexpected";
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
            using var lifetime = new CancellationTokenSource();
            Dialog? dialog = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (!checkedInvalid)
                {
                    var input = dialog.SubViews.OfType<TextField>().Single(); input.Text = "a..b";
                    var prepare = dialog.SubViews.OfType<Button>().Single(button => button.Text == "Prepare tag");
                    prepare.SetFocus(); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                    Assert.True(dialog.IsRunning); Assert.True(input.HasFocus);
                    Assert.Contains(dialog.SubViews.OfType<Label>(), label => label.Text.Contains("valid Git tag", StringComparison.Ordinal));
                    checkedInvalid = true;
                    lifetime.Cancel();
                }
                return false;
            });
            result = GitTagSelectionModal.Show(app, RemoteTarget(), lifetime.Token, value => dialog = value);
            Assert.False(dialog!.IsRunning);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.True(checkedInvalid); Assert.Null(result);
    }

    [Fact]
    public void AlreadyCanceledLifetimeDoesNotOpenModal()
    {
        using var app = Application.Create().Init("ansi");
        var opened = false;
        Assert.Null(GitTagSelectionModal.Show(app, RemoteTarget(), new CancellationToken(true), _ => opened = true));
        Assert.False(opened);
    }

    private static InstalledSkill RemoteTarget() => LocalReimportScreenTests.Target() with
    {
        FrontMatter = SkillFrontMatter.Empty with
        { Upstream = "https://github.com/test/repo", GithubRef = "refs/heads/main", GithubPath = "skills/demo" }
    };
}
