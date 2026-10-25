using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Theming;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledCategoryColorsTests
{
    [Fact]
    public void CuesDifferButPreserveSelectionAndDisabledColors()
    {
        var baseline = WingetTuiTheme.CreateTableScheme();
        var colors = new InstalledCategoryColors(baseline, AppTheme.Default);
        var locations = new[] { colors.Location(Scope.User)!, colors.Location(Scope.Project)!, colors.Location(Scope.Custom)! };
        Assert.Equal(3, locations.Select(s => s.Normal).Distinct().Count());
        var agents = new[] { colors.Agents("CDX")!, colors.Agents("CLD")!, colors.Agents("GHC")!, colors.Agents("CSR")! };
        Assert.Equal(4, agents.Select(s => s.Normal).Distinct().Count());
        foreach (var scheme in locations.Concat(agents))
        {
            Assert.Equal(baseline.Focus, scheme.Focus);
            Assert.Equal(baseline.Active, scheme.Active);
            Assert.Equal(baseline.Disabled, scheme.Disabled);
            Assert.Equal(baseline.Normal.Background, scheme.Normal.Background);
        }
        Assert.Null(colors.Agents("CDX CLD"));
        Assert.Null(colors.Agents("XYZ"));
    }

    [Fact]
    public void HighContrastKeepsExistingThemeAndTextBadges()
    {
        var colors = new InstalledCategoryColors(WingetTuiTheme.CreateTableScheme(), AppTheme.HighContrast);
        Assert.Null(colors.Location(Scope.User));
        Assert.Null(colors.Agents("CDX"));
    }
}
