using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Threading;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

internal static class QuarantineReview
{
    private enum ReviewState { Loading, MoveReview, Result, UndoReview, Refused }

    internal static void Show(IApplication app, Logger logger, OperationSubject subject, CancellationToken lifetime,
        Func<CancellationToken, Task<QuarantineRecord>> preview,
        Func<QuarantineRecord, bool, bool, CancellationToken, Task<StandaloneMoveResult>> apply,
        Func<string, bool, CancellationToken, Task<StandaloneMoveResult>> undo,
        Action<Dialog>? ready = null)
    {
        using var dialog = new Dialog { Title = "Selected standalone Move aside", Width = Dim.Percent(96), Height = Dim.Percent(96) };
        using var operation = new ModalOperationTracker(app, logger, "standalone.review");
        var state = ReviewState.Loading;
        QuarantineRecord? review = null;
        StandaloneMoveResult? result = null;
        string? undoId = null;
        var closeRequested = false; var readyToClose = false;
        var detail = new Markdown { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(7) };
        TuiHelpers.ConfigureMarkdownPane(detail, SkillViewStyling.BaseSchemeName);
        var standalone = new CheckBox { X = 0, Y = Pos.AnchorEnd(6), Text = "I chose this standalone custom copy; no preferred survivor is implied", Enabled = false };
        var loss = new CheckBox { X = 0, Y = Pos.AnchorEnd(5), Text = "I reviewed this location becoming vacant; sessions remain unknown", Enabled = false };
        var restore = new CheckBox { X = 0, Y = Pos.AnchorEnd(4), Text = "Restore this retained original only to its vacant original location", Visible = false };
        var status = new Label { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1, Text = "Inspecting exact native target and fresh inventory…" };
        var confirm = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(2), Text = "Move aside", Enabled = false };
        var inspect = new SingleRowButton { X = Pos.Right(confirm) + 1, Y = Pos.AnchorEnd(2), Text = "Inspect retained", Visible = false };
        var reviewUndo = new SingleRowButton { X = Pos.Right(inspect) + 1, Y = Pos.AnchorEnd(2), Text = "Review Undo", Visible = false };
        var close = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "Back (Esc)", IsDefault = true };
        bool Busy() => operation.CurrentOwnership != ModalOperationTracker.Ownership.None;
        void Controls()
        {
            var idle = !Busy();
            standalone.Enabled = loss.Enabled = idle && state == ReviewState.MoveReview;
            restore.Visible = state == ReviewState.UndoReview;
            restore.Enabled = idle && state == ReviewState.UndoReview;
            confirm.Text = state == ReviewState.UndoReview ? "Restore original" : "Move aside";
            confirm.Enabled = idle && (state == ReviewState.MoveReview && standalone.Value == CheckState.Checked && loss.Value == CheckState.Checked
                || state == ReviewState.UndoReview && restore.Value == CheckState.Checked && undoId is not null);
            inspect.Visible = reviewUndo.Visible = result?.Native.Id is not null;
            inspect.Enabled = reviewUndo.Enabled = idle && result?.Native.Status == "quarantined";
        }
        string Literal(string value) => MarkdownTableFormatter.FormatCodeSpan(TerminalEscapeSanitizer.Sanitize(value) ?? "");
        string Restart(string id) => "\n\nAfter restart use the same HOME/state/CWD and scan roots with `skillview quarantine-history` and `skillview undo --id "
            + id + "` for a fresh preview, then `--yes` after review. Global --scan-root flags precede the command. F4 History lists replacement operations only; it does not list quarantine. No overwrite or automatic Undo.";
        void ShowResult(StandaloneMoveResult value)
        {
            result = value;
            var native = value.Native;
            detail.Text = "## Native result: " + Literal(native.Status) + "\n\n" + Literal(native.Message)
                + "\n\n" + Literal(value.Verification) + "\n\nOperation: " + Literal(native.Id)
                + "\n\nOriginal: " + Literal(native.OriginalPath) + "\n\nRetained slot: " + Literal(native.RetainedPath) + Restart(native.Id);
            status.Text = native.MutationObserved ? "Native move/restore observed. Read verification separately." : "No completed native move reported; inspect the result.";
            standalone.Value = loss.Value = restore.Value = CheckState.UnChecked;
            state = ReviewState.Result;
        }
        var completionPending = false;
        void PublishControls(object? sender = null, EventArgs? args = null)
        {
            if (!completionPending || Busy()) return;
            completionPending = false;
            Controls();
            if (!closeRequested && !lifetime.IsCancellationRequested)
            {
                if (state == ReviewState.MoveReview) standalone.SetFocus();
                else if (state == ReviewState.UndoReview) restore.SetFocus();
                else if (inspect.Enabled) inspect.SetFocus();
                else close.SetFocus();
            }
            if (closeRequested || lifetime.IsCancellationRequested) { readyToClose = true; app.RequestStop(); }
        }
        void Finish()
        {
            operation.Release();
            completionPending = true;
            PublishControls();
        }
        void Run(Func<CancellationToken, Task<Action>> work)
        {
            if (Busy() || lifetime.IsCancellationRequested) return;
            confirm.Enabled = standalone.Enabled = loss.Enabled = restore.Enabled = inspect.Enabled = reviewUndo.Enabled = false;
            status.Text = "Working on the selected operation… Esc cancels and waits.";
            operation.TryStart(token => Task.Run(async () =>
            {
                try
                {
                    using var cancellation = new CancellationSource(token, lifetime, CancellationCallbackReporter.For(logger, "standalone.review"));
                    var publish = await work(cancellation.Token).ConfigureAwait(false);
                    operation.InvokeTerminalIfActive(() => { if (!closeRequested && !lifetime.IsCancellationRequested) publish(); Finish(); });
                }
                catch (Exception ex)
                {
                    logger.Error("standalone.review", ex.ToString());
                    operation.InvokeTerminalIfActive(() =>
                    {
                        state = ReviewState.Refused; standalone.Value = loss.Value = restore.Value = CheckState.UnChecked;
                        detail.Text = "## Refused or incomplete\n\n" + Literal(ex.Message)
                            + "\n\nNo peer was selected. Native/retained objects may need inspection if execution started. Refresh before another review."
                            + (result is null ? "" : Restart(result.Native.Id));
                        status.Text = "Review closed. Inspect retained evidence; no automatic retry."; Finish();
                    });
                }
            }));
        }
        void Close()
        {
            if (Busy()) { closeRequested = true; operation.Cancel(); status.Text = "Canceling; retaining native results and reconciling before close…"; return; }
            readyToClose = true; app.RequestStop();
        }
        standalone.ValueChanged += (_, _) => Controls(); loss.ValueChanged += (_, _) => Controls(); restore.ValueChanged += (_, _) => Controls();
        confirm.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (!confirm.Enabled || Busy()) return;
            var moving = state == ReviewState.MoveReview;
            var captured = review; var id = undoId;
            var standaloneApproval = standalone.Value == CheckState.Checked; var lossApproval = loss.Value == CheckState.Checked;
            standalone.Value = loss.Value = restore.Value = CheckState.UnChecked;
            Run(async token =>
            {
                var value = moving ? await apply(captured!, standaloneApproval, lossApproval, token).ConfigureAwait(false)
                    : await undo(id!, true, token).ConfigureAwait(false);
                return () => ShowResult(value);
            });
        };
        void Inspect(bool forUndo)
        {
            if (result?.Native is not { Status: "quarantined" } native || Busy()) return;
            Run(async token =>
            {
                var value = await undo(native.Id, false, token).ConfigureAwait(false);
                return () =>
                {
                    detail.Text = "## " + (forUndo ? "Review Undo" : "Retained operation inspection") + "\n\n" + Literal(value.Native.Message)
                        + "\n\nOperation: " + Literal(native.Id) + "\n\nOriginal: " + Literal(value.Native.OriginalPath)
                        + "\n\nRetained: " + Literal(value.Native.RetainedPath)
                        + "\n\nRestore rechecks the vacant destination, native identities, consumers and original context. A new dangling exposure can refuse Undo." + Restart(native.Id);
                    undoId = forUndo && value.Native.Succeeded && value.Native.Status == "quarantined" ? native.Id : null;
                    state = undoId is null ? ReviewState.Result : ReviewState.UndoReview;
                    status.Text = forUndo ? "Explicit restoration approval required. Undo has no --expect fingerprint contract." : "Inspection only; nothing restored.";
                };
            });
        }
        inspect.Accepting += (_, e) => { e.Handled = true; Inspect(false); };
        reviewUndo.Accepting += (_, e) => { e.Handled = true; Inspect(true); };
        close.Accepting += (_, e) => { e.Handled = true; Close(); };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; Close(); } };
        dialog.IsRunningChanging += (_, args) => { if (!args.NewValue && !readyToClose && Busy()) { args.Cancel = true; Close(); } };
        detail.Text = "## Selected copy\n\n" + Literal(subject.Path) + "\n\nFresh standalone admission is independent of any comparison. Owner/provider/managed/shared/tracked/partial/unknown states remain refused. Move aside retains the original native directory on the same volume; it is not security isolation or a registry edit.";
        dialog.Add(detail, standalone, loss, restore, status, confirm, inspect, reviewUndo, close);
        close.SetFocus();
        Run(async token =>
        {
            var value = await preview(token).ConfigureAwait(false);
            return () =>
            {
                review = value; state = ReviewState.MoveReview;
                detail.Text += "\n\n## Move aside review\n\nExact location: " + Literal(value.OriginalPath)
                    + "\n\nReview fingerprint: " + Literal(value.ReviewFingerprint)
                    + "\n\nEffect: this location becomes vacant. Live bytes, permissions and links move to retained state. No peer, registry record or running session is changed or selected by this action. Undo requires the same original context and an empty destination; new consumers can refuse restoration.";
                status.Text = "Review both independent approvals, then Move aside. Cancel keeps files.";
            };
        });
        using var registration = lifetime.Register(() => app.Invoke(() => { if (dialog.IsRunning) Close(); }));
        app.Iteration += PublishControls;
        try
        {
            ready?.Invoke(dialog);
            if (!lifetime.IsCancellationRequested) app.Run(dialog);
        }
        finally { app.Iteration -= PublishControls; }
    }
}
