using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Covers the workspace with a clear resize message when the terminal is too
/// small for the two-pane layouts. Keeping this as one lightweight view avoids
/// every tab attempting its own cramped fallback layout.
internal sealed class TerminalSizeGuardView : FrameView
{
    internal const int MinimumWidth = 80;
    internal const int MinimumHeight = 24;

    private readonly Label _message;

    internal TerminalSizeGuardView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        CanFocus = false;
        Visible = false;

        _message = new Label
        {
            X = Pos.Center(),
            Y = Pos.Center(),
            TextAlignment = Alignment.Center,
            Text = BuildMessage(MinimumWidth, MinimumHeight),
        };
        TuiHelpers.ApplyScheme(SchemeNames.Base, this, _message);
        Add(_message);
    }

    internal void UpdateForSize(int width, int height)
    {
        Visible = IsTooSmall(width, height);
        if (Visible)
        {
            _message.Text = BuildMessage(width, height);
            SetNeedsDraw();
        }
    }

    internal static bool IsTooSmall(int width, int height) =>
        width > 0 && height > 0 && (width < MinimumWidth || height < MinimumHeight);

    internal static string BuildMessage(int width, int height) =>
        $"Terminal too small ({width}×{height})\nResize to at least {MinimumWidth}×{MinimumHeight}\nCtrl+Q quits";
}
