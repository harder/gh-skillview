using System.Runtime.ExceptionServices;
using SkillView.Gh.Models;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InstallFailureDetailsTests
{
    private const string Refusal = "An existing shared selector would be replaced. Use update or a coordinated maintenance plan before reinstalling.";

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task LiteralRefusalRemainsCompleteAndScrollableWithStreamsAndInertMarkup(int width, int height)
    {
        var error = Refusal + "\n```\n[error](https://invalid.example/)\n\u001b]8;;https://invalid.example/\u0007literal\u001b]8;;\u0007\u001b[31m red\u001b[0m\u0000";
        var original = InstallResult.Failure("owner/repo", "demo", null, 2, error, []) with
        { StdOut = "stdout begin\n" + new string('`', 80) + "\n" + new string('x', 9000) };
        var rendered = InstallFailureDetails.Render(original);
        Assert.Contains(Refusal, rendered);
        Assert.Contains("```\n[error](https://invalid.example/)", rendered);
        Assert.Contains("literal red", rendered);
        Assert.DoesNotContain('\u001b', rendered);
        Assert.DoesNotContain('\u0007', rendered);
        Assert.DoesNotContain('\u0000', rendered);
        Assert.Contains("Standard error:", rendered);
        Assert.Contains("Standard output:\nstdout begin", rendered);
        Assert.EndsWith("[truncated]", rendered);
        Assert.Equal(error, original.ErrorMessage);
        Assert.Equal(error, original.StdErr);
        Assert.True(original.StdOut.Length > InstallFailureDetails.MaxFieldCharacters);
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            ExceptionDispatchInfo? failure = null;
            var inspected = false;
            InstallFailureDetails.Show(app, rendered, dialog =>
            {
                app.AddTimeout(TimeSpan.FromMilliseconds(20), () =>
                {
                    try
                    {
                        var pane = dialog.SubViews.OfType<Editor>().Single();
                        Assert.Equal(rendered, pane.Text);
                        Assert.Equal(0, pane.CaretOffset);
                        Assert.True(pane.WordWrap);
                        Assert.True(pane.ReadOnly);
                        Assert.True(pane.HasFocus);
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.End | KeyCode.CtrlMask));
                        Assert.Equal(rendered.Length, pane.CaretOffset);
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Home | KeyCode.CtrlMask));
                        Assert.Equal(0, pane.CaretOffset);
                        inspected = true;
                    }
                    catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
                    app.RequestStop();
                    return false;
                });
            });
            failure?.Throw();
            Assert.True(inspected);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void BoundsEveryFieldBeforeSanitizingAndAvoidsSplittingSurrogatePair()
    {
        var text = new string('x', InstallFailureDetails.MaxFieldCharacters - 1) + "\U0001f642tail";
        var rendered = InstallFailureDetails.Literal(text);
        Assert.Equal(new string('x', InstallFailureDetails.MaxFieldCharacters - 1) + "\n[truncated]", rendered);
        var result = InstallResult.Failure("owner/repo", "demo", null, 2, text, []) with { StdOut = text };
        Assert.InRange(InstallFailureDetails.Render(result).Length, 3 * 8192, 3 * 8192 + 200);
        Assert.Equal(text, result.StdErr);
        Assert.Equal(text, result.StdOut);
        Assert.Equal(text, result.ErrorMessage);
        Assert.Equal("plain\nnext\nlast\tline", InstallFailureDetails.Literal("plain\r\nnext\rlast\tline"));
        Assert.Equal("", InstallFailureDetails.Literal(null));
    }
}
