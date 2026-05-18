using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Modal keybindings reference accessible via `?` (and F1). Renders the
/// winget-tui-style grouped key table as Markdown for consistent typography
/// with the rest of the app. Dismisses on Esc, Enter, `?`, or button click.
internal static class HelpOverlay
{
    private const string MarkdownBody = """
## Navigation

- **↑ / k**, **↓ / j** — move row · scroll detail when focused
- **PgUp / PgDn**, **Home / End** — jump
- **Tab / Shift+Tab** — focus list ↔ detail
- **← / →** — previous / next tab
- **1 / 2 / 3** — Search · Installed · Updates

## Search & filter

- **/** — focus the search box
- **f** — cycle source filter (planned)
- **P** — cycle pin filter (planned, Installed/Updates)
- **r** — refresh current view (planned)
- **S** — cycle sort (planned)

## Actions

- **i** — install selected (compact modal — planned)
- **I** — install advanced / show Installed view
- **u / U** — update selected · update all marked
- **x** — remove selected (planned compact confirm)
- **p** — pin / unpin (planned)
- **o** — open homepage in browser
- **e** — toggle raw / rendered preview

## Batch (Updates tab — planned)

- **Space** — toggle mark on current row
- **a** — mark / unmark all visible

## Modes

- **?** or **F1** — toggle this help
- **d** — open Doctor
- **c** — review cleanup candidates
- **h** — toggle hidden-dir access for preview/install
- **l** — show / hide logs
- **q / Esc** — quit at root · close modal otherwise

_Press **Esc**, **Enter**, or **?** to close._
""";

    internal static void Show(IApplication app)
    {
        var dialog = new Dialog
        {
            Title = " SkillView keys ",
            Width = Dim.Percent(70),
            Height = Dim.Percent(80),
        };
        dialog.SchemeName = SchemeNames.Dialog;

        var body = new Markdown
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
            Text = MarkdownBody,
        };
        TuiHelpers.ConfigureMarkdownPane(body, SchemeNames.Dialog);
        dialog.Add(body);

        var close = new Button
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1),
            Text = "Close",
            IsDefault = true,
        };
        close.Accepting += (_, _) => app.RequestStop();
        dialog.Add(close);

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc
                || key.AsRune.Value == '?'
                || key.KeyCode == KeyCode.F1)
            {
                key.Handled = true;
                app.RequestStop();
            }
        };

        app.Run(dialog);
        dialog.Dispose();
    }
}
