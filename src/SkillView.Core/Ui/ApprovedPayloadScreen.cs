using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Threading;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

internal static class ApprovedPayloadScreen
{
    internal static ReplacementResult? Show(IApplication app, Logger logger, InstalledSkill selected,
        Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>> execute, CancellationToken lifetimeToken,
        Action<Dialog>? readyForTests = null, Action<Action>? invokeForTests = null)
    {
        if (lifetimeToken.IsCancellationRequested) return null;
        using var dialog = new Dialog { Title = " Immutable installed approval ", Width = Dim.Percent(96), Height = Dim.Percent(94), SchemeName = SchemeNames.Dialog };
        using var operation = new ModalOperationTracker(invokeForTests ?? app.Invoke, logger, "approval.ui");
        var id = new TextField { X = 22, Y = 0, Width = Dim.Fill() };
        var fingerprint = new TextField { X = 22, Y = 1, Width = Dim.Fill() };
        var hidden = new CheckBox { X = 0, Y = 2, Text = "Include hidden directories" };
        var status = new Label { X = 0, Y = 3, Width = Dim.Fill(), Text = "Preview the exact installed bytes; capture never sets desired intent." };
        var output = new Editor
        {
            X = 0,
            Y = 4,
            Width = Dim.Fill(),
            Height = Dim.Fill(8),
            ReadOnly = true,
            WordWrap = true,
            Text = $"Selected: {TerminalEscapeSanitizer.Sanitize(selected.InstallationId)}\n{TerminalEscapeSanitizer.Sanitize(selected.ResolvedPath)}\n\nCapture retains actual installed/custom bytes and supported metadata. It does not prove pristine upstream provenance, source safety or an active session. Existing-target restore only; no new roots or links."
        };
        var captureConsent = new CheckBox { X = 0, Y = Pos.AnchorEnd(8), Text = "I approve retaining the previewed installed bytes privately" };
        var policyConsent = new CheckBox { X = 0, Y = Pos.AnchorEnd(7), Text = "I separately approve this captured archive as desired immutable intent" };
        var preview = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(6), Text = "Preview installed" };
        var capture = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(5), Text = "Capture private archive" };
        var inspect = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(4), Text = "Inspect approval ID/fingerprint" };
        var policy = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(3), Text = "Set desired immutable intent" };
        var restore = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(2), Text = "Stage existing-target restore; review source/retention next" };
        var close = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "Close / cancel" };
        ApprovalResponse? review = null;
        ReplacementResult? nextPlan = null;
        var closing = false; var stopped = false; long generation = 0;
        void Reset()
        {
            review = null; captureConsent.Value = policyConsent.Value = CheckState.UnChecked; ++generation; Refresh();
        }
        void Refresh(bool terminal = false)
        {
            var idle = (terminal || operation.CurrentOwnership == ModalOperationTracker.Ownership.None) && !lifetimeToken.IsCancellationRequested;
            id.Enabled = fingerprint.Enabled = hidden.Enabled = preview.Enabled = inspect.Enabled = idle;
            captureConsent.Enabled = idle && review is { Reference: null };
            policyConsent.Enabled = idle && review?.Reference is not null;
            capture.Enabled = captureConsent.Enabled && captureConsent.Value == CheckState.Checked;
            policy.Enabled = policyConsent.Enabled && policyConsent.Value == CheckState.Checked;
            restore.Enabled = idle && review?.Reference is not null;
        }
        id.TextChanged += (_, _) => Reset(); fingerprint.TextChanged += (_, _) => Reset(); hidden.ValueChanged += (_, _) => Reset();
        captureConsent.ValueChanged += (_, _) => Refresh(); policyConsent.ValueChanged += (_, _) => Refresh();
        void Finish()
        {
            operation.Release();
            if (closing || lifetimeToken.IsCancellationRequested) { stopped = true; app.RequestStop(); }
            else Refresh(terminal: true);
        }
        void Start(ApprovalAction action)
        {
            if (lifetimeToken.IsCancellationRequested || operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            var captured = review;
            var consent = action == ApprovalAction.Capture ? captureConsent.Value == CheckState.Checked : action == ApprovalAction.SetPolicy && policyConsent.Value == CheckState.Checked;
            if (action == ApprovalAction.Capture && (!consent || captured is not { Reference: null })
                || action == ApprovalAction.SetPolicy && (!consent || captured?.Reference is null)
                || action == ApprovalAction.Plan && captured?.Reference is null) return;
            var request = new ApprovalRequest(action, id.Text.Trim(), fingerprint.Text.Trim(), captured, hidden.Value == CheckState.Checked, consent);
            Reset(); var requestGeneration = generation;
            status.Text = $"{action} in progress…";
            if (!operation.TryStart(async trackerToken =>
            {
                using var cancellation = new CancellationSource(trackerToken, lifetimeToken, CancellationCallbackReporter.For(logger, "immutable approval"));
                ApprovalResponse? result = null; Exception? failure = null;
                try { result = await execute(request, cancellation.Token).ConfigureAwait(false); }
                catch (Exception ex) { failure = ex; logger.Warn("approval.ui", ex.ToString()); }
                if (result?.Reference is { } retained) logger.Info("approval.ui", $"Retained approval {retained.Id} {retained.Fingerprint} at {retained.StoreAddress}.");
                if (result?.Plan is { } plan) logger.Info("approval.ui", $"Saved approved restore {plan.Id} {plan.PlanFingerprint} at {plan.PlanPath}.");
                operation.InvokeTerminalIfActive(() =>
                {
                    if (requestGeneration != generation) { Finish(); return; }
                    if (lifetimeToken.IsCancellationRequested || operation.Token.IsCancellationRequested)
                    { status.Text = "Canceled; inspect retained evidence before retrying."; Finish(); return; }
                    if (failure is not null)
                    {
                        output.Text = Bound(failure.Message); status.Text = "Refused; any partial private attempt remains retained.";
                    }
                    else if (result is not null)
                    {
                        id.Text = result.Reference?.Id ?? "";
                        fingerprint.Text = result.Reference?.Fingerprint ?? "";
                        // Text edits above invalidate old approvals; assign only
                        // the exact newly completed response, with fresh consents.
                        review = result;
                        output.Text = Render(result, selected);
                        status.Text = "Review this exact result. Capture, desired intent, source and retention are separate consents.";
                        if (result.Plan is { } staged) { nextPlan = staged; closing = true; }
                    }
                    Finish();
                });
            })) { status.Text = "Operation admission closed."; Refresh(); return; }
            Refresh();
        }
        void RequestClose()
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            { closing = true; operation.Cancel(); status.Text = "Canceling; waiting for owned work…"; return; }
            stopped = true; app.RequestStop();
        }
        preview.Accepting += (_, e) => { e.Handled = true; Start(ApprovalAction.Preview); };
        capture.Accepting += (_, e) => { e.Handled = true; Start(ApprovalAction.Capture); };
        inspect.Accepting += (_, e) => { e.Handled = true; Start(ApprovalAction.Inspect); };
        policy.Accepting += (_, e) => { e.Handled = true; Start(ApprovalAction.SetPolicy); };
        restore.Accepting += (_, e) => { e.Handled = true; Start(ApprovalAction.Plan); };
        close.Accepting += (_, e) => { e.Handled = true; RequestClose(); };
        dialog.IsRunningChanging += (_, e) => { if (!e.NewValue && !stopped && operation.CurrentOwnership != ModalOperationTracker.Ownership.None) { e.Cancel = true; RequestClose(); } };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; RequestClose(); } };
        dialog.Add(new Label { X = 0, Y = 0, Text = "Approval ID:" }, id, new Label { X = 0, Y = 1, Text = "Approval fingerprint:" }, fingerprint,
            hidden, status, output, captureConsent, policyConsent, preview, capture, inspect, policy, restore, close);
        TuiHelpers.ConfigureTextInput(id, SchemeNames.Dialog); TuiHelpers.ConfigureTextInput(fingerprint, SchemeNames.Dialog);
        TuiHelpers.ConfigureReadOnlyPane(output, SchemeNames.Dialog);
        Refresh(); readyForTests?.Invoke(dialog); app.Run(dialog);
        return lifetimeToken.IsCancellationRequested ? null : nextPlan;
    }

    private static string Render(ApprovalResponse result, InstalledSkill selected) => Bound($"{selected.InstallationId}\n{selected.ResolvedPath}\nPayload: {result.Payload}\nDesired policy: {result.Policy.Policy?.Mode ?? result.Policy.Status}\nPolicy fingerprint: {result.Policy.Fingerprint}\nApproval: {result.Reference?.Id ?? "none"} {result.Reference?.Fingerprint}\nKind: {result.Header?.Kind ?? "preview-only"}\n\nCaptured installed/custom bytes are not proved pristine upstream. Private capture is independent of desired intent and source/retention approval. Running session state remains unknown.");
    private static string Bound(string text) => TerminalEscapeSanitizer.Sanitize(text.Length > 8192 ? text[..8192] + "\n[view truncated; full evidence retained]" : text) ?? "";
}
