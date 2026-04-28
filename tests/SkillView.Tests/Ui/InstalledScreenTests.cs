using SkillView.Ui;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledScreenTests
{
    [Fact]
    public void BuildShortcuts_AdvertisesSearchAndFilterDistinctly()
    {
        var shortcuts = InstalledScreen.BuildShortcuts(canRemove: true, hasPackages: true);

        Assert.Contains(shortcuts, shortcut => shortcut.Title == "/" && shortcut.HelpText == "Search");
        Assert.Contains(shortcuts, shortcut => shortcut.Title == "f" && shortcut.HelpText == "Filter");
        Assert.DoesNotContain(shortcuts, shortcut => shortcut.Title == "/" && shortcut.HelpText == "Filter");
    }

    [Fact]
    public void DecideShortcut_ReturnsSearchHandoff_AndStopsInstalled()
    {
        var decision = InstalledScreen.DecideShortcut(new Key('/'), filterHasFocus: false, canRemove: true);

        Assert.Equal(InstalledScreen.ShortcutCommand.GoToSearch, decision.Command);
        Assert.True(decision.RequestStop);
    }

    [Fact]
    public void DecideShortcut_KeepsFFocusedOnFilter()
    {
        var decision = InstalledScreen.DecideShortcut(new Key('f'), filterHasFocus: false, canRemove: true);

        Assert.Equal(InstalledScreen.ShortcutCommand.FocusFilter, decision.Command);
        Assert.False(decision.RequestStop);
    }
}
