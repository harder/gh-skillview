using SkillView.Ui;
using SkillView.Tests.Gh;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InstallScreenTests
{
    [Fact]
    public void KnownAgents_UseCurrentGhAgentIds()
    {
        Assert.Contains("claude-code", InstallScreen.KnownAgents);
        Assert.Contains("github-copilot", InstallScreen.KnownAgents);
        Assert.Contains("gemini-cli", InstallScreen.KnownAgents);
    }
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task AdvancedFlagsAndAgentsHaveRealKeyboardRoutes(int width, int height)
    {
        using var fixture = new InstallAttemptTests.Fixture("ok");
        var visited = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            Dialog? dialog = null;
            ModalOperationTracker? operation = null;
            Exception? failure = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { failure = new TimeoutException("Advanced route timed out"); app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(20), () =>
            {
                if (dialog is null || operation?.CurrentOwnership != ModalOperationTracker.Ownership.None) return true;
                try
                {
                    var controls = Walk(dialog).ToArray();
                    var install = controls.OfType<Button>().Single(button => button.Text == "_Install");
                    if (!install.Enabled) return true;
                    install.SetFocus();
                    var options = controls.OfType<Button>().Single(button => button.Text == "More options");
                    var path = new List<string>();
                    for (var count = 0; count < 50 && !options.HasFocus; count++)
                    {
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
                        path.Add(string.Join("/", controls.Where(control => control.HasFocus && control.CanFocus).Select(control => control is Button button ? button.Text : control.GetType().Name)));
                    }
                    Assert.True(options.HasFocus, string.Join("\n", path));
                    Assert.True(options.SuperView!.ViewportToScreen().Contains(options.FrameToScreen()));
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                    Assert.Equal("Fewer options", options.Text);
                    Assert.True(controls.OfType<FrameView>().Single(frame => frame.Title == "Behavior").Visible);
                    var more = controls.OfType<Button>().Single(button => button.Text == "More agents");
                    path.Clear();
                    for (var count = 0; count < 50 && !more.HasFocus; count++)
                    {
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
                        path.Add(string.Join("/", controls.Where(control => control.HasFocus && control.CanFocus).Select(control => control is Button button ? button.Text : control is FrameView frame ? frame.Title + ":" + frame.TabStop : control.GetType().Name)));
                    }
                    Assert.True(more.HasFocus, string.Join("\n", path));
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                    Assert.Equal("Fewer agents", more.Text);
                    visited = true;
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                }
                catch (Exception ex) { failure = ex; app.RequestStop(); }
                return false;
            });
            new InstallScreen(app, fixture.Services.InstallService, fixture.Logger, fixture.Script,
                new("fixture/source", "demo", SourcePath: "skills/demo")).ShowForTests((view, owner) => { dialog = view; operation = owner; });
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(visited);
        Assert.False(File.Exists(fixture.Payload("demo")));
    }

    [Theory]
    [InlineData(80, 24, "", false, true)]
    [InlineData(80, 24, "v2", false, false)]
    [InlineData(120, 36, "v2", true, true)]
    public async Task SubmittedVersionCanOnlyReuseTheInitiallyObservedDiscoverAnchor(int width, int height,
        string requestedVersion, bool changeBack, bool hasAnchor)
    {
        using var fixture = new InstallAttemptTests.Fixture("ok");
        InstallScreen? screen = null;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            Dialog? dialog = null;
            ModalOperationTracker? operation = null;
            Exception? failure = null;
            var submitted = false;
            var requestedBrowse = false;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { failure = new TimeoutException("Advanced version submission timed out"); app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(20), () =>
            {
                if (dialog is null || operation?.CurrentOwnership != ModalOperationTracker.Ownership.None) return true;
                try
                {
                    var controls = Walk(dialog).ToArray();
                    if (!submitted)
                    {
                        var install = controls.OfType<Button>().Single(button => button.Text == "_Install");
                        if (!install.Enabled) return true;
                        var version = controls.OfType<FrameView>().Single(frame => frame.Title == "Source")
                            .SubViews.OfType<TextField>().OrderBy(field => field.Frame.Y).Last();
                        version.Text = requestedVersion;
                        if (changeBack) version.Text = "";
                        submitted = true;
                        install.InvokeCommand(Command.Accept);
                        return true;
                    }
                    if (screen!.LastResult is null) return true;
                    Assert.True(screen.LastResult.Succeeded);
                    Assert.Equal(changeBack || requestedVersion.Length == 0 ? null : requestedVersion, screen.LastResult.Version);
                    Assert.Equal(hasAnchor, screen.LastResult.Navigation is not null);
                    if (hasAnchor)
                    {
                        Assert.Equal("skills/demo", screen.LastResult.Navigation!.Source.Subpath);
                        Assert.Equal("https://github.com/fixture/source", screen.LastResult.Navigation.Source.Origin);
                    }
                    else
                    {
                        Assert.Contains("demo@" + requestedVersion, screen.LastResult.CommandLine);
                        if (!requestedBrowse)
                        {
                            requestedBrowse = true;
                            controls.OfType<Button>().Single(button => button.Text == "Browse Installed").InvokeCommand(Command.Accept);
                            return true;
                        }
                        Assert.Contains(controls.OfType<Label>(), label => label.Text.Contains("Exact-copy navigation needs the unchanged named Discover source", StringComparison.Ordinal));
                    }
                    controls.OfType<Button>().Single(button => button.Text == "Back to Discover").InvokeCommand(Command.Accept);
                }
                catch (Exception ex) { failure = ex; app.RequestStop(); }
                return false;
            });
            // Echo is an inert Unix gh child: actual InstallAsync admission and
            // argv construction execute, but no payload/source is fabricated.
            screen = new InstallScreen(app, fixture.Services.InstallService, fixture.Logger, "/bin/echo",
                new("fixture/source", "demo", SourcePath: "skills/demo"));
            screen.ShowForTests((view, owner) => { dialog = view; operation = owner; });
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(screen!.LastResult!.Succeeded);
        Assert.Equal(hasAnchor, screen.LastResult.Navigation is not null);
        Assert.False(File.Exists(fixture.Payload("demo")));
    }

    private static IEnumerable<View> Walk(View view)
    {
        yield return view;
        foreach (var child in view.SubViews)
            foreach (var nested in Walk(child)) yield return nested;
    }
}
