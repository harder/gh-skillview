using SkillView.Bootstrapping;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class DiscoverShortcutAuditTests
{
    [Theory]
    [InlineData(KeyCode.L)]
    [InlineData(KeyCode.L | KeyCode.ShiftMask)]
    [InlineData(KeyCode.Esc)]
    public void FocusedReadOnlyLogsHaveAKeyboardReturnToPreview(KeyCode code)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(80, 24);
        var subject = new SkillViewApp(TuiServices.Build(new Logger()), new AppOptions(
            InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []));
        using var window = subject.BuildUiForTests();
        var session = app.Begin(window);
        try
        {
            subject.ShowLogPaneForTests();
            window.Layout();
            var log = subject.DiscoverTabForTests!.DetailPane.LogPane;
            log.SetFocus();
            Assert.True(log.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(new Key(code));
            Assert.False(log.Visible);
            Assert.True(subject.DiscoverTabForTests.LeftFrame.Visible);
            Assert.True(subject.ResultsTableForTests!.HasFocus);
        }
        finally { app.End(session!); }
    }

    [Theory]
    [InlineData(KeyCode.E)]
    [InlineData(KeyCode.E | KeyCode.ShiftMask)]
    public void FocusedReadOnlySourceCanToggleBackToRenderedPreview(KeyCode code)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(80, 24);
        var subject = new SkillViewApp(TuiServices.Build(new Logger()), new AppOptions(
            InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []));
        using var window = subject.BuildUiForTests();
        var session = app.Begin(window);
        try
        {
            subject.ResultsTableForTests!.SetFocus();
            app.Keyboard.RaiseKeyDownEvent(new Key('e'));
            var raw = subject.DiscoverTabForTests!.DetailPane.PreviewRawPane;
            raw.SetFocus();
            Assert.True(raw.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(new Key(code));
            Assert.False(raw.Visible);
            Assert.True(subject.DiscoverTabForTests.DetailPane.PreviewPane.Visible);
        }
        finally { app.End(session!); }
    }

    [Theory]
    [InlineData(KeyCode.Enter)]
    [InlineData(KeyCode.P)]
    [InlineData(KeyCode.P | KeyCode.ShiftMask)]
    [InlineData(KeyCode.V)]
    [InlineData(KeyCode.V | KeyCode.ShiftMask)]
    [InlineData(KeyCode.CursorRight)]
    [InlineData(KeyCode.J | KeyCode.CtrlMask)]
    public void EveryAdvertisedPreviewAliasActivatesOnceWithoutChangingTabs(KeyCode code)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(120, 36);
        var subject = new SkillViewApp(TuiServices.Build(new Logger()), new AppOptions(
            InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []));
        using var window = subject.BuildUiForTests();
        subject.LoadSearchResultsForTests([new SearchResultSkill("Fixture", null, "skills/demo", "audit/example", "demo", 1)]);
        var session = app.Begin(window);
        try
        {
            window.Layout();
            var table = subject.ResultsTableForTests!;
            table.SetFocus();
            Assert.True(table.HasFocus);
            var activated = 0;
            table.Accepted += (_, _) => activated++;
            app.Keyboard.RaiseKeyDownEvent(new Key(code));
            Assert.Equal(1, activated);
            Assert.Equal(SkillViewTab.Discover, subject.ActiveTabForTests);
        }
        finally { app.End(session!); }
    }
}
