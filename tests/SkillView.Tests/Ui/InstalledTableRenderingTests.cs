using System.Drawing;
using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using SkillView.Maintenance;
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
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void AvailabilityRefreshPreservesFilterSelectionAndTableAndDetailScroll(int width, int height)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var skills = Enumerable.Range(0, 60).Select(index => MaintenanceQueueTests.Skill("/skills/skill-" + index.ToString("D2"), "https://github.com/owner/repo")
            with
        { Name = "skill-" + index.ToString("D2") }).ToImmutableArray();
        var snapshot = InventorySnapshot.Empty with { Skills = skills };
        var availability = new UpdateAvailabilitySession();
        var request = availability.Begin(snapshot, 1, "context");
        var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(snapshot), (_, _, _) => Task.CompletedTask, () => { }, () => { }, availability: availability)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            view.SetFilterTextForTests("skill");
            var table = view.SubViews.OfType<TableView>().Single();
            view.FocusList();
            window.Layout(); window.Draw(new DrawContext());
            for (var index = 0; index < 30; index++) app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.CursorDown));
            view.NewKeyDownEvent(new Key(KeyCode.F2));
            window.Layout(); window.Draw(new DrawContext());
            var rowOffset = table.RowOffset;
            Assert.True(rowOffset > 0);
            var detail = view.SubViews.OfType<Markdown>().Single();
            detail.ScrollVertical(5);
            window.Draw(new DrawContext());
            var detailOffset = detail.Viewport.Y;
            Assert.True(detailOffset > 0);
            availability.Publish(request, 1, "context", skills[30], new(UpdateAvailabilityStatus.Available, "gh reports a new tree.", DateTimeOffset.Now));
            view.RefreshAvailability();
            window.Layout(); window.Draw(new DrawContext());
            Assert.Equal("skill", view.GetFilterText());
            Assert.Equal(skills[30].InstallationId, view.GetSelectedSkill()!.InstallationId);
            Assert.Equal(rowOffset, table.RowOffset);
            Assert.Equal(detailOffset, detail.Viewport.Y);
            Assert.Equal("Available", table.Table![30, 4]);
        }
        finally { app.End(session!); }
    }

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
