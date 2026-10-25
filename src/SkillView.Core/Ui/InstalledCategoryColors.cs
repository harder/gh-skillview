using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace SkillView.Ui;

/// Supplemental scanning cues; text badges and selection remain authoritative.
internal sealed class InstalledCategoryColors(Scheme baseScheme, AppTheme theme)
{
    private readonly Scheme _blue = Tint(baseScheme, WingetTuiTheme.Info);
    private readonly Scheme _green = Tint(baseScheme, WingetTuiTheme.Success);
    private readonly Scheme _purple = Tint(baseScheme, WingetTuiTheme.Selection);
    private readonly Scheme _gold = Tint(baseScheme, WingetTuiTheme.Accent);

    internal Scheme? Location(Scope scope) => theme == AppTheme.HighContrast ? null : scope switch
    {
        Scope.User => _blue,
        Scope.Project => _green,
        _ => _purple,
    };

    internal Scheme? Agents(string badges) => theme == AppTheme.HighContrast ? null : badges switch
    {
        "CDX" => _blue,
        "CLD" => _gold,
        "GHC" => _purple,
        "CSR" or "GMI" => _green,
        _ => null, // Mixed/unknown agents keep their explicit text and normal color.
    };

    private static Scheme Tint(Scheme source, Color color) => new(source)
    {
        Normal = new Attribute(color, source.Normal.Background),
    };
}
