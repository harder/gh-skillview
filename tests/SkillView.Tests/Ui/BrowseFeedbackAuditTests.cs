using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class BrowseFeedbackAuditTests
{
    [Fact]
    public void SingleCopyMenuDoesNotOfferAComparisonWithNoReference()
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(120, 36);
        var snapshot = InventorySnapshot.Empty with { Skills = [InstalledOverviewTests.Copy("/skills/demo", "demo")] };
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(snapshot), (_, _, _) => Task.CompletedTask, () => { })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            window.Layout();
            var more = view.SubViews.OfType<Button>().Single(button => button.Text == "More actions");
            more.SetFocus();
            app.Keyboard.RaiseKeyDownEvent(Key.Enter);
            var popup = app.Popovers!.GetActivePopover();
            Assert.NotNull(popup);
            var entries = Assert.IsType<PopoverMenu>(popup).Root!.SubViews.OfType<MenuItem>().Select(item => item.Title).ToArray();
            Assert.Contains("Open location (o)", entries);
            Assert.DoesNotContain("Compare copies...", entries);
        }
        finally { app.End(session!); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void CollapsedFiltersStillExplainPinScopeAndSortChanges(int width, int height)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        var snapshot = InventorySnapshot.Empty with { Skills = [InstalledOverviewTests.Copy("/skills/demo", "demo")] };
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(snapshot), (_, _, _) => Task.CompletedTask, () => { })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            window.Layout();
            view.FocusList();
            var filters = view.SubViews.OfType<Button>().Single(button => button.Text == "Filters (f)");
            app.Keyboard.RaiseKeyDownEvent(new Key('P'));
            Assert.Equal("Filters* (f)", filters.Text);
            Assert.Contains(view.SubViews.OfType<Label>(), label => label.Text.Contains("Pins: pinned", StringComparison.Ordinal));
            app.Keyboard.RaiseKeyDownEvent(new Key('G'));
            Assert.Contains(view.SubViews.OfType<Label>(), label => label.Text.Contains("Scope: user", StringComparison.Ordinal));
            app.Keyboard.RaiseKeyDownEvent(new Key('s'));
            Assert.Contains(view.SubViews.OfType<Label>(), label => label.Text.Contains("Sort: source", StringComparison.Ordinal));
            var clear = view.SubViews.OfType<Button>().Single(button => button.Text == "Clear");
            clear.InvokeCommand(Command.Accept);
            Assert.Equal("Filters (f)", filters.Text);
            Assert.Single(view.RowsForTests);
            Assert.DoesNotContain(view.SubViews.OfType<Label>(), label => label.Text.Contains("Pins: pinned", StringComparison.Ordinal)
                || label.Text.Contains("Scope: user", StringComparison.Ordinal));
        }
        finally { app.End(session!); }
    }

    [Fact]
    public void FocusedButtonMnemonicHasContrastingForegroundAndBackground()
    {
        var scheme = WingetTuiTheme.CreateBaseScheme();
        Assert.NotEqual(scheme.HotFocus.Foreground, scheme.HotFocus.Background);
        Assert.Equal(scheme.Focus.Foreground, scheme.HotFocus.Foreground);
    }
}
