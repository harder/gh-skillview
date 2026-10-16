using SkillView.Cli;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Threading;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

internal enum LocalReimportAction { Prepare, Inspect, Apply, Recover, Undo }
internal sealed record LocalReimportRequest(LocalReimportAction Action, string? Id, ReplacementResult? Review, bool AllowHidden);
internal sealed record LocalReimportResponse(ReplacementResult? Plan = null, UpdateResult? Preparation = null, string? ReconciliationWarning = null);

/// One selected physical installation; every operation remains owned through
/// its queued completion. Reopening never prepares or approves a new plan.
internal static class LocalReimportScreen
{
    internal static string RenderText(ReplacementResult plan) => UpdateScreen.RenderPlan(plan);

    internal static bool IsLocalReimport(ReplacementResult plan) => plan.LocalImport is not null
        && plan.Revision is null && plan.PinPolicy is null && plan.LocalDisposition is null;

    internal static void Show(IApplication app, Logger logger, InstalledSkill selected,
        Func<LocalReimportRequest, CancellationToken, Task<LocalReimportResponse>> execute,
        CancellationToken lifetimeToken = default, Action<Dialog>? readyForTests = null, Action<Action>? invokeForTests = null)
    {
        if (lifetimeToken.IsCancellationRequested) return;
        using var dialog = new Dialog { Title = " Local reimport ", Width = Dim.Percent(96), Height = Dim.Percent(94), SchemeName = SchemeNames.Dialog };
        using var operation = new ModalOperationTracker(invokeForTests ?? app.Invoke, logger, "reimport.ui");
        var id = new TextField { X = 16, Y = 0, Width = Dim.Fill(), Text = "" };
        var hidden = new CheckBox { X = 0, Y = 1, Text = "Include hidden directories" };
        var output = new Editor
        {
            X = 0,
            Y = 4,
            Width = Dim.Fill(),
            Height = Dim.Fill(6),
            ReadOnly = true,
            WordWrap = true,
            Text = $"Selected installation: {TerminalEscapeSanitizer.Sanitize(selected.ResolvedPath)}\n\nPrepare replaces this installation from its same recorded local source. Review all changed paths and consumers; the exact original is retained. No recorded base or automatic merge exists.\n\nTo reopen retained history, enter a saved plan ID and Inspect. Inspection does not prepare another candidate. Source freshness gates the first retain; later recovery and undo use frozen evidence even if that source disappears."
        };
        var status = new Label { X = 0, Y = 3, Width = Dim.Fill(), Text = "Prepare, or inspect a saved local-reimport ID." };
        var retain = new CheckBox { X = 0, Y = Pos.AnchorEnd(5), Width = Dim.Fill(), Text = "Replace from local source; retain exact original for every consumer", Enabled = false };
        var prepare = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(4), Text = "_Prepare" };
        var inspect = new SingleRowButton { X = Pos.Right(prepare) + 2, Y = Pos.AnchorEnd(4), Text = "_Inspect saved ID" };
        var apply = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(3), Text = "_Apply reviewed", Enabled = false };
        var recover = new SingleRowButton { X = Pos.Right(apply) + 2, Y = Pos.AnchorEnd(3), Text = "_Recover reviewed", Enabled = false };
        var undo = new SingleRowButton { X = Pos.Right(recover) + 2, Y = Pos.AnchorEnd(3), Text = "_Undo reviewed", Enabled = false };
        var close = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "_Close / cancel" };
        ReplacementResult? review = null;
        var inspected = false;
        var closeRequested = false;
        var readyToClose = false;
        var generation = 0L;
        void Refresh(bool terminalCommit = false)
        {
            var idle = (terminalCommit || operation.CurrentOwnership == ModalOperationTracker.Ownership.None) && !lifetimeToken.IsCancellationRequested;
            id.Enabled = hidden.Enabled = prepare.Enabled = inspect.Enabled = idle;
            retain.Enabled = idle && review is not null;
            apply.Enabled = idle && review is { Succeeded: true, Status: "ready" or "planned" }
                && review.LocalSource?.Status is not ("stale" or "unavailable") && retain.Value == CheckState.Checked;
            recover.Enabled = undo.Enabled = idle && inspected && review is not null && retain.Value == CheckState.Checked;
        }
        retain.ValueChanged += (_, _) => Refresh();
        id.TextChanged += (_, _) => { review = null; inspected = false; retain.Value = CheckState.UnChecked; Refresh(); };
        hidden.ValueChanged += (_, _) => { review = null; inspected = false; retain.Value = CheckState.UnChecked; Refresh(); };
        void Start(LocalReimportAction action)
        {
            if (lifetimeToken.IsCancellationRequested || operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            logger.Debug("reimport.ui", $"Starting {action}; ownership={operation.CurrentOwnership}.");
            var captured = review;
            if (action is LocalReimportAction.Apply or LocalReimportAction.Recover or LocalReimportAction.Undo
                && (captured is null || retain.Value != CheckState.Checked || !IsLocalReimport(captured))) return;
            if (action == LocalReimportAction.Inspect && !QuarantineStore.ValidId(id.Text.Trim()))
            { status.Text = "Enter one lowercase 32-character saved plan ID."; return; }
            var request = new LocalReimportRequest(action, action == LocalReimportAction.Inspect ? id.Text.Trim() : captured?.Id,
                captured, hidden.Value == CheckState.Checked);
            // Close all prior approvals before starting private writes or lifecycle work.
            review = null; inspected = false; retain.Value = CheckState.UnChecked;
            var currentGeneration = ++generation;
            status.Text = $"{action} in progress…";
            if (!operation.TryStart(async trackerToken =>
            {
                using var cancellation = new CancellationSource(trackerToken, lifetimeToken,
                    CancellationCallbackReporter.For(logger, "local reimport"));
                var token = cancellation.Token;
                LocalReimportResponse? response = null;
                Exception? failure = null;
                try { response = await execute(request, token).ConfigureAwait(false); }
                catch (Exception exception) { failure = exception; logger.Warn("reimport.ui", exception.ToString()); }
                // Evidence remains discoverable even when cancellation prevents UI delivery.
                if ((response?.Plan ?? response?.Preparation?.SavedPlan) is { } retained)
                    logger.Info("reimport.ui", $"Saved local reimport {retained.Id}; manifest: {retained.PlanPath}; original retention path: {retained.RetainedPath}. {retained.Message}");
                else if (response?.Preparation?.ErrorMessage is { } detail)
                    logger.Warn("reimport.ui", detail);
                operation.InvokeTerminalIfActive(() =>
                {
                    logger.Debug("reimport.ui", $"Terminal {action}; ownership={operation.CurrentOwnership}; parentCanceled={lifetimeToken.IsCancellationRequested}.");
                    // Stable parent/tracker tokens outlive the worker's linked source.
                    if (currentGeneration != generation) { operation.Release(); return; }
                    if (lifetimeToken.IsCancellationRequested)
                    { Finish(); return; }
                    if (operation.Token.IsCancellationRequested)
                    { status.Text = "Canceled; inspect the retained saved ID before resuming."; Finish(); return; }
                    if (failure is not null)
                    {
                        output.Text = TerminalEscapeSanitizer.Sanitize(failure.Message) ?? "Operation failed.";
                        status.Text = "Failed/refused; retained evidence is identified below. Inspect before retrying.";
                    }
                    else if (response is not null)
                    {
                        var result = response.Plan ?? response.Preparation?.SavedPlan;
                        if (result is not null && !IsLocalReimport(result))
                        { output.Text = "Refused: this saved plan is not a local reimport."; }
                        else
                        {
                            id.Text = result?.Id ?? id.Text;
                            // Never reuse post-mutation observations without a new Inspect.
                            review = action is LocalReimportAction.Prepare or LocalReimportAction.Inspect ? result : null;
                            inspected = action == LocalReimportAction.Inspect && review is not null;
                            output.Text = result is not null ? RenderText(result) : response.Preparation is { } prepared
                                ? TerminalEscapeSanitizer.Sanitize(UpdateScreen.RenderResult(prepared, false, false, [selected.Name], localReimport: true)) ?? ""
                                : "No saved evidence returned.";
                            status.Text = result?.LocalSource is { } source ? $"{source.Status}: {TerminalEscapeSanitizer.Sanitize(source.Message)}"
                                : "Review the result. Inspect the saved ID for recovery or undo.";
                        }
                    }
                    if (response?.ReconciliationWarning is { } warning)
                    {
                        output.Text += "\n\nReconciliation partial: " + TerminalEscapeSanitizer.Sanitize(warning);
                        status.Text = "Reconciliation partial; inspect saved evidence and refresh inventory before continuing.";
                        review = null; inspected = false;
                    }
                    Finish();
                });
            })) { status.Text = "Operation admission closed; inspect saved evidence before retrying."; Refresh(); return; }
            Refresh();
        }
        void Finish()
        {
            operation.Release();
            logger.Debug("reimport.ui", $"Completion released; ownership={operation.CurrentOwnership}; closing={closeRequested}.");
            if (closeRequested || lifetimeToken.IsCancellationRequested) { readyToClose = true; app.RequestStop(); return; }
            // A queued callback can beat the worker's final return. Release
            // keeps ownership until that return; render terminal controls now,
            // while Start still refuses admission until the worker has exited.
            Refresh(terminalCommit: true);
        }
        void RequestClose()
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            { closeRequested = true; operation.Cancel(); status.Text = "Canceling; waiting for owned work…"; return; }
            readyToClose = true; app.RequestStop();
        }
        prepare.Accepting += (_, args) => { args.Handled = true; Start(LocalReimportAction.Prepare); };
        inspect.Accepting += (_, args) => { args.Handled = true; Start(LocalReimportAction.Inspect); };
        apply.Accepting += (_, args) => { args.Handled = true; Start(LocalReimportAction.Apply); };
        recover.Accepting += (_, args) => { args.Handled = true; Start(LocalReimportAction.Recover); };
        undo.Accepting += (_, args) => { args.Handled = true; Start(LocalReimportAction.Undo); };
        close.Accepting += (_, args) => { args.Handled = true; RequestClose(); };
        dialog.IsRunningChanging += (_, args) =>
        {
            if (args.NewValue || readyToClose || operation.CurrentOwnership == ModalOperationTracker.Ownership.None) return;
            args.Cancel = true; RequestClose();
        };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; RequestClose(); } };
        dialog.Add(new Label { X = 0, Y = 0, Text = "Saved plan ID:" }, id, hidden, status, output, retain, prepare, inspect, apply, recover, undo, close);
        TuiHelpers.ConfigureTextInput(id, SchemeNames.Dialog);
        TuiHelpers.ConfigureReadOnlyPane(output, SchemeNames.Dialog);
        TuiHelpers.ApplyScheme(SchemeNames.Dialog, dialog, id, hidden, status, output, retain, prepare, inspect, apply, recover, undo, close);
        readyForTests?.Invoke(dialog);
        app.Run(dialog);
    }
}
