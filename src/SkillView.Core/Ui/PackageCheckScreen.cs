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
            check.Enabled = false;
            status.Text = "Checking local files for " + TerminalEscapeSanitizer.Sanitize(selected.Name) + "…";
            operation.TryStart(async token =>
            {
                using var cancellation = new CancellationSource(token, lifetimeToken, CancellationCallbackReporter.For(logger, "package.check"));
                var ownedToken = cancellation.Token;
                try
                {
                    var result = await capture(ownedToken).ConfigureAwait(false);
                    ownedToken.ThrowIfCancellationRequested();
                    var rendered = CliDispatcher.RenderPackageCheckText(result);
                    operation.InvokeTerminalIfActive(() =>
                    {
                        if (lifetimeToken.IsCancellationRequested) { Finish(); return; }
                        // The linked worker source may already be disposed while
                        // this commit remains queued. Its parents are stable
                        // token values and still reject late cancellation.
                        if (!ownedToken.IsCancellationRequested && !token.IsCancellationRequested && !lifetimeToken.IsCancellationRequested)
                        {
                            output.Text = rendered;
                            status.Text = $"Metadata: {result.FormatStatus} · references: {result.ReferenceStatus} · payload coverage: {result.Coverage.Payload}";
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
                            status.Text = TerminalEscapeSanitizer.Sanitize("Check unavailable: " + exception.Message) ?? "Check unavailable.";
                        Finish();
                    });
                }
            });
        }
        void RequestClose()
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            { closeRequested = true; operation.Cancel(); status.Text = "Canceling check…"; return; }
            readyToClose = true; app.RequestStop();
        }
        check.Accepting += (_, args) => { args.Handled = true; Start(); };
        close.Accepting += (_, args) => { args.Handled = true; RequestClose(); };
        dialog.IsRunningChanging += (_, args) =>
        {
            if (args.NewValue || readyToClose || operation.CurrentOwnership == ModalOperationTracker.Ownership.None) return;
            args.Cancel = true; closeRequested = true; operation.Cancel(); status.Text = "Canceling check…";
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
}
