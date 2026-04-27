using SkillView.Ui;
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
}
