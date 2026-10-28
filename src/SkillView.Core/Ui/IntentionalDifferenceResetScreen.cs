using SkillView.Logging;
using SkillView.Preferences;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using SkillView.Ui.Theming;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

internal static class IntentionalDifferenceResetScreen
{
    internal static void Show(IApplication app, Logger logger, DifferenceResetReview? review, string store, string? refusal,
        Func<DifferenceResetReview, CancellationToken, Task<DifferenceResetResult>> reset, CancellationToken lifetime,
        Action<Dialog>? readyForTests = null, string? contextDetails = null)
    {
        if (lifetime.IsCancellationRequested) return;
        using var dialog = new Dialog { Title = "Reset saved intentional differences", Width = Dim.Percent(96), Height = Dim.Percent(94) };
        using var operation = new ModalOperationTracker(app, logger, "issues.reset");
        var text = new Editor
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
            ReadOnly = true,
            WordWrap = true,
            Text = Plain("Store: " + store + (contextDetails is null ? "" : "\n" + contextDetails) + "\n\n" + (review is null ? "Reset cannot be admitted.\n" + refusal
                : $"Review {review.Files.Length} remaining ordinary files ({review.Files.Sum(file => file.Data.Length)} bytes).\n\n"
                    + "Confirming forgets only saved intentional-difference acknowledgements and their history. A raw backup is preserved first. Payloads, gh registry, cleanup choices, Locations and saved operations are unaffected.\n\n"
                    + "Backup: " + Path.Combine(store, review.Backup ?? "backup-" + review.Records![8..])
                    + "\n\nOnly one backup is retained (at most 512 raw files of 2 KiB plus a 512 KiB manifest). Before another reset, close SkillView and externally archive the entire backup outside this store. No backup is automatically deleted.\n\n"
                    + "Interrupted work is resumed only through a new exact Reset review. Cancel is the default; no change occurs just by opening this review."))
        };
        var status = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, Text = review is null ? "Refused — exact details above" : "Review the location and what will be forgotten." };
        var cancel = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "Cancel", IsDefault = true };
        var confirm = new SingleRowButton { X = Pos.Right(cancel) + 2, Y = Pos.AnchorEnd(1), Text = "Confirm Reset", Enabled = review is not null };
        var closing = false;
        void Close()
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            { closing = true; operation.Cancel(); status.Text = "Canceling — preservation may already have started"; return; }
            app.RequestStop();
        }
        cancel.Accepting += (_, e) => { e.Handled = true; Close(); };
        confirm.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (review is null || operation.CurrentOwnership != ModalOperationTracker.Ownership.None || lifetime.IsCancellationRequested) return;
            if (!operation.TryStart(async token =>
            {
                string message;
                try
                {
                    var result = await reset(review, token).ConfigureAwait(false);
                    message = result.Message;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested || lifetime.IsCancellationRequested)
                { message = "Reset interrupted. Do not assume choices are active or forgotten. Use Reset again to inspect the exact remaining state and backup at " + store + "."; }
                catch (Exception ex) { logger.Error("issues.reset", ex.ToString()); message = "Reset did not complete. " + ex.Message + "\nStore: " + store + "\nInspect Reset again; preserve the reported backup/remaining entries."; }
                operation.InvokeTerminalIfActive(() =>
                {
                    if (!lifetime.IsCancellationRequested)
                    {
                        text.Text = Plain(message); text.CaretOffset = 0;
                        status.Text = "Outcome above; close and inspect again before retrying.";
                        confirm.Enabled = false; cancel.Text = "Close"; cancel.SetFocus();
                    }
                    operation.Release();
                    if (closing || lifetime.IsCancellationRequested) app.RequestStop();
                });
            })) return;
            confirm.Enabled = false; status.Text = "Preserving raw bytes before reset…";
        };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; Close(); } };
        dialog.IsRunningChanging += (_, args) =>
        { if (!args.NewValue && operation.CurrentOwnership != ModalOperationTracker.Ownership.None) { args.Cancel = true; Close(); } };
        dialog.Add(text, status, cancel, confirm); TuiHelpers.ConfigureReadOnlyPane(text, SchemeNames.Dialog); cancel.SetFocus();
        using var registration = lifetime.Register(() => { operation.Cancel(); app.Invoke(() => { if (dialog.IsRunning) Close(); }); });
        readyForTests?.Invoke(dialog);
        if (!lifetime.IsCancellationRequested) app.Run(dialog);
    }
    private static string Plain(string value) => TerminalEscapeSanitizer.Sanitize(value) ?? "";
}
