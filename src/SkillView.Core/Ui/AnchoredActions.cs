using System.Drawing;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

internal static class AnchoredActions
{
    internal static Point Position(Rectangle anchor, Size menu, Rectangle screen) => new(
        Math.Clamp(anchor.Left, screen.Left, Math.Max(screen.Left, screen.Right - menu.Width)),
        anchor.Bottom + menu.Height <= screen.Bottom ? anchor.Bottom
            : Math.Max(screen.Top, anchor.Top - menu.Height));

    internal static PopoverMenu Show(View anchor, IEnumerable<(string Label, Action Run)> actions)
    {
        var choices = actions.ToArray();
        var screen = anchor.App!.Screen;
        var size = new Size(Math.Min(screen.Width, choices.Select(choice => choice.Label.GetColumns()).DefaultIfEmpty(12).Max() + 4),
            Math.Min(screen.Height, choices.Length + 2));
        var menu = new PopoverMenu(choices.Select(action => new MenuItem
        { Title = action.Label, Action = action.Run }).ToArray())
        { App = anchor.App, SchemeName = SchemeNames.Dialog };
        menu.Root!.BorderStyle = LineStyle.Single;
        menu.Root.Width = size.Width; menu.Root.Height = size.Height;
        TuiHelpers.ApplyScheme(SchemeNames.Dialog, menu, menu.Root);
        menu.Root.ViewportSettings &= ~ViewportSettingsFlags.Transparent;
        anchor.App.Popovers!.Register(menu);
        var control = anchor.FrameToScreen();
        menu.MakeVisible(Position(control, size, screen), control);
        return menu;
    }
}
