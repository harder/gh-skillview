using SkillView.Ui.Theming;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Prominent loading feedback owned by its tab's current request.
/// Tabs may opt into focus to retain non-text shortcut routing during a load.
internal sealed class TabLoadingView : View
{
    private readonly SpinnerView _spinner;

    internal TabLoadingView(string message)
    {
        X = Pos.Center();
        Y = Pos.Center();
        Width = message.Length + 4;
        Height = 1;
        Visible = false;
        CanFocus = false;
        _spinner = new SpinnerView
        {
            Width = 1,
            Height = 1,
            AutoSpin = false,
            Style = new SpinnerStyle.Dots(),
        };
        var label = new Label { X = 2, Text = message };
        TuiHelpers.ApplyScheme(SchemeNames.Base, this, _spinner, label);
        Add(_spinner, label);
    }

    internal void SetLoading(bool loading)
    {
        Visible = loading;
        _spinner.AutoSpin = loading;
    }
}
