using System.Data;
using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class VisualHierarchyTests
{
    [Fact]
    public void EmptyInputsAndHeadingsRemainDistinctInSixteenColorTerminals()
    {
        var input = WingetTuiTheme.CreateEditableInputScheme().Normal;
        var header = WingetTuiTheme.CreateHeadingScheme().Normal;
        var body = WingetTuiTheme.CreateBaseScheme().Normal;
        var selected = WingetTuiTheme.CreateTableScheme().Focus;
        Assert.NotEqual(body.Background.GetClosestNamedColor16(), input.Background.GetClosestNamedColor16());
        Assert.NotEqual(input.Foreground.GetClosestNamedColor16(), input.Background.GetClosestNamedColor16());
        Assert.NotEqual(header.Foreground.GetClosestNamedColor16(), header.Background.GetClosestNamedColor16());
        Assert.NotEqual(selected.Background.GetClosestNamedColor16(), header.Background.GetClosestNamedColor16());
        Assert.Equal(body.Background, header.Background);
        Assert.NotEqual(input, header);
    }

    [Theory]
    [InlineData(80, 24, AppTheme.Default)]
    [InlineData(120, 36, AppTheme.Default)]
    [InlineData(160, 48, AppTheme.Default)]
    [InlineData(80, 24, AppTheme.HighContrast)]
    [InlineData(120, 36, AppTheme.HighContrast)]
    [InlineData(160, 48, AppTheme.HighContrast)]
    public void ScreenTitlesAndTableHeadersStayDistinctWhenFocusLeavesTheList(int width, int height, AppTheme theme)
    {
        var previous = TuiHelpers.CurrentTheme;
        try
        {
            TuiHelpers.SetTheme(theme);
            WingetTuiTheme.Register(theme);
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            var subject = new SkillViewApp(TuiServices.Build(new Logger()), new AppOptions(
                InvocationMode.Standalone, DispatchMode.Tui, false, theme, [], null, []));
            using var window = subject.BuildUiForTests();
            var installed = subject.InstalledTabForTests!;
            installed.LoadSeeded(InventorySnapshot.Empty with { Skills = [InstalledOverviewTests.Copy("/skills/demo", "demo")] });
            subject.LoadSearchResultsForTests([new("Fixture", null, "skills/demo", "audit/example", "demo", 1)]);
            var issues = subject.ChangesTabForTests!;
            var issueTable = issues.SubViews.OfType<TableView>().Single();
            var data = new DataTable();
            data.Columns.Add("Kind"); data.Columns.Add("Title"); data.Rows.Add("Review", "demo");
            issueTable.Table = new DataTableSource(data);
            var session = app.Begin(window);
            try
            {
                foreach (var tab in new[] { SkillViewTab.Discover, SkillViewTab.Installed, SkillViewTab.Changes })
                {
                    subject.ForceActiveTabForTests(tab);
                    installed.Visible = tab == SkillViewTab.Installed;
                    issues.Visible = tab == SkillViewTab.Changes;
                    var table = tab == SkillViewTab.Discover ? subject.ResultsTableForTests!
                        : tab == SkillViewTab.Installed ? installed.SubViews.OfType<TableView>().Single() : issueTable;
                    var title = tab == SkillViewTab.Discover ? "Discover skills" : tab == SkillViewTab.Installed ? "Installed skills" : "Issues";
                    var column = tab == SkillViewTab.Discover ? "Seen" : tab == SkillViewTab.Installed ? "Skill" : "Kind";
                    app.LayoutAndDraw(true);
                    Assert.True(table.SetFocus(), $"Cannot focus {tab} list");
                    app.LayoutAndDraw();
                    var focused = Header(column);
                    Assert.Contains(title, Lines()[2]);
                    var body = app.Driver.Contents![focused.Y + 2, focused.X].Attribute;
                    Assert.NotEqual(focused.Style, body);
                    var control = window.SubViews.OfType<Button>().Single(button => button.Text.Contains("History", StringComparison.Ordinal));
                    Assert.True(control.SetFocus(), $"Cannot focus {tab} toolbar control");
                    Assert.False(table.HasFocus, $"Table retained focus on {tab}");
                    app.LayoutAndDraw(true);
                    Assert.Equal(focused.Style, Header(column).Style);
                    Assert.Contains(title, Lines()[2]);
                }
            }
            finally { app.End(session!); }

            string[] Lines() => Enumerable.Range(0, height).Select(y => string.Concat(
                Enumerable.Range(0, width).Select(x => app.Driver.Contents![y, x].Grapheme))).ToArray();
            (int X, int Y, Attribute? Style) Header(string text)
            {
                var lines = Lines();
                var y = Array.FindIndex(lines, line => line.Contains(text, StringComparison.Ordinal)
                    && line.Contains('│') && line.Contains('─') == false && !line.Contains("SkillView", StringComparison.Ordinal));
                Assert.True(y > 0 && y + 1 < height, $"Header {text}: row {y}.\n" + string.Join("\n", lines));
                var x = lines[y].IndexOf(text, StringComparison.Ordinal);
                Assert.Contains('─', lines[y - 1]);
                Assert.Contains('─', lines[y + 1]);
                return (x, y, app.Driver.Contents![y, x].Attribute);
            }
        }
        finally { TuiHelpers.SetTheme(previous); WingetTuiTheme.Register(previous); }
    }
}
