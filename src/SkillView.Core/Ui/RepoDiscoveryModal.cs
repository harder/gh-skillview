using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Threading;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

// The A shortcut requests a subset. Listing failure keeps that intent until an
// explicit retry succeeds; no failure or cancellation can select install-all.
internal static class RepoDiscoveryModal
{
    internal static RepoSkillListing? Show(IApplication app, Logger logger, string repo,
        Func<CancellationToken, Task<RepoSkillListing>> list, Func<bool> isCurrent,
        CancellationToken lifetimeToken = default,
        Action<Dialog, ModalOperationTracker>? readyForTests = null, Action<Action>? dispatchForTests = null)
    {
        if (lifetimeToken.IsCancellationRequested || !isCurrent()) return null;
        using var dialog = new Dialog
        {
            Title = " Choose repository skills ",
            Width = Dim.Percent(94),
            Height = Dim.Percent(90),
        };
        using var operation = new ModalOperationTracker(dispatchForTests ?? app.Invoke, logger, "install.discovery");
        RepoSkillListing? result = null;
        var closed = false;
        var status = new Label { X = 1, Y = 0, Width = Dim.Fill(1), Height = 1 };
        var output = InstallFailureDetails.CreatePane(string.Empty);
        output.Y = 2;
        TuiHelpers.ConfigureReadOnlyPane(output, SkillViewStyling.DialogSchemeName);
        var retry = new SingleRowButton { X = 1, Y = Pos.AnchorEnd(1), Text = "Retry", Enabled = false };
        var cancel = new SingleRowButton { X = Pos.Right(retry) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel", IsDefault = true };
        bool Current() => !closed && !lifetimeToken.IsCancellationRequested && !operation.Token.IsCancellationRequested && isCurrent();
        void Close()
        {
            closed = true;
            result = null;
            operation.Cancel();
            if (dialog.IsRunning) app.RequestStop();
        }
        void Start()
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            if (!Current()) { Close(); return; }
            retry.Enabled = false;
            cancel.SetFocus();
            status.Text = "Listing repository skills…";
            output.Text = "Repository: " + InstallFailureDetails.Literal(repo) + "\n\nChoose a subset after listing completes. No skills have been installed.";
            operation.TryStart(async token =>
            {
                using var cancellation = new CancellationSource(token, lifetimeToken, CancellationCallbackReporter.For(logger, "repository listing"));
                var ownedToken = cancellation.Token;
                try
                {
                    var listing = await list(ownedToken).ConfigureAwait(false);
                    ownedToken.ThrowIfCancellationRequested();
                    var detail = "Repository: " + InstallFailureDetails.Literal(repo) + "\n\n"
                        + (listing.Succeeded ? "No skills were discovered." : $"Could not list repository skills (exit {listing.ExitCode}).")
                        + "\nNo skills have been installed. Retry to choose a subset, or Cancel.\n\n"
                        + InstallFailureDetails.Literal(listing.ErrorMessage);
                    operation.InvokeTerminalIfActive(() =>
                    {
                        operation.Release();
                        if (!Current() || ownedToken.IsCancellationRequested) { Close(); return; }
                        if (listing.Succeeded && !listing.Skills.IsDefaultOrEmpty)
                        {
                            result = listing;
                            app.RequestStop();
                            return;
                        }
                        status.Text = listing.Succeeded ? "No repository skills found" : "Repository listing failed";
                        output.Text = detail;
                        output.CaretOffset = 0;
                        retry.Enabled = true;
                        cancel.SetFocus();
                    });
                }
                catch (OperationCanceledException) when (ownedToken.IsCancellationRequested)
                {
                    operation.InvokeTerminalIfActive(() => { operation.Release(); Close(); });
                }
                catch (Exception exception)
                {
                    logger.Error("install.discovery", exception.ToString());
                    var detail = "Repository: " + InstallFailureDetails.Literal(repo)
                        + "\n\nNo skills have been installed. Retry to choose a subset, or Cancel.\n\n"
                        + InstallFailureDetails.Literal(exception.Message);
                    operation.InvokeTerminalIfActive(() =>
                    {
                        operation.Release();
                        if (!Current()) { Close(); return; }
                        status.Text = "Repository listing failed";
                        output.Text = detail;
                        output.CaretOffset = 0;
                        retry.Enabled = true;
                        cancel.SetFocus();
                    });
                }
            });
        }
        retry.Accepting += (_, args) => { args.Handled = true; Start(); };
        cancel.Accepting += (_, args) => { args.Handled = true; Close(); };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Esc) return;
            key.Handled = true; Close();
        };
        dialog.IsRunningChanging += (_, args) =>
        {
            if (!args.NewValue) { closed = true; operation.Cancel(); }
        };
        dialog.Accepting += (_, args) => args.Handled = true;
        dialog.Add(status, output, retry, cancel);
        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, dialog, status, retry, cancel);
        using var registration = lifetimeToken.Register(() => operation.InvokeIfActive(Close));
        Start();
        readyForTests?.Invoke(dialog, operation);
        if (!closed && !lifetimeToken.IsCancellationRequested) app.Run(dialog);
        return lifetimeToken.IsCancellationRequested || !isCurrent() ? null : result;
    }
}
