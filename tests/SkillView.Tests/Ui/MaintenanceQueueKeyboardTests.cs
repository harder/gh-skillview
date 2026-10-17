using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class MaintenanceQueueKeyboardTests
{
    [Fact]
    public async Task FocusedQueue_RefreshesAndRoutesExactRowThroughApplicationKeyboard()
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(100, 26);
        var first = MaintenanceQueueTests.Skill("/skills/one", "https://github.com/one/repo");
        var second = MaintenanceQueueTests.Skill("/skills/two", "https://github.com/two/repo");
        var captures = 0;
        MaintenanceQueueEntry? activated = null;
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => { captures++; return Task.FromResult(InventorySnapshot.Empty with { Skills = [first, second] }); },
            static () => throw new InvalidOperationException("Untargeted updates"),
            static () => throw new InvalidOperationException("Untargeted cleanup"),
            static () => { }, static () => { }, onActivateEntry: entry => activated = entry,
            runTask: (work, _) => work().GetAwaiter().GetResult())
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        await view.LoadAsync();
        var session = app.Begin(window);
        try
        {
            var table = view.SubViews.OfType<TableView>().Single();
            table.SetFocus();
            Assert.True(table.HasFocus);
            Press(new Key('r'));
            Assert.Equal(2, captures);
            Assert.Null(activated);
            Assert.True(table.HasFocus);
            Press(new Key(KeyCode.CursorDown));
            Press(Key.Enter);
            Assert.NotNull(activated);
            Assert.Equal("NameCollision", activated.Issue);
            Assert.Equal(second.InstallationId, activated.Subject.InstallationId);
            Assert.Contains(second.ResolvedPath, activated.AffectedPaths);
        }
        finally { app.End(session!); }

        void Press(Key key)
        {
            app.Keyboard.RaiseKeyDownEvent(key);
            window.Draw(new DrawContext());
        }
    }
}
