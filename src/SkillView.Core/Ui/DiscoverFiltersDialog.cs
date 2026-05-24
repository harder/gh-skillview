using SkillView.Gh;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Compact Discover filter editor. Keeps owner / agent / limit / hidden-dir
/// controls out of the always-visible search chrome while preserving the same
/// filter model used by the gh skill search adapter.
internal sealed class DiscoverFiltersDialog
{
    internal sealed record Result(
        bool Accepted,
        string Owner,
        string Agent,
        int Limit,
        bool HiddenDirs);

    private readonly IApplication _app;
    private readonly string _owner;
    private readonly string _agent;
    private readonly int _limit;
    private readonly bool _hiddenDirs;
    private readonly bool _supportsHiddenDirs;

    internal DiscoverFiltersDialog(
        IApplication app,
        string owner,
        string agent,
        int limit,
        bool hiddenDirs,
        bool supportsHiddenDirs)
    {
        _app = app;
        _owner = owner;
        _agent = agent;
        _limit = limit;
        _hiddenDirs = hiddenDirs;
        _supportsHiddenDirs = supportsHiddenDirs;
    }

    internal Result Show()
    {
        var result = new Result(
            Accepted: false,
            Owner: _owner,
            Agent: _agent,
            Limit: _limit,
            HiddenDirs: _hiddenDirs);

        var dialog = new Dialog
        {
            Title = " Discover filters ",
            Width = 56,
            Height = 13,
        };
        dialog.SchemeName = SchemeNames.Dialog;

        var ownerLabel = new Label { X = 1, Y = 0, Text = "Owner:" };
        var ownerField = new TextField
        {
            X = 10,
            Y = 0,
            Width = Dim.Fill(2),
            Text = _owner,
        };
        TuiHelpers.ConfigureTextInput(ownerField, SkillViewStyling.DialogSchemeName);

        var agentLabel = new Label { X = 1, Y = 2, Text = "Agent:" };
        var agentField = new TextField
        {
            X = 10,
            Y = 2,
            Width = Dim.Fill(2),
            Text = _agent,
        };
        TuiHelpers.ConfigureTextInput(agentField, SkillViewStyling.DialogSchemeName);

        var limitLabel = new Label { X = 1, Y = 4, Text = "Limit:" };
        var limitField = new NumericUpDown<int>
        {
            X = 10,
            Y = 4,
            Value = Math.Clamp(_limit, 1, 200),
            Increment = 10,
        };

        var hiddenDirs = new CheckBox
        {
            X = 1,
            Y = 6,
            Text = _supportsHiddenDirs
                ? "_Show hidden dirs"
                : "_Show hidden dirs (unsupported by current gh)",
            Value = _hiddenDirs ? CheckState.Checked : CheckState.UnChecked,
            Enabled = _supportsHiddenDirs,
        };

        var hint = new Label
        {
            X = 1,
            Y = 8,
            Width = Dim.Fill(2),
            Text = "These filters apply to the next search.",
        };

        var saveButton = new Button
        {
            X = Pos.Center() - 10,
            Y = Pos.AnchorEnd(1),
            Text = "Save",
            IsDefault = true,
        };
        var cancelButton = new Button
        {
            X = Pos.Center() + 2,
            Y = Pos.AnchorEnd(1),
            Text = "Cancel",
        };

        saveButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            result = new Result(
                Accepted: true,
                Owner: ownerField.Text.Trim(),
                Agent: agentField.Text.Trim(),
                Limit: Math.Clamp(limitField.Value, 1, 200),
                HiddenDirs: hiddenDirs.Value == CheckState.Checked);
            _app.RequestStop();
        };
        cancelButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _app.RequestStop();
        };

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Esc)
            {
                return;
            }

            key.Handled = true;
            _app.RequestStop();
        };

        dialog.Add(
            ownerLabel, ownerField,
            agentLabel, agentField,
            limitLabel, limitField,
            hiddenDirs,
            hint,
            saveButton, cancelButton);

        TuiHelpers.ApplyScheme(
            SkillViewStyling.DialogSchemeName,
            dialog,
            ownerLabel, ownerField,
            agentLabel, agentField,
            limitLabel, limitField,
            hiddenDirs,
            hint,
            saveButton, cancelButton);

        _app.Run(dialog);
        dialog.Dispose();

        return result;
    }
}
