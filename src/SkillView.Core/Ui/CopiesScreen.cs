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

internal enum CopiesAction { Update, Cleanup }
internal sealed record CopiesChoice(CopiesAction Action, OperationSubject Subject);

internal static class CopiesScreen
{
    internal static CopiesChoice? Show(IApplication app, Logger logger, CopiesGroup group,
        Func<InstalledSkill, InstalledSkill, CancellationToken, Task<SkillComparisonResult>> compare,
        CancellationToken lifetimeToken, Action<Dialog>? readyForTests = null)
    {
        if (lifetimeToken.IsCancellationRequested || group.Copies.Length == 0) return null;
        using var dialog = new Dialog { Title = "Copies and versions", Width = Dim.Percent(96), Height = Dim.Percent(96), SchemeName = SchemeNames.Dialog };
        var reference = group.Copies[0];
        var referenceLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        var table = new TableView { X = 0, Y = 1, Width = Dim.Fill(), Height = 6, FullRowSelect = true };
        TuiHelpers.ConfigureTableNavigation(table); TuiHelpers.ConfigureTableChrome(table); TuiHelpers.DisableTypeToSearch(table);
        table.Table = new EnumerableTableSource<InstalledSkill>(group.Copies, new Dictionary<string, Func<InstalledSkill, object>>
        {
            ["Location"] = skill => Safe(TuiHelpers.ShortenPath(skill.ResolvedPath, 2), 18),
            ["Scope"] = skill => skill.Scope,
            ["Ref"] = skill => Safe(skill.Revision.RepositoryRef ?? "unknown", 16),
            ["Pin"] = skill => skill.Pinned || skill.Revision.PinnedRef is not null ? "yes" : "—",
            ["Used by"] = skill => Safe(CopiesPresentation.Consumers(skill), 18)
        });
        foreach (var (column, width) in new[] { (0, 16), (1, 7), (2, 14), (3, 3), (4, 16) }) table.Style.GetOrCreateColumnStyle(column).MaxWidth = width;
        var detail = new Editor { X = 0, Y = Pos.Bottom(table), Width = Dim.Fill(), Height = Dim.Fill(4), ReadOnly = true, WordWrap = true };
        var status = new Label { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1, Text = "Select a copy. Compare reads local files; actions open review." };
        var setReference = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(2), Text = "Use as reference" };
        var compareButton = new SingleRowButton { X = Pos.Right(setReference) + 2, Y = Pos.AnchorEnd(2), Text = "Compare" };
        var update = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "Update / version..." };
        var cleanup = new SingleRowButton { X = Pos.Right(update) + 2, Y = Pos.AnchorEnd(1), Text = "Cleanup review..." };
        var close = new SingleRowButton { X = Pos.Right(cleanup) + 2, Y = Pos.AnchorEnd(1), Text = "Close" };
        using var operation = new ModalOperationTracker(app, logger, "copies.compare");
        CopiesChoice? choice = null;
        var closeRequested = false;
        var readyToClose = false;
        InstalledSkill Selected() => group.Copies[Math.Clamp(table.GetSelectedRow(), 0, group.Copies.Length - 1)];
        void Refresh()
        {
            referenceLabel.Text = "Reference: " + Safe(TuiHelpers.ShortenPath(reference.ResolvedPath, 2) + " · " + (reference.Revision.RepositoryRef ?? "ref unknown"), 68);
            detail.Text = CopiesPresentation.Render(group, reference, Selected());
            detail.CaretOffset = 0;
            compareButton.Enabled = Selected().InstallationId != reference.InstallationId;
            status.Text = "Select a copy. Compare reads local files; actions open review.";
        }
        void Finish()
        {
            operation.Release();
            if (closeRequested || lifetimeToken.IsCancellationRequested) { readyToClose = true; app.RequestStop(); return; }
            table.Enabled = setReference.Enabled = update.Enabled = cleanup.Enabled = true;
            compareButton.Enabled = Selected().InstallationId != reference.InstallationId;
        }
        void RequestClose()
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            { closeRequested = true; operation.Cancel(); status.Text = "Canceling comparison…"; return; }
            readyToClose = true; app.RequestStop();
        }
        void Choose(CopiesAction action)
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None || lifetimeToken.IsCancellationRequested) return;
            choice = new(action, OperationSubject.From(Selected()));
            RequestClose();
        }
        table.ValueChanged += (_, _) => Refresh();
        setReference.Accepting += (_, args) =>
        { args.Handled = true; if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return; reference = Selected(); Refresh(); };
        compareButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None || lifetimeToken.IsCancellationRequested) return;
            var left = reference; var right = Selected();
            if (left.InstallationId == right.InstallationId) return;
            table.Enabled = setReference.Enabled = compareButton.Enabled = update.Enabled = cleanup.Enabled = false;
            status.Text = "Comparing local files… Close cancels.";
            operation.TryStart(async token =>
            {
                using var cancellation = new CancellationSource(token, lifetimeToken, CancellationCallbackReporter.For(logger, "copies.compare"));
                var ownedToken = cancellation.Token;
                try
                {
                    var result = await compare(left, right, ownedToken).ConfigureAwait(false);
                    ownedToken.ThrowIfCancellationRequested();
                    var rendered = CopiesPresentation.Render(group, left, right, result);
                    operation.InvokeTerminalIfActive(() =>
                    {
                        if (!token.IsCancellationRequested && !lifetimeToken.IsCancellationRequested
                            && reference.InstallationId == left.InstallationId && Selected().InstallationId == right.InstallationId)
                        {
                            detail.Text = rendered;
                            detail.CaretOffset = 0;
                            status.Text = result.Complete ? "Comparison complete. Review a change for the selected copy, or close." : "Comparison incomplete. Inspect the notices; nothing changed.";
                        }
                        Finish();
                    });
                }
                catch (OperationCanceledException) when (ownedToken.IsCancellationRequested)
                { operation.InvokeTerminalIfActive(() => { if (!lifetimeToken.IsCancellationRequested) status.Text = "Comparison canceled."; Finish(); }); }
                catch (Exception ex)
                {
                    logger.Error("copies.compare", ex.ToString());
                    operation.InvokeTerminalIfActive(() => { if (!lifetimeToken.IsCancellationRequested) status.Text = "Comparison unavailable: " + TuiHelpers.ErrorSnippet(ex.Message); Finish(); });
                }
            });
        };
        update.Accepting += (_, args) => { args.Handled = true; Choose(CopiesAction.Update); };
        cleanup.Accepting += (_, args) => { args.Handled = true; Choose(CopiesAction.Cleanup); };
        close.Accepting += (_, args) => { args.Handled = true; RequestClose(); };
        detail.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Tab) { key.Handled = true; setReference.SetFocus(); }
            else if (key.KeyCode == (KeyCode.Tab | KeyCode.ShiftMask)) { key.Handled = true; table.SetFocus(); }
        };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; RequestClose(); } };
        dialog.IsRunningChanging += (_, args) =>
        {
            if (args.NewValue || readyToClose || operation.CurrentOwnership == ModalOperationTracker.Ownership.None) return;
            args.Cancel = true; RequestClose();
        };
        dialog.Add(referenceLabel, table, detail, status, setReference, compareButton, update, cleanup, close);
        TuiHelpers.ApplyScheme(SchemeNames.Dialog, dialog, referenceLabel, table, detail, status, setReference, compareButton, update, cleanup, close);
        TuiHelpers.ConfigureReadOnlyPane(detail, SchemeNames.Dialog);
        table.SetSelectedRow(0); Refresh(); table.SetFocus();
        using var lifetimeRegistration = lifetimeToken.Register(() => app.Invoke(() => { if (dialog.IsRunning) RequestClose(); }));
        readyForTests?.Invoke(dialog);
        if (!lifetimeToken.IsCancellationRequested) app.Run(dialog);
        return lifetimeToken.IsCancellationRequested ? null : choice;
    }

    private static string Safe(string value, int width) => TuiHelpers.Truncate((TerminalEscapeSanitizer.Sanitize(value) ?? "")
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal), width);
}
