using System.Drawing;
using SkillView.Inventory.Models;
using SkillView.Ui;
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
public sealed class InstalledTableRenderingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResizeKeepsShortColumnsVisibleAndSelectionInsideFinalBorder(bool hasPackage)
    {
        using var app = Application.Create().Init("ansi");
        var driver = app.Driver!;
        driver.SetScreenSize(120, 30);
        using var window = new Window();
        var skill = MaintenanceQueueTests.Skill("/skills/long-skill-name-that-must-grow-after-resizing", "https://github.com/owner/repo") with
        {
            Name = "long-skill-name-that-must-grow-after-resizing",
            Package = hasPackage ? new SkillPackage("owner/very-long-package-name", "git", null, null, null) : null,
            Agents = [new("codex", "/skills/demo", false), new("claude", "/skills/demo", false)],
        };
        var view = new InstalledTabView(action => { action(); return Task.CompletedTask; },
            (operation, _) => operation().GetAwaiter().GetResult(),
            _ => Task.FromResult(InventorySnapshot.Empty), (_, _, _) => Task.CompletedTask, () => { }, () => { })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        view.LoadSeeded(InventorySnapshot.Empty with { Skills = [.. Enumerable.Repeat(skill, 50)] });
        var session = app.Begin(window);
        try
        {
            view.FocusList();
            var table = Assert.Single(view.SubViews.OfType<TableView>());
            // Both growing and shrinking, including the user's wide terminal.
            foreach (var width in new[] { 80, 120, 215, 100, 80 })
            {
                driver.SetScreenSize(width, 30);
                window.Layout();
                window.Draw(new DrawContext());
                Assert.Equal(table.Viewport.Width, table.GetContentSize().Width);
                Assert.Equal(0, table.Viewport.X);
                Assert.Equal(skill.Name, table.Table![0, 0]);
                Assert.Equal("CDX CLD", table.Table[0, 3]);
                var origin = table.ViewportToScreen(Point.Empty);
                string Line(int y) => string.Concat(Enumerable.Range(origin.X, table.Viewport.Width)
                    .Select(x => driver.Contents![y, x].Grapheme));
                var header = Line(origin.Y + 1);
                Assert.Contains("State", header);
                Assert.Contains("Loc", header);
                Assert.Contains("Agents", header);
                Assert.Equal("│", driver.Contents![origin.Y + 3, origin.X + table.Viewport.Width - 1].Grapheme);
                Assert.Equal(table.GetScheme().Normal, driver.Contents![origin.Y + 3, origin.X + table.Viewport.Width - 1].Attribute);
                Assert.NotEqual(table.GetScheme().Focus.Background,
                    driver.Contents![origin.Y + 3, origin.X + table.Viewport.Width].Attribute!.Value.Background);
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.End | KeyCode.CtrlMask));
                window.Draw(new DrawContext());
                Assert.True(table.RowOffset > 0);
                Assert.Contains("Agents", Line(origin.Y + 1));
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Home | KeyCode.CtrlMask));
            }
        }
        finally { app.End(session!); }
    }
}
