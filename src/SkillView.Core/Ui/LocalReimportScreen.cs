using System.Collections.Immutable;
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
internal sealed record LocalReimportRequest(LocalReimportAction Action, string? Id, ReplacementResult? Review, bool AllowHidden, string? NewSourcePath = null);
internal sealed record LocalReimportResponse(ReplacementResult? Plan = null, UpdateResult? Preparation = null, string? ReconciliationWarning = null);

/// One selected physical installation; every operation remains owned through
/// its queued completion. Reopening never prepares or approves a new plan.
internal static class LocalReimportScreen
{
    private const int DiagnosticLimit = 8192;

    private static string DisplayDiagnostic(string? text)
    {
        var bounded = text is { Length: > DiagnosticLimit }
            ? text[..DiagnosticLimit] + "\n[Additional diagnostic text omitted from this view.]" : text;
        return TerminalEscapeSanitizer.Sanitize(bounded) ?? "";
    }

    internal static string RenderText(ReplacementResult plan) =>
        $"### Operation outcome: {(plan.Canceled ? "Canceled" : plan.Succeeded ? "Succeeded" : "Failed/refused")}\n\n"
        + MarkdownTableFormatter.FormatCodeSpan(DisplayDiagnostic(plan.Message)) + "\n\n" + UpdateScreen.RenderPlan(plan);

    private static string RenderPreparation(UpdateResult prepared, string selectedName, bool sourceSwitch, bool registryRestore = false)
    {
        var bounded = prepared with
        {
            ErrorMessage = DisplayDiagnostic(prepared.ErrorMessage),
            StdOut = DisplayDiagnostic(prepared.StdOut),
            StdErr = DisplayDiagnostic(prepared.StdErr),
            Notices = prepared.Notices.Select(notice => notice with { Text = DisplayDiagnostic(notice.Text) }).ToImmutableArray(),
            Outcomes = prepared.Outcomes.Select(outcome => outcome with { Message = DisplayDiagnostic(outcome.Message) }).ToImmutableArray()
        };
        var message = prepared.SavedPlan is { } saved
            ? "\n\nSaved operation message: " + MarkdownTableFormatter.FormatCodeSpan(DisplayDiagnostic(saved.Message)) : "";
        return TerminalEscapeSanitizer.Sanitize(UpdateScreen.RenderResult(bounded, selectedName, localReimport: !sourceSwitch && !registryRestore, localSourceSwitch: sourceSwitch, registryRestore: registryRestore) + message) ?? "";
    }

    internal static bool IsLocalReimport(ReplacementResult plan) => plan.LocalSourceSwitch is null && plan.LocalImport is not null && plan.LocalImport.Disposition == ReplacementPlanStore.LocalReimportDisposition
        && plan.Revision is null && plan.PinPolicy is null && plan.LocalDisposition is null;

    internal static bool IsLocalSourceSwitch(ReplacementResult plan) => plan.LocalSourceSwitch is not null && plan.LocalImport is not null
        && plan.LocalImport.Disposition == ReplacementPlanStore.LocalSourceSwitchDisposition
        && plan.Revision is null && plan.PinPolicy is null && plan.LocalDisposition is null;

