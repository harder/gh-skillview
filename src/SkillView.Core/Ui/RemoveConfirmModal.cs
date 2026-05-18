using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Compact remove confirm matching winget-tui's "press x, see [y/n]"
/// vocabulary. Use this only when the underlying RemoveTargetEvaluation
/// is simple — see <see cref="CanRunCompact"/>. Anything that requires the
/// second-confirm escalation, has errors, or spans multiple skills (package
/// / repo groups) routes to the existing <see cref="RemoveScreen"/> wizard
/// instead.
internal sealed class RemoveConfirmModal
{
    internal enum Outcome
    {
        Cancelled,
        Removed,
        Failed,
        EscalateToWizard,
    }

    internal sealed record Result(Outcome Outcome, RemoveService.RemoveReport? Report);

    private readonly IApplication _app;
    private readonly RemoveService _remove;
    private readonly Logger _logger;
    private readonly InstalledSkill _skill;
    private readonly RemoveTargetEvaluation _evaluation;

    internal RemoveConfirmModal(
        IApplication app,
        RemoveService remove,
        Logger logger,
        InstalledSkill skill,
        RemoveTargetEvaluation evaluation)
    {
        _app = app;
        _remove = remove;
        _logger = logger;
        _skill = skill;
        _evaluation = evaluation;
    }

    /// True iff the evaluation represents a single skill, with no validation
    /// errors, and no second-confirm flag. The caller should only construct
    /// this modal when this returns true; otherwise route to RemoveScreen.
    internal static bool CanRunCompact(RemoveTargetEvaluation evaluation)
    {
        if (evaluation.Target.Kind != RemoveTargetKind.CurrentInstall) return false;
        if (evaluation.Items.Length != 1) return false;
        if (evaluation.RequiresSecondConfirm) return false;
        if (!evaluation.CanExecute) return false;
        if (evaluation.Errors.Length > 0) return false;
        return true;
    }

    internal Result Show()
    {
        var validation = _evaluation.Items[0].Validation;
        var outcome = Outcome.Cancelled;
        RemoveService.RemoveReport? report = null;

        var dialog = new Dialog
        {
            Title = " Remove skill ",
            Width = Dim.Percent(50),
            Height = 12,
        };
        dialog.SchemeName = SchemeNames.Dialog;

        var prompt = new Label
        {
            X = 1, Y = 0,
            Width = Dim.Fill(2),
            Text = $"Remove {_skill.Name}?",
        };
        var path = new Label
        {
            X = 1, Y = 1,
            Width = Dim.Fill(2),
            Text = $"  {TuiHelpers.ShortenPath(_skill.ResolvedPath, segments: 4)}",
        };
        var warningsText = _evaluation.Warnings.Length > 0
            ? "  ⚠ " + string.Join("; ", _evaluation.Warnings.Select(w => w.Detail))
            : string.Empty;
        var warnings = new Label
        {
            X = 1, Y = 3,
            Width = Dim.Fill(2),
            Text = warningsText,
            Visible = warningsText.Length > 0,
        };

        var status = new Label
        {
            X = 1, Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(2),
            Text = " [y] yes   [n] no   [a] advanced…",
        };

        var yesButton = new Button
        {
            X = Pos.Center() - 16, Y = Pos.AnchorEnd(1),
            Text = "Yes",
            IsDefault = false,
        };
        var noButton = new Button
        {
            X = Pos.Center() - 6, Y = Pos.AnchorEnd(1),
            Text = "No",
            IsDefault = true,
        };
        var advancedButton = new Button
        {
            X = Pos.Center() + 4, Y = Pos.AnchorEnd(1),
            Text = "Advanced…",
        };

        yesButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            try
            {
                report = _remove.Remove(validation, new RemoveService.Options(DryRun: false));
                outcome = report.Succeeded ? Outcome.Removed : Outcome.Failed;
                if (!report.Succeeded)
                {
                    status.Text = $" remove failed: {report.Errors.FirstOrDefault() ?? "(no detail)"}";
                    return;
                }
                _app.RequestStop();
            }
            catch (Exception ex)
            {
                _logger.Error("remove.compact", ex.Message);
                outcome = Outcome.Failed;
                status.Text = $" remove failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
            }
        };
        noButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            outcome = Outcome.Cancelled;
            _app.RequestStop();
        };
        advancedButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            outcome = Outcome.EscalateToWizard;
            _app.RequestStop();
        };

        dialog.KeyDown += (_, key) =>
        {
            var ch = key.AsRune.Value;
            if (ch == 'y' || ch == 'Y')
            {
                key.Handled = true;
                yesButton.InvokeCommand(Command.Accept);
            }
            else if (ch == 'n' || ch == 'N' || key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                outcome = Outcome.Cancelled;
                _app.RequestStop();
            }
            else if (ch == 'a' || ch == 'A')
            {
                key.Handled = true;
                outcome = Outcome.EscalateToWizard;
                _app.RequestStop();
            }
        };

        dialog.Add(prompt, path, warnings, status, yesButton, noButton, advancedButton);
        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName,
            dialog, prompt, path, warnings, status,
            yesButton, noButton, advancedButton);

        _app.Run(dialog);
        dialog.Dispose();

        return new Result(outcome, report);
    }
}
