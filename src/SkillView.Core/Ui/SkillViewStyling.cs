using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;

namespace SkillView.Ui;

internal static class SkillViewStyling
{
    public static string BaseSchemeName => SchemeManager.SchemesToSchemeName(Schemes.Base)!;
    public static string DialogSchemeName => SchemeManager.SchemesToSchemeName(Schemes.Dialog)!;
}
