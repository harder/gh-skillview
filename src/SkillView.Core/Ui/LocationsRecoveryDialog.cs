using SkillView.Inventory.Models;
using SkillView.Preferences;
using SkillView.Threading;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

internal static class LocationsRecoveryDialog
{
    internal static LocationRead? Show(IApplication app, TuiServices services, SkillViewWorkflowCoordinator workflows,
        InventorySnapshot? snapshot, CancellationToken lifetime, string details, Action<Dialog>? readyForTests = null)
    {
        using var dialog = new Dialog { Title = " Locations details / recovery ", Width = Dim.Fill(), Height = Dim.Fill() };
        using var operation = new ModalOperationTracker(app, services.Logger, "locations.recovery");
        using var linked = new CancellationSource(lifetime, operation.Token, CancellationCallbackReporter.For(services.Logger, "locations.recovery"));
        var token = linked.Token;
        var text = new Editor { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(5), ReadOnly = true, WordWrap = true };
        var hint = new Label { X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(), Text = "Tab to text: PgUp/PgDn, Ctrl+Home/End. Enter on Cancel is safe." };
        var reset = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(3), Text = "Reset saved Locations (keeps backup)", Enabled = false };
        var compact = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(2), Text = "Compact, keeping saved selection", Enabled = false };
        var cancel = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "Cancel", IsDefault = true };
        LocationRead? result = null; LocationResetReview? review = null; var active = true;
        void Display(string value)
        {
            // Bound literal evidence, no Markdown/active paths. The filesystem
            // names and supplied state context themselves are already bounded.
            text.Text = TerminalEscapeSanitizer.Sanitize(value.Length <= 64 * 1024 ? value : value[..(64 * 1024)] + "\n[display limit]") ?? "";
            text.Layout(); text.CaretOffset = 0; text.Viewport = text.Viewport with { X = 0, Y = 0 };
        }
        void Close() { active = false; operation.Cancel(); app.RequestStop(dialog); }
        void Start(bool? keep)
        {
            if (!active || token.IsCancellationRequested || operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            reset.Enabled = compact.Enabled = false;
            Display(details + "\n\n" + (keep is null ? "Reading native recovery review…" : "Applying reviewed recovery. Cancel may leave an incomplete recovery; reopen Recovery to inspect and resume."));
            operation.TryStart(async _ =>
            {
                LocationResetReview? captured = null; LocationRead? saved = null; string? error = null;
                try
                {
                    if (keep is null) captured = await Task.Run(() => workflows.PrepareLocationReset(snapshot, token), token).ConfigureAwait(false);
                    else saved = await Task.Run(() => workflows.ResetLocations(review!, keep.Value, snapshot, token), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { error = ex.Message; services.Logger.Error("locations.recovery", ex.ToString()); }
                operation.InvokeTerminalIfActive(() =>
                {
                    try
                    {
                        if (!active || token.IsCancellationRequested) return;
                        if (saved is not null) { result = saved; Close(); return; }
                        if (captured is not null) review = captured;
                        Display((error ?? review?.Explanation ?? "Recovery could not be reviewed.") + "\n\n" + details);
                        // A failed apply requires a new review. Do not reuse its
                        // old version/entry approvals against changed storage.
                        reset.Enabled = keep is null && review?.CanReset == true;
                        compact.Enabled = keep is null && review?.CanCompact == true;
                    }
                    finally { operation.Release(); }
                });
            });
        }
        reset.Accepting += (_, args) => { args.Handled = true; Start(false); };
        compact.Accepting += (_, args) => { args.Handled = true; Start(true); };
        cancel.Accepting += (_, args) => { args.Handled = true; Close(); };
        text.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter) key.Handled = true;
            else if ((key.KeyCode & ~KeyCode.ShiftMask) == KeyCode.Tab)
            {
                // Editor owns indentation bindings even when read-only. Keep
                // both directions available to the visible recovery buttons.
                key.Handled = true;
                if (key.KeyCode.HasFlag(KeyCode.ShiftMask)) cancel.SetFocus();
                else if (reset.Enabled) reset.SetFocus();
                else if (compact.Enabled) compact.SetFocus();
                else cancel.SetFocus();
            }
        };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; Close(); } };
        dialog.IsRunningChanging += (_, args) => { if (!args.NewValue) { active = false; operation.Cancel(); } };
        dialog.Add(text, hint, reset, compact, cancel);
        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, dialog, hint, reset, compact, cancel);
        TuiHelpers.ConfigureReadOnlyPane(text, SkillViewStyling.DialogSchemeName);
        Display(details); Start(null); cancel.SetFocus();
        readyForTests?.Invoke(dialog);
        try { app.Run(dialog); }
        finally { active = false; linked.TryCancel(); operation.Cancel(); }
        return result;
    }
}
