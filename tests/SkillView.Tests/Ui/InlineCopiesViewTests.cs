using System.Collections.Concurrent;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InlineCopiesViewTests
{
    [Theory]
    [InlineData("back")]
    [InlineData("refresh")]
    [InlineData("context")]
    [InlineData("selection")]
    public async Task QueuedComparisonCannotPublishAfterOwningSelectionOrContextRetires(string change)
    {
        var (request, reference) = InlineCopyWorkflowTests.Request();
        var pending = new ConcurrentQueue<(Action Action, TaskCompletionSource Done)>();
        var work = new List<Task>(); var compared = 0; var generation = request.Generation; var context = request.Context;
        var sent = false; var rejected = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
            using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
            InlineCopiesView? panel = null;
            panel = new InlineCopiesView(action =>
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); pending.Enqueue((action, done)); return done.Task;
            }, (task, _) => work.Add(task()), () => generation, () => context, () => panel!.Retire(),
                TestContext.Current.CancellationToken, compare: (_, _, _) =>
                { compared++; return Task.FromResult(InlineCopyWorkflowTests.Equal(request, reference)); })
            { Width = Dim.Fill(), Height = Dim.Fill() };
            window.Add(panel);
            Assert.True(panel.Show(request.Snapshot, request.Target, request.ReturnState));
            var compare = panel.SubViews.OfType<Button>().Single(button => button.Text == "Compare");
            Assert.False(compare.Enabled); Assert.Equal(0, compared); // No auto-reference or startup hashing.
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (!sent)
                {
                    sent = true; panel.SetReference(panel.Request!, reference); compare.SetFocus(); Assert.True(compare.HasFocus); Assert.True(compare.Enabled); app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                    Assert.Equal(1, compared); Assert.Null(panel.Fact); return true;
                }
                if (!pending.TryDequeue(out var delivery)) return true;
                switch (change)
                {
                    case "back": app.Keyboard.RaiseKeyDownEvent(Key.Esc); break;
                    case "refresh": generation++; panel.ValidateObservation(); break;
                    case "context": context = "changed"; panel.ValidateObservation(); break;
                    case "selection": panel.Show(request.Snapshot, reference, request.ReturnState); break;
                }
                delivery.Action(); delivery.Done.SetResult();
                Assert.Null(panel.Fact); rejected = true; app.RequestStop(); return false;
            });
            app.Run(window);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        await Task.WhenAll(work).WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(rejected); Assert.False(timedOut);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task KeyboardAndMouseAcceptanceUseSameFramedControlAnchor(int width, int height)
    {
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
            using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
            var button = new SingleRowButton { X = 37, Y = Pos.AnchorEnd(1), Text = "More actions" };
            window.Add(button);
            var session = app.Begin(window);
            using var menu = AnchoredActions.Show(button, [("Compare captured copies...", () => { }), ("Remove exact copy", () => { })]);
            Assert.NotNull(menu.Root);
            Assert.Equal(Terminal.Gui.Drawing.LineStyle.Single, menu.Root.BorderStyle);
            Assert.False(menu.Root.ViewportSettings.HasFlag(ViewportSettingsFlags.Transparent));
            Assert.True(app.Screen.Contains(menu.Root.FrameToScreen()));
            Assert.True(menu.Root.FrameToScreen().Bottom <= button.FrameToScreen().Top, $"menu={menu.Root.FrameToScreen()} control={button.FrameToScreen()} screen={app.Screen}");
            Assert.Equal(button.FrameToScreen().Left, menu.Root.FrameToScreen().Left);
            app.End(session!);
        }, TestContext.Current.CancellationToken);
    }
}
