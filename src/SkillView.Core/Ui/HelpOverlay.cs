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
    internal static string BuildMarkdown() => """
## Primary

- **↑ / ↓** — move selection
- **Enter** — preview the selected search result or open the selected change item
- **f** — filters in Discover · filter box in Installed
- **1 / 2 / 3** or **← / →** — switch tabs
- **?** or **F1** — show this help

## Discover

- **/** — focus the Discover query box
- **i** — install selected via compact confirm
- **I** — install selected via advanced wizard
- **A** — install *all* skills from the selected repo (gh 2.94+)
- **o** — open the selected repo in the browser

## Installed

- **x** — remove the selected installed skill
- **o** — open the selected skill path
- **G** — cycle scope filter (all / user / project / custom)

## Changes

- **c** — open cleanup
- **d** — open Doctor

## Advanced

- **s / S** — cycle sort in Discover or Installed
- **P** — cycle Installed pin filter
- **G** — cycle Installed scope filter (pushes `--scope` to gh)
- **e** — toggle raw / rendered preview
- **l / r** — show / hide logs
- **q / Esc** — quit at root · close modal otherwise

_Press **Esc**, **Enter**, or **?** to close._
""";

    internal static string MarkdownBodyForTests => BuildMarkdown();

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
            Text = BuildMarkdown(),
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
