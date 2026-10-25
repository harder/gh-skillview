using SkillView.Ui.Theming;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Prominent loading feedback owned by its tab's current request.
/// Transfers browse focus before hiding content, then restores it on completion.
internal sealed class TabLoadingView : View
{
    private readonly SpinnerView _spinner;
    private View? _returnFocus;

    internal TabLoadingView(string message)
    {
        X = Pos.Center();
        Y = Pos.Center();
        Width = message.Length + 4;
        Height = 1;
        Visible = false;
        CanFocus = true;
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

    internal void SetContentLoading(bool loading, View fallback, params View[] content)
    {
        if (loading)
        {
            var starting = !Visible;
            if (starting) _returnFocus = content.Where(view => view.HasFocus)
                .Select(view => view.MostFocused ?? view).FirstOrDefault();
            SetLoading(true);
            if (starting && _returnFocus is not null) SetFocus();
            foreach (var view in content) view.Visible = false;
        }
        else
        {
            foreach (var view in content) view.Visible = true;
            if (HasFocus)
            {
                var target = _returnFocus ?? fallback;
                target.SetFocus();
                if (!target.HasFocus) fallback.SetFocus();
            }
            SetLoading(false);
            _returnFocus = null;
        }
    }

    internal void SetLoading(bool loading)
    {
        Visible = loading;
        _spinner.AutoSpin = loading;
    }
}
