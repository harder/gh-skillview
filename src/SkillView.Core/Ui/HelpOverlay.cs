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
    internal static string BuildMarkdown(ActionScreen screen = ActionScreen.Discover, bool showingLogs = false, bool sourceDiffVisible = false)
    {
        var title = screen == ActionScreen.UpdateReview ? "Update review" : screen.ToString();
        var primary = new[] { ActionLabels.Selection, ActionLabels.Focus, ActionLabels.Back, ActionLabels.Help, ActionLabels.Locations };
        var advanced = new[] { ActionLabels.Tabs, ActionLabels.History, ActionLabels.Quit, ActionLabels.ListQuit };
        return "## " + title + "\n\n" + string.Join("\n", ActionLabels.ScreenActions(screen, showingLogs, sourceDiffVisible).Select(action => action.Markdown))
            + "\n\n## Primary\n\n" + string.Join("\n", primary.Select(action => action.Markdown))
            + "\n\n## Advanced\n\n" + string.Join("\n", advanced.Select(action => action.Markdown))
            + "\n\n_Printable shortcuts type into focused text fields. Leave the field first; F1 and Ctrl+Q remain available._"
            + "\n\n_Press **Esc**, **Enter** (on Close), **F1**, or **?** to close._";
    }

    internal static string MarkdownBodyForTests => BuildMarkdown();

    internal static void Show(IApplication app, ActionScreen screen, bool showingLogs, bool sourceDiffVisible = false)
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
            Text = BuildMarkdown(screen, showingLogs, sourceDiffVisible),
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
