using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Footer actions whose neighboring controls own the rows above and below.
internal sealed class SingleRowButton : Button
{
    public SingleRowButton() => Height = 1;

    protected override void OnInitializingShadowStyle(ValueChangingEventArgs<ShadowStyles?> args)
    {
        // In Terminal.Gui 2.5, None still allocates shadow margin space. Suppress
        // shadow initialization before the base constructor creates that margin.
        args.NewValue = null;
    }
}
