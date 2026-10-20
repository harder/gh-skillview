using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Chooses a source tag only; preparation and every approval stay in Updates.
internal static class GitTagSelectionModal
{
    internal static string? Show(IApplication app, InstalledSkill selected, CancellationToken lifetimeToken = default,
        Action<Dialog>? readyForTests = null)
    {
        if (lifetimeToken.IsCancellationRequested) return null;
        using var dialog = new Dialog
        {
            Title = "Choose version",
            Width = Dim.Percent(94),
            Height = Dim.Percent(94),
            SchemeName = SchemeNames.Dialog
        };
        var fields = MaintenanceQueueFormatter.RenderFields(
            [("Installation", selected.Name), ("Location", selected.ResolvedPath),
             ("Repository", selected.FrontMatter.Upstream), ("Current ref", selected.FrontMatter.GithubRef),
             ("Skill subpath", selected.FrontMatter.GithubPath is "" ? "repository root" : selected.FrontMatter.GithubPath)], lifetimeToken, MaintenanceQueueFormatter.MaxSummaryCharacters);
        var context = new Markdown
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(8),
            Text = fields.Text + (fields.OmittedFields > 0 ? "Some long context fields are omitted here; the saved review retains exact evidence." : "")
        };
        TuiHelpers.ConfigureMarkdownPane(context, SchemeNames.Dialog);
        var label = new Label { X = 0, Y = Pos.AnchorEnd(7), Text = "Git tag:" };
        var tag = new TextField { X = 10, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 1, Text = "" };
        var example = new Label { X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 1, Text = "Example: v1.2.3. Tags only; branches and raw commits are excluded." };
        var future = new Label { X = 0, Y = Pos.AnchorEnd(5), Width = Dim.Fill(), Height = 1, Text = "One-time choice; later ordinary Update follows latest." };
        var consumers = new Label { X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(), Height = 1, Text = "Every agent sharing this physical installation is affected." };
        var pins = new Label { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1, Text = "Pins require a separate reviewed unpin before choosing a tag." };
        var status = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, Text = "Enter prepares review; Esc cancels. Nothing is applied." };
        var cancel = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        var prepare = new SingleRowButton { X = Pos.Right(cancel) + 2, Y = Pos.AnchorEnd(1), Text = "Prepare tag", IsDefault = true, Enabled = !selected.Pinned };
        string? result = null;
        cancel.Accepting += (_, args) => { args.Handled = true; app.RequestStop(); };
        prepare.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (lifetimeToken.IsCancellationRequested || selected.Pinned) return;
            var value = tag.Text.ToString().Trim();
            try { GhRevisionResolver.ValidateTag(value); }
            catch (IOException)
            { status.Text = "Enter a valid Git tag (for example v1.2.3)."; tag.SetFocus(); return; }
            result = value;
            app.RequestStop();
        };
        // No printable key handler: tag characters belong to the text field.
        dialog.Add(context, label, tag, example, future, consumers, pins, status, cancel, prepare);
        tag.SetFocus();
        using var cancellation = lifetimeToken.Register(() => app.Invoke(() =>
        {
            if (dialog.IsRunning) app.RequestStop();
        }));
        if (lifetimeToken.IsCancellationRequested) return null;
        readyForTests?.Invoke(dialog);
        app.Run(dialog);
        return lifetimeToken.IsCancellationRequested ? null : result;
    }
}