    internal static void Show(IApplication app, Logger logger, InstalledSkill selected,
        Func<LocalReimportRequest, CancellationToken, Task<LocalReimportResponse>> execute,
        CancellationToken lifetimeToken = default, Action<Dialog>? readyForTests = null, Action<Action>? invokeForTests = null, bool sourceSwitch = false, bool registryRestore = false)
    {
        if (lifetimeToken.IsCancellationRequested) return;
        var family = registryRestore ? "registry-recorded restore" : sourceSwitch ? "local source switch" : "local reimport";
        bool CorrectFamily(ReplacementResult plan) => registryRestore ? plan.RegistryRestore is not null && plan.Purpose == ReplacementPlanStore.RegistryRestorePurpose : sourceSwitch ? IsLocalSourceSwitch(plan) : IsLocalReimport(plan);
        using var dialog = new Dialog { Title = registryRestore ? " Registry-recorded restore " : sourceSwitch ? " Local source switch " : " Local reimport ", Width = Dim.Percent(96), Height = Dim.Percent(94), SchemeName = SchemeNames.Dialog };
        var id = new TextField { X = 16, Y = 0, Width = Dim.Fill(), Text = "" };
        using var newSource = new TextField { X = 16, Y = 1, Width = Dim.Fill(), Text = "" };
        using var operation = new ModalOperationTracker(invokeForTests ?? app.Invoke, logger, "reimport.ui");
        var rowOffset = sourceSwitch ? 1 : 0;
        var hidden = new CheckBox { X = 0, Y = 1 + rowOffset, Text = "Include hidden directories" };
        var output = new Editor
        {
            X = 0,
            Y = 4 + rowOffset,
            Width = Dim.Fill(),
            Height = Dim.Fill(7),
            ReadOnly = true,
            WordWrap = true,
            Text = registryRestore ? $"Selected installation: {TerminalEscapeSanitizer.Sanitize(selected.ResolvedPath)}\n\n{TerminalEscapeSanitizer.Sanitize(ReplacementPlanStore.RegistryRestoreNotice(selected.Name, false))}\n\nPrepare saves one exact candidate. Inspect a saved ID to recover or Undo; source and retention approvals are independent." : sourceSwitch ? $"Selected installation: {TerminalEscapeSanitizer.Sanitize(selected.ResolvedPath)}\n\nChoose a different existing durable ordinary local source. Baseline-less replacement retains the exact installed original for every consumer. The old source is placement-only and may be missing; it is never a historical base. Git-managed sources remain unsupported.\n\nInspect a saved ID to recover or undo from frozen evidence after the first retain. No automatic merge or fork ancestry is claimed." : $"Selected installation: {TerminalEscapeSanitizer.Sanitize(selected.ResolvedPath)}\n\nPrepare replaces this installation from its same recorded local source. Review all changed paths and consumers; the exact original is retained. No recorded base or automatic merge exists.\n\nTo reopen retained history, enter a saved plan ID and Inspect. Inspection does not prepare another candidate. Source freshness gates the first retain; later recovery and undo use frozen evidence even if that source disappears."
        };
        var status = new Label { X = 0, Y = 3 + rowOffset, Width = Dim.Fill(), Text = $"Prepare, or inspect a saved {family} ID." };
        var sourceReview = new CheckBox { X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Text = "I reviewed frozen source changes/omissions; no authenticity or safety claim", Enabled = false };
        var retain = new CheckBox { X = 0, Y = Pos.AnchorEnd(5), Width = Dim.Fill(), Text = registryRestore ? "Replace from registry-recorded tree; retain raw damaged original for every consumer" : "Replace from local source; retain exact original for every consumer", Enabled = false };
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
            id.Enabled = hidden.Enabled = newSource.Enabled = prepare.Enabled = inspect.Enabled = idle;
            retain.Enabled = idle && review is not null;
            sourceReview.Enabled = idle && review?.SourceReview is not null;
            apply.Enabled = idle && review is { Succeeded: true, Status: "ready" or "planned" }
                && review.LocalSource?.Status is not ("stale" or "unavailable") && retain.Value == CheckState.Checked
                && (review.SourceReview is null || sourceReview.Value == CheckState.Checked);
            recover.Enabled = undo.Enabled = idle && inspected && review is not null && retain.Value == CheckState.Checked;
            recover.Enabled &= review?.SourceReview is null || review.SourceReviewAcknowledged || sourceReview.Value == CheckState.Checked;
        }
        sourceReview.ValueChanged += (_, _) => Refresh();
        retain.ValueChanged += (_, _) => Refresh();
        id.TextChanged += (_, _) => { review = null; inspected = false; retain.Value = CheckState.UnChecked; sourceReview.Value = CheckState.UnChecked; Refresh(); };
        newSource.TextChanged += (_, _) => { review = null; inspected = false; retain.Value = CheckState.UnChecked; sourceReview.Value = CheckState.UnChecked; Refresh(); };
        hidden.ValueChanged += (_, _) => { review = null; inspected = false; retain.Value = CheckState.UnChecked; sourceReview.Value = CheckState.UnChecked; Refresh(); };
        void Start(LocalReimportAction action)
        {
            if (lifetimeToken.IsCancellationRequested || operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            logger.Debug("reimport.ui", $"Starting {action}; ownership={operation.CurrentOwnership}.");
            var captured = review;
            if (action is LocalReimportAction.Apply or LocalReimportAction.Recover or LocalReimportAction.Undo
                && (captured is null || retain.Value != CheckState.Checked || !CorrectFamily(captured))) return;
            if (action == LocalReimportAction.Inspect && !QuarantineStore.ValidId(id.Text.Trim()))
            { status.Text = "Enter one lowercase 32-character saved plan ID."; return; }
            if (sourceSwitch && action == LocalReimportAction.Prepare
                && (!Path.IsPathFullyQualified(newSource.Text) || newSource.Text.Any(char.IsControl)))
            { status.Text = "Enter an absolute existing ordinary new source directory."; return; }
            if (captured?.SourceReview is not null && action is LocalReimportAction.Apply or LocalReimportAction.Recover)
            {
                if (!captured.SourceReviewAcknowledged && sourceReview.Value != CheckState.Checked) return;
                captured = captured with { AcknowledgeSourceReview = sourceReview.Value == CheckState.Checked };
            }
            var request = new LocalReimportRequest(action, action == LocalReimportAction.Inspect ? id.Text.Trim() : captured?.Id,
                captured, hidden.Value == CheckState.Checked, sourceSwitch ? newSource.Text : null);
            // Close all prior approvals before starting private writes or lifecycle work.
            review = null; inspected = false; retain.Value = CheckState.UnChecked; sourceReview.Value = CheckState.UnChecked;
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
                if (response?.Preparation?.ErrorMessage is { } detail)
                    logger.Warn("reimport.ui", detail);
                if (response?.Preparation is { } diagnostics)
                {
                    if (diagnostics.StdOut.Length > 0) logger.Info("reimport.ui", diagnostics.StdOut);
                    if (diagnostics.StdErr.Length > 0) logger.Warn("reimport.ui", diagnostics.StdErr);
                    foreach (var notice in diagnostics.Notices) logger.Warn("reimport.ui", notice.Stream + ": " + notice.Text);
                }
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
                        output.Text = DisplayDiagnostic(failure.Message);
                        status.Text = "Failed/refused; retained evidence is identified below. Inspect before retrying.";
                    }
                    else if (response is not null)
                    {
                        var result = response.Plan ?? response.Preparation?.SavedPlan;
                        if (result is not null && !CorrectFamily(result))
                        {
                            output.Text = $"Refused: this saved plan is not a {family}.";
                            status.Text = "Failed/refused; inspect the correct saved purpose before continuing.";
                        }
                        else
                        {
                            id.Text = result?.Id ?? id.Text;
                            var canceled = response.Preparation?.Cancelled == true || result?.Canceled == true;
                            var failed = response.Preparation is { Succeeded: false } or { ReconciliationPartial: true } || result is { Succeeded: false };
                            // Never reuse post-mutation observations without a new Inspect.
                            review = !canceled && !failed && (action is LocalReimportAction.Prepare or LocalReimportAction.Inspect) ? result : null;
                            inspected = action == LocalReimportAction.Inspect && review is not null;
                            output.Text = response.Preparation is { } prepared ? RenderPreparation(prepared, selected.Name, sourceSwitch, registryRestore)
                                : result is not null ? RenderText(result) : "No saved evidence returned.";
                            status.Text = canceled ? "Canceled; inspect the retained saved ID before resuming."
                                : failed ? "Failed/refused; inspect saved evidence before recovery or retry."
                                : result?.LocalSource is { } source ? $"{source.Status}: {DisplayDiagnostic(source.Message)}"
                                : "Review the result. Inspect the saved ID for recovery or undo.";
                        }
                    }
                    if (response?.ReconciliationWarning is { } warning)
                    {
                        output.Text += "\n\nReconciliation partial: " + DisplayDiagnostic(warning);
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
        dialog.Add(new Label { X = 0, Y = 0, Text = "Saved plan ID:" }, id, hidden, status, output, sourceReview, retain, prepare, inspect, apply, recover, undo, close);
        if (sourceSwitch)
        {
            dialog.Add(new Label { X = 0, Y = 1, Text = "New source:" }, newSource);
            TuiHelpers.ConfigureTextInput(newSource, SchemeNames.Dialog);
        }
        TuiHelpers.ConfigureTextInput(id, SchemeNames.Dialog);
        TuiHelpers.ConfigureReadOnlyPane(output, SchemeNames.Dialog);
        // This review pane has no indentation workflow. Let the application's
        // normal focus traversal handle Tab, including reverse/wrapped traversal
        // and disabled controls, without changing either approval value.
        output.KeyBindings.Remove(KeyCode.Tab);
        output.KeyBindings.Remove(KeyCode.Tab | KeyCode.ShiftMask);
        TuiHelpers.ApplyScheme(SchemeNames.Dialog, dialog, id, hidden, status, output, sourceReview, retain, prepare, inspect, apply, recover, undo, close);
        readyForTests?.Invoke(dialog);
        app.Run(dialog);
    }
}
