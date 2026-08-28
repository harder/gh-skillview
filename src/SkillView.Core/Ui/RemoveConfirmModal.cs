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

    internal enum OperationOwnership
    {
        None,
        Running,
        AwaitingUiCompletion,
    }

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

    internal static OperationOwnership ClassifyOperation(Task? operation) => operation switch
    {
        null => OperationOwnership.None,
        { IsCompleted: false } => OperationOwnership.Running,
        _ => OperationOwnership.AwaitingUiCompletion,
    };

    internal Result Show()
    {
        using var lifetime = new CancellationTokenSource();
        var validation = _evaluation.Items[0].Validation;
        var outcome = Outcome.Cancelled;
        RemoveService.RemoveReport? report = null;
        RemoveService.RemoveProgress? lastProgress = null;
        Task? activeOperation = null;
        var dialogActive = 1;

        using var dialog = new Dialog
        {
            Title = " Remove skill ",
            Width = Dim.Percent(50),
            Height = 12,
        };
        dialog.SchemeName = SchemeNames.Dialog;

        var prompt = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Text = $"Remove {_skill.Name}?",
        };
        var path = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Text = $"  {TuiHelpers.ShortenPath(_skill.ResolvedPath, segments: 4)}",
        };
        var warningsText = _evaluation.Warnings.Length > 0
            ? "  ⚠ " + string.Join("; ", _evaluation.Warnings.Select(w => w.Detail))
            : string.Empty;
        var warnings = new Label
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill(2),
            Text = warningsText,
            Visible = warningsText.Length > 0,
        };

        var status = new Label
        {
            X = 3,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(4),
            Text = " [y] yes   [n] no   [a] advanced…",
        };
        var spinner = new SpinnerView
        {
            X = 1,
            Y = Pos.AnchorEnd(3),
            Visible = false,
            AutoSpin = false,
        };

        var yesButton = new Button
        {
            X = Pos.Center() - 16,
            Y = Pos.AnchorEnd(1),
            Text = "Yes",
            IsDefault = false,
        };
        var noButton = new Button
        {
            X = Pos.Center() - 6,
            Y = Pos.AnchorEnd(1),
            Text = "No",
            IsDefault = true,
        };
        var advancedButton = new Button
        {
            X = Pos.Center() + 4,
            Y = Pos.AnchorEnd(1),
            Text = "Advanced…",
        };

        void InvokeIfActive(Action action)
        {
            if (Volatile.Read(ref dialogActive) == 0)
            {
                return;
            }

            try
            {
                _app.Invoke(() =>
                {
                    if (Volatile.Read(ref dialogActive) == 0)
                    {
                        return;
                    }

                    try { action(); }
                    catch (Exception ex) { _logger.Error("remove.compact.ui", ex.Message); }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("remove.compact.ui", ex.Message);
            }
        }

        void SetRunning(bool running)
        {
            spinner.AutoSpin = running;
            spinner.Visible = running;
            yesButton.Enabled = !running;
            noButton.Enabled = !running;
            advancedButton.Enabled = !running;
        }

        async Task RunRemovalAsync(CancellationToken cancellationToken)
        {
            var progress = new CallbackProgress<RemoveService.RemoveProgress>(value =>
            {
                lastProgress = value;
                InvokeIfActive(() => status.Text = FormatProgress(value));
            });

            try
            {
                var completed = await _remove.RemoveAsync(
                    validation,
                    new RemoveService.Options(DryRun: false),
                    cancellationToken,
                    progress).ConfigureAwait(false);
                report = RemovalReportState.Accumulate(report, completed);
                outcome = completed.Succeeded ? Outcome.Removed : Outcome.Failed;
                InvokeIfActive(() =>
                {
                    SetRunning(false);
                    if (completed.Succeeded)
                    {
                        status.Text = " removed — closing";
                        _app.RequestStop();
                    }
                    else
                    {
                        activeOperation = null;
                        var detail = TuiHelpers.ErrorSnippet(completed.Errors.FirstOrDefault());
                        status.Text = detail.Length > 0
                            ? $" remove failed: {detail}"
                            : " remove failed — see logs";
                    }
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                report = RemovalReportState.Accumulate(
                    report,
                    RemovalReportState.Canceled(validation.ResolvedPath, lastProgress));
                outcome = Outcome.Cancelled;
                _logger.Debug("remove.compact", "removal canceled");
                InvokeIfActive(() =>
                {
                    SetRunning(false);
                    status.Text = " removal canceled";
                    _app.RequestStop();
                });
            }
            catch (Exception ex)
            {
                _logger.Error("remove.compact", ex.Message);
                report = RemovalReportState.Accumulate(
                    report,
                    RemovalReportState.Failed(
                        validation.ResolvedPath,
                        lastProgress,
                        ex.Message));
                outcome = Outcome.Failed;
                InvokeIfActive(() =>
                {
                    SetRunning(false);
                    activeOperation = null;
                    var detail = TuiHelpers.ErrorSnippet(ex.Message);
                    status.Text = detail.Length > 0
                        ? $" remove failed: {detail}"
                        : " remove failed — see logs";
                });
            }
        }

        yesButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            if (activeOperation is not null)
            {
                return;
            }

            SetRunning(true);
            lastProgress = null;
            status.Text = $" removing {_skill.Name}…  Esc cancels";
            var cancellationToken = lifetime.Token;
            activeOperation = RunRemovalAsync(cancellationToken);
        };
        noButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            lifetime.Cancel();
            outcome = Outcome.Cancelled;
            _app.RequestStop();
        };
        advancedButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            lifetime.Cancel();
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
                var operationOwnership = ClassifyOperation(activeOperation);
                if (operationOwnership != OperationOwnership.None)
                {
                    if (operationOwnership == OperationOwnership.Running)
                    {
                        lifetime.Cancel();
                        status.Text = " canceling removal…";
                    }
                    else
                    {
                        status.Text = " finishing removal…";
                    }
                    return;
                }
                lifetime.Cancel();
                outcome = Outcome.Cancelled;
                _app.RequestStop();
            }
            else if (ch == 'a' || ch == 'A')
            {
                key.Handled = true;
                if (ClassifyOperation(activeOperation) != OperationOwnership.None)
                {
                    status.Text = activeOperation!.IsCompleted
                        ? " finishing removal…"
                        : " removal in progress — Esc cancels";
                    return;
                }
                outcome = Outcome.EscalateToWizard;
                _app.RequestStop();
            }
        };

        dialog.Add(prompt, path, warnings, spinner, status, yesButton, noButton, advancedButton);
        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName,
            dialog, prompt, path, warnings, spinner, status,
            yesButton, noButton, advancedButton);

        try
        {
            _app.Run(dialog);
        }
        finally
        {
            Interlocked.Exchange(ref dialogActive, 0);
            lifetime.Cancel();
            activeOperation?.GetAwaiter().GetResult();
        }

        return new Result(outcome, report);
    }

    private static string FormatProgress(RemoveService.RemoveProgress progress)
    {
        if (progress.IsCanceled)
        {
            return $" canceling… {progress.FilesProcessed} file(s), {progress.DirectoriesProcessed} dir(s)";
        }

        return $" removing… {progress.FilesProcessed} file(s), {progress.DirectoriesProcessed} dir(s)  Esc cancels";
    }
}
