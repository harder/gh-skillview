using SkillView.Cli;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.PackageChecking;
using SkillView.Threading;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

internal static class PackageCheckScreen
{
    internal static void Show(IApplication app, Logger logger, InstalledSkill selected,
        Func<CancellationToken, Task<PackageCheckReport>> capture, CancellationToken lifetimeToken = default,
        Action<Dialog>? readyForTests = null, Action<Action>? dispatchForTests = null)
        => ShowBound(app, logger, selected, async token => new(await capture(token).ConfigureAwait(false), null),
            lifetimeToken, readyForTests, dispatchForTests);

    internal static void ShowBound(IApplication app, Logger logger, InstalledSkill selected,
        Func<CancellationToken, Task<PackageCheckResult>> capture, CancellationToken lifetimeToken = default,
        Action<Dialog>? readyForTests = null, Action<Action>? dispatchForTests = null,
        Func<PackageCheckResult, bool>? publish = null)
    {
        if (lifetimeToken.IsCancellationRequested) return;
        using var dialog = new Dialog { Title = " Static package check ", Width = Dim.Percent(96), Height = Dim.Percent(92), SchemeName = SchemeNames.Dialog };
        using var operation = dispatchForTests is null ? new ModalOperationTracker(app, logger, "package.check")
            : new ModalOperationTracker(dispatchForTests, logger, "package.check");
        var status = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "Checking captured local package…" };
        var output = new Editor
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            WordWrap = true,
            Text = "Reading bounded package files. No skill scripts or agent clients are executed."
        };
        var check = new SingleRowButton { Text = "_Check again", X = 0, Y = Pos.AnchorEnd(1), Enabled = false };
        var close = new SingleRowButton { Text = "_Close", X = Pos.Right(check) + 2, Y = Pos.AnchorEnd(1) };
        var closeRequested = false;
        long epoch = 0;
        var readyToClose = false;
        void Finish()
        {
            operation.Release();
            if (closeRequested || lifetimeToken.IsCancellationRequested) { readyToClose = true; app.RequestStop(); }
            else check.Enabled = true;
        }
        void Start()
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None || lifetimeToken.IsCancellationRequested) return;
            var request = ++epoch;
            check.Enabled = false;
            status.Text = "Checking local files for " + InstalledOverview.Text(selected.Name, 64) + "…";
            operation.TryStart(async token =>
            {
                using var cancellation = new CancellationSource(token, lifetimeToken, CancellationCallbackReporter.For(logger, "package.check"));
                var ownedToken = cancellation.Token;
                try
                {
                    var captured = await capture(ownedToken).ConfigureAwait(false);
                    var result = captured.Report;
                    ownedToken.ThrowIfCancellationRequested();
                    var rendered = RenderReport(result);
                    operation.InvokeTerminalIfActive(() =>
                    {
                        if (lifetimeToken.IsCancellationRequested) { Finish(); return; }
                        // The linked worker source may already be disposed while
                        // this commit remains queued. Its parents are stable
                        // token values and still reject late cancellation.
                        if (!closeRequested && request == epoch && !ownedToken.IsCancellationRequested && !token.IsCancellationRequested && !lifetimeToken.IsCancellationRequested)
                        {
                            var attached = publish is null || publish(captured);
                            output.Text = rendered + (attached ? "" : "\nCheck retained here only. The copy or displayed inventory could not be bound; Refresh Installed before checking again.\n");
                            status.Text = (attached ? "" : "Refresh Installed · ") + $"Format: {result.Format.State} · agent: {result.AgentAssessment.State} · intact: {result.PayloadIntact.State} · source: {result.SourceReviewed.State}";
                        }
                        else if (!lifetimeToken.IsCancellationRequested) status.Text = "Check canceled.";
                        Finish();
                    });
                }
                catch (OperationCanceledException) when (ownedToken.IsCancellationRequested)
                {
                    operation.InvokeTerminalIfActive(() =>
                    {
                        if (!lifetimeToken.IsCancellationRequested) status.Text = "Check canceled.";
                        Finish();
                    });
                }
                catch (Exception exception)
                {
                    logger.Error("package.check", exception.ToString());
                    operation.InvokeTerminalIfActive(() =>
                    {
                        if (!lifetimeToken.IsCancellationRequested)
                            status.Text = InstalledOverview.Text("Check unavailable: " + exception.Message, 240);
                        Finish();
                    });
                }
            });
        }
        void RequestClose()
        {
            ++epoch;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            { closeRequested = true; operation.Cancel(); status.Text = "Canceling check…"; return; }
            readyToClose = true; app.RequestStop();
        }
        check.Accepting += (_, args) => { args.Handled = true; Start(); };
        close.Accepting += (_, args) => { args.Handled = true; RequestClose(); };
        dialog.IsRunningChanging += (_, args) =>
        {
            if (args.NewValue || readyToClose || operation.CurrentOwnership == ModalOperationTracker.Ownership.None) return;
            args.Cancel = true; ++epoch; closeRequested = true; operation.Cancel(); status.Text = "Canceling check…";
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc) { key.Handled = true; RequestClose(); }
        };
        dialog.Add(status, output, check, close);
        TuiHelpers.ApplyScheme(SchemeNames.Dialog, dialog, status, output, check, close);
        TuiHelpers.ConfigureReadOnlyPane(output, SchemeNames.Dialog);
        readyForTests?.Invoke(dialog);
        Start();
        app.Run(dialog);
    }

    internal static void ShowRetained(IApplication app, PackageCheckFact fact, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        using var dialog = new Dialog { Title = " Retained package check ", Width = Dim.Percent(96), Height = Dim.Percent(92) };
        var output = new Editor
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            WordWrap = true,
            Text = fact.Detail + "\n\n" + RenderReport(fact.Report)
        };
        var close = new SingleRowButton { Text = "_Close", X = 0, Y = Pos.AnchorEnd(1) };
        close.Accepting += (_, e) => { e.Handled = true; app.RequestStop(); };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; app.RequestStop(); } };
        dialog.Add(output, close);
        TuiHelpers.ApplyScheme(SchemeNames.Dialog, dialog, output, close);
        TuiHelpers.ConfigureReadOnlyPane(output, SchemeNames.Dialog);
        app.Run(dialog);
    }

    private static string RenderReport(PackageCheckReport report)
    {
        const int limit = 256 * 1024;
        var text = CliDispatcher.RenderPackageCheckText(report);
        if (text.Length <= limit) return text;
        var end = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..end] + $"\nReport display limit: {text.Length - end} characters omitted. Retained totals: {report.FindingCount} findings, {report.ObservationCount} observations; check limits omitted {report.OmittedFindingCount} findings and {report.OmittedObservationCount} observations.\n";
    }

}
