using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class CopiesScreenTests
{
    [Theory]
    [InlineData(80, 24, "Update / version...", "Update")]
    [InlineData(120, 36, "Cleanup review...", "Cleanup")]
    public async Task RealKeyboardSelectsComparesAndHandsOffOnlyTheChosenPhysicalCopy(int width, int height, string actionLabel, string action)
    {
        var first = CopiesPresentationTests.Copy("/first"); var second = CopiesPresentationTests.Copy("/second", reference: "refs/tags/v2");
        var calls = 0; var timedOut = false; var compared = false; CopiesChoice? choice = null;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
            Dialog? dialog = null;
            var requested = false;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                var table = dialog.SubViews.OfType<TableView>().Single();
                if (!requested)
                {
                    table.SetFocus(); Assert.True(table.HasFocus);
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.CursorDown));
                    Assert.Equal(1, table.GetSelectedRow());
                    foreach (var control in dialog.SubViews)
                    {
                        Assert.InRange(control.Frame.Left, 0, dialog.Viewport.Width - 1); Assert.InRange(control.Frame.Right, 1, dialog.Viewport.Width);
                        Assert.InRange(control.Frame.Top, 0, dialog.Viewport.Height - 1); Assert.InRange(control.Frame.Bottom, 1, dialog.Viewport.Height);
                    }
                    var buttons = dialog.SubViews.OfType<Button>().ToArray();
                    foreach (var button in buttons)
                        Assert.DoesNotContain(buttons, other => !ReferenceEquals(button, other) && button.Frame.IntersectsWith(other.Frame));
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
                    Assert.True(dialog.SubViews.OfType<Editor>().Single().HasFocus);
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
                    Assert.True(buttons.Single(button => button.Text == "Use as reference").HasFocus);
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
                    Assert.True(buttons.Single(button => button.Text == "Compare").HasFocus);
                    requested = true;
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                    return true;
                }
                var text = dialog.SubViews.OfType<Editor>().Single().Text.ToString();
                if (!text.Contains("Differing files: 1", StringComparison.Ordinal)) return true;
                Assert.Contains("Different recorded revisions", text); Assert.Contains("Changed: SKILL.md", text);
                compared = true;
                var actionButton = dialog.SubViews.OfType<Button>().Single(button => button.Text == actionLabel);
                actionButton.SetFocus(); Assert.True(actionButton.HasFocus); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter)); return false;
            });
            choice = CopiesScreen.Show(app, new Logger(), new([first, second], 0, 0, false), (left, right, _) =>
            { calls++; Assert.Same(first, left); Assert.Same(second, right); return Task.FromResult(CopiesPresentationTests.Compared(left, right)); },
                TestContext.Current.CancellationToken, readyForTests: value => dialog = value);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.True(compared); Assert.Equal(1, calls);
        Assert.NotNull(choice); Assert.Equal(action, choice.Action.ToString()); Assert.Equal(second.InstallationId, choice.Subject.InstallationId); Assert.Equal(second.ResolvedPath, choice.Subject.Path);
    }

    [Fact]
    public async Task EscapeCancelsAnActiveComparisonAndWaitsForItBeforeClosing()
    {
        var first = CopiesPresentationTests.Copy("/first"); var second = CopiesPresentationTests.Copy("/second");
        var started = false; var canceled = false; var stopped = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
            Dialog? dialog = null;
            var requested = false;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (started) { app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false; }
                if (requested) return true;
                var table = dialog.SubViews.OfType<TableView>().Single(); table.SetSelectedRow(1);
                dialog.SubViews.OfType<Button>().Single(button => button.Text == "Compare").SetFocus();
                requested = true;
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter)); return true;
            });
            Assert.Null(CopiesScreen.Show(app, new Logger(), new([first, second], 0, 0, false), async (_, _, token) =>
            {
                started = true;
                try { await Task.Delay(System.Threading.Timeout.Infinite, token); throw new InvalidOperationException("No uncanceled completion"); }
                catch (OperationCanceledException) { canceled = true; throw; }
                finally { stopped = true; }
            }, TestContext.Current.CancellationToken, value => dialog = value));
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.True(started); Assert.True(canceled); Assert.True(stopped);
    }
}
