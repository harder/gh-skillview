using SkillView.Diagnostics;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

/// Embedded full-screen Doctor view — replaces DoctorScreen.Show()'s
/// Application.Run(window) subloop with a Visible-toggle pane that lives
/// inside the main window. Reuses the existing DoctorScreen.Render markdown
/// so the 3 DoctorScreenTests stay green unchanged.
///
/// Doctor isn't a primary tab (no pill in TabBarView). It's reached via
/// `d` and dismissed via Esc / q — which restores the previously-active
/// primary tab through onLeaveTab.
internal sealed class DoctorTabView : FrameView
{
    private readonly Action _onLeaveTab;
    private readonly Markdown _body;
    private readonly StatusBar _statusBar;

    internal DoctorTabView(Action onLeaveTab)
    {
        _onLeaveTab = onLeaveTab;
        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;

        _body = new Markdown
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            Text = "_(loading…)_",
        };
        TuiHelpers.ConfigureMarkdownPane(_body, SchemeNames.Base);

        _statusBar = new StatusBar(TuiHelpers.WithMarkdownShortcuts(
        [
            new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Back" },
            new Shortcut { Title = "q",   HelpText = "Quit" },
        ]));

        TuiHelpers.ApplyScheme(SchemeNames.Base, this, _body, _statusBar);

        KeyDown += (_, key) =>
        {
            if (key.Handled) return;
            if (key.KeyCode == KeyCode.Esc || key.AsRune.Value == 'q' || key.AsRune.Value == 'Q')
            {
                key.Handled = true;
                _onLeaveTab();
            }
        };

        Add(_body, _statusBar);
    }

    /// Replace the rendered body with a fresh report. Call this on activate
    /// so capability/auth changes during the session are reflected.
    internal void SetReport(EnvironmentReport report)
    {
        _body.Text = DoctorScreen.Render(report);
    }
}
