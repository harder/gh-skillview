using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Wizard-based remove flow with progressive disclosure:
/// pick the scope, review the consequence, then confirm if the action is safe.
public sealed class RemoveScreen
{
    private readonly IApplication _app;
    private readonly RemoveService _remove;
    private readonly Logger _logger;
    private readonly InstalledSkill _target;
    private readonly InventorySnapshot _snapshot;
    private readonly RemoveValidator.RemoveValidation? _legacyValidation;

    public RemoveService.BatchRemoveReport? LastReport { get; private set; }
    public bool Confirmed { get; private set; }

    public RemoveScreen(
        IApplication app,
        RemoveService remove,
        Logger logger,
        InstalledSkill target,
        InventorySnapshot snapshot)
    {
        _app = app;
        _remove = remove;
        _logger = logger;
        _target = target;
        _snapshot = snapshot;
    }

    internal RemoveScreen(
        IApplication app,
        RemoveService remove,
        Logger logger,
        InstalledSkill target,
        RemoveValidator.RemoveValidation validation)
    {
        _app = app;
        _remove = remove;
        _logger = logger;
        _target = target;
        _snapshot = new InventorySnapshot
        {
            Skills = [target],
            ScannedRoots = ImmutableArray<ScanRoot>.Empty,
            UsedGhSkillList = false,
            CapturedAt = DateTimeOffset.UtcNow,
        };
        _legacyValidation = validation;
    }

    public void Show()
    {
        using var operation = new ModalOperationTracker(_app, _logger, "remove.ui");
        var targets = RemoveTargetResolver.BuildTargets(_target, _snapshot);
        var evaluations = new Dictionary<int, RemoveTargetEvaluation>();
        var selectedIndex = 0;
        RemoveTargetEvaluation? currentEvaluation = null;
        RemoveService.RemoveProgress? lastProgress = null;

        using var wizard = new Wizard
        {
            Title = $"Remove — {_target.Name}",
            // Wizard inherits Dialog's Dim.Auto sizing, which collapses to each
            // step's *natural* content size. Every step here lays its content
            // out with Dim.Fill()/Pos.AnchorEnd(), which contribute nothing to
            // an Auto measurement, so without an explicit size the wizard
            // shrinks to the lone fixed label and hides the radio list and the
            // Review/Confirm markdown. Pin a percentage size (matching the
            // explicit-size pattern in InstallConfirmModal) so the Fill content
            // gets real space; the 25-col help padding still fits comfortably.
            Width = Dim.Percent(80),
            Height = Dim.Percent(70),
        };

        var chooseStep = new WizardStep
        {
            Title = "Choose",
            NextButtonText = "Review",
            HelpText = "Pick what you want to remove. Package and repo scopes only appear when SkillView has explicit metadata.",
        };

        var choiceLabel = new Label
        {
            X = 0,
            Y = 0,
            Text = "What do you want to remove?",
        };
        var choicePicker = new OptionSelector
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(5),
            Labels = targets.Select(target => target.Title).ToArray(),
            Value = selectedIndex,
        };
        var choiceDescription = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Text = targets[0].Description,
        };
        var evaluationSpinner = new SpinnerView
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Visible = false,
            AutoSpin = false,
        };
        var evaluationStatus = new Label
        {
            X = 2,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(2),
            Text = " checking removal safety…",
        };
        chooseStep.Add(
            choiceLabel,
            choicePicker,
            choiceDescription,
            evaluationSpinner,
            evaluationStatus);

        var reviewStep = new WizardStep
        {
            Title = "Review",
        };
        var review = new Markdown
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "## Checking removal safety…",
        };
        TuiHelpers.ConfigureMarkdownPane(review, SkillViewStyling.BaseSchemeName);
        reviewStep.Add(review);

        var confirmStep = new WizardStep
        {
            Title = "Confirm",
        };
        var confirmText = new Markdown
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
            Text = string.Empty,
        };
        TuiHelpers.ConfigureMarkdownPane(confirmText, SkillViewStyling.BaseSchemeName);
        var secondConfirm = new CheckBox
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Text = "_I understand the warnings and want to continue",
            Visible = false,
        };
        var status = new Label
        {
            X = 2,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(2),
            Text = string.Empty,
        };
        var spinner = new SpinnerView
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Visible = false,
            AutoSpin = false,
        };
        confirmStep.Add(confirmText, secondConfirm, spinner, status);

        TuiHelpers.ApplyScheme(
            SkillViewStyling.BaseSchemeName,
            wizard,
            choiceLabel,
            choicePicker,
            choiceDescription,
            evaluationSpinner,
            evaluationStatus,
            review,
            confirmText,
            secondConfirm,
            spinner,
            status);

        wizard.AddStep(chooseStep);
        wizard.AddStep(reviewStep);
        wizard.AddStep(confirmStep);

        void SetEvaluationBusy(bool busy)
        {
            evaluationSpinner.AutoSpin = busy;
            evaluationSpinner.Visible = busy;
            choicePicker.Enabled = !busy;
            evaluationStatus.Text = busy
                ? " checking removal safety…"
                : string.Empty;
        }

        void ApplyEvaluation(int index, RemoveTargetEvaluation evaluation)
        {
            selectedIndex = index;
            currentEvaluation = evaluation;
            var target = targets[index];
            choiceDescription.Text = target.Description;
            review.Text = RemoveWizardContent.BuildReviewMarkdown(evaluation);
            confirmText.Text = RemoveWizardContent.BuildConfirmMarkdown(evaluation);
            secondConfirm.Visible = evaluation.RequiresSecondConfirm;
            secondConfirm.Value = CheckState.UnChecked;
            status.Text = evaluation.CanExecute
                ? " ready"
                : " blocked — choose a different option or close";
            reviewStep.NextButtonText = evaluation.CanExecute ? "Continue" : "Close";
            reviewStep.HelpText = evaluation.CanExecute
                ? "Review the impact, then continue to confirm."
                : "SkillView can't do this safely. Use Close or go Back to choose a less destructive option.";
            confirmStep.Enabled = evaluation.CanExecute;
            confirmStep.NextButtonText = RemoveWizardContent.ActionText(target);
            confirmStep.HelpText = evaluation.RequiresSecondConfirm
                ? "Final confirmation is required because SkillView found related installs or repository state."
                : "Finish to apply this removal.";
            if (!evaluation.CanExecute && wizard.CurrentStep == confirmStep)
            {
                wizard.CurrentStep = reviewStep;
            }
        }

        void RefreshFromSelection()
        {
            // The checkbox the user sees is the source of truth because
            // OptionSelector can transiently desynchronize Value during lazy
            // subview layout.
            var checkedIndex = CurrentSelection(choicePicker, selectedIndex);
            if (checkedIndex >= 0 && checkedIndex < targets.Length)
            {
                selectedIndex = checkedIndex;
            }

            if (evaluations.TryGetValue(selectedIndex, out var cached))
            {
                ApplyEvaluation(selectedIndex, cached);
                return;
            }

            StartEvaluation(selectedIndex, findFirstExecutable: false);
        }

        choicePicker.ValueChanged += (_, _) =>
        {
            if (choicePicker.Value is int value && value >= 0 && value < targets.Length)
            {
                selectedIndex = value;
                RefreshFromSelection();
            }
        };

        // Re-sync the evaluation from the live selection whenever the wizard
        // moves between steps. This guarantees the Review and Confirm steps are
        // rebuilt from whatever the radio currently shows, even if an init-time
        // ValueChanged left selectedIndex stale before the user advanced.
        wizard.StepChanged += (_, _) => RefreshFromSelection();

        // The OptionSelector opens with keyboard focus on its first checkbox,
        // which may differ from the checked default (the initial background
        // evaluation picks the first executable option). Pressing
        // Enter to advance would then "activate" the focused-but-unchecked
        // checkbox and silently change the selection (e.g. from Unlink back to a
        // blocked full remove). Align the focused item with the checked value so
        // Enter advances without mutating the choice.
        choicePicker.HasFocusChanged += (_, _) =>
        {
            if (!choicePicker.HasFocus)
            {
                return;
            }

            var checkedIndex = CurrentSelection(choicePicker, selectedIndex);
            if (checkedIndex >= 0 && checkedIndex < targets.Length && choicePicker.FocusedItem != checkedIndex)
            {
                choicePicker.FocusedItem = checkedIndex;
            }
        };

        wizard.Accepting += (_, e) =>
        {
            e.Handled = true;

            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None
                || currentEvaluation is null)
            {
                return;
            }

            if (wizard.CurrentStep != confirmStep)
            {
                _app.RequestStop();
                return;
            }

            if (currentEvaluation.RequiresSecondConfirm
                && secondConfirm.Value != CheckState.Checked)
            {
                status.Text = " check the confirmation box to continue";
                return;
            }

            spinner.Visible = true;
            spinner.AutoSpin = true;
            secondConfirm.Enabled = false;
            lastProgress = null;
            status.Text = " removing…  Esc cancels";
            var evaluation = currentEvaluation;
            operation.TryStart(token => RunRemovalAsync(evaluation, token));
        };

        wizard.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Esc)
            {
                return;
            }

            key.Handled = true;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            {
                operation.Cancel();
                status.Text = " canceling operation…";
                return;
            }

            _app.RequestStop();
        };

        void StartEvaluation(int index, bool findFirstExecutable)
        {
            if (index < 0
                || index >= targets.Length
                || operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            {
                return;
            }

            SetEvaluationBusy(true);
            review.Text = "## Checking removal safety…";
            confirmStep.Enabled = false;
            if (!operation.TryStart(token =>
                    RunEvaluationAsync(index, findFirstExecutable, token)))
            {
                SetEvaluationBusy(false);
            }
        }

        async Task RunEvaluationAsync(
            int requestedIndex,
            bool findFirstExecutable,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await Task.Run(() =>
                {
                    var completed = new List<(int Index, RemoveTargetEvaluation Evaluation)>();
                    var selected = requestedIndex;
                    if (findFirstExecutable)
                    {
                        for (var index = 0; index < targets.Length; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var evaluation = Evaluate(targets[index]);
                            completed.Add((index, evaluation));
                            if (evaluation.CanExecute)
                            {
                                selected = index;
                                break;
                            }
                        }
                    }
                    else
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        completed.Add((requestedIndex, Evaluate(targets[requestedIndex])));
                    }
                    return (Selected: selected, Completed: completed);
                }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                operation.InvokeTerminalIfActive(() =>
                {
                    foreach (var completed in result.Completed)
                    {
                        evaluations[completed.Index] = completed.Evaluation;
                    }

                    if (!evaluations.TryGetValue(result.Selected, out var evaluation))
                    {
                        evaluation = result.Completed[0].Evaluation;
                    }
                    choicePicker.Value = result.Selected;
                    choicePicker.FocusedItem = result.Selected;
                    ApplyEvaluation(result.Selected, evaluation);
                    SetEvaluationBusy(false);
                    operation.Release();
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                operation.InvokeTerminalIfActive(() =>
                {
                    SetEvaluationBusy(false);
                    evaluationStatus.Text = " safety check canceled";
                    _app.RequestStop();
                });
            }
            catch (Exception ex)
            {
                _logger.Error("remove.evaluate", ex.Message);
                operation.InvokeTerminalIfActive(() =>
                {
                    SetEvaluationBusy(false);
                    evaluationStatus.Text = " safety check failed — see logs";
                    operation.Release();
                });
            }
        }

        async Task RunRemovalAsync(
            RemoveTargetEvaluation evaluation,
            CancellationToken cancellationToken)
        {
            var progress = new CallbackProgress<RemoveService.RemoveProgress>(value =>
            {
                lastProgress = value;
                operation.InvokeIfActive(() => status.Text = FormatProgress(value));
            });

            try
            {
                var refreshed = await Task.Run(
                    () => Evaluate(evaluation.Target),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!HasSameSafetyContract(evaluation, refreshed))
                {
                    operation.InvokeTerminalIfActive(() =>
                    {
                        var refreshedIndex = targets.IndexOf(evaluation.Target);
                        if (refreshedIndex >= 0)
                        {
                            evaluations[refreshedIndex] = refreshed;
                            ApplyEvaluation(refreshedIndex, refreshed);
                        }
                        spinner.AutoSpin = false;
                        spinner.Visible = false;
                        secondConfirm.Enabled = true;
                        wizard.CurrentStep = reviewStep;
                        status.Text = " filesystem state changed — review again";
                        operation.Release();
                    });
                    return;
                }

                var report = await ExecuteAsync(refreshed, cancellationToken, progress)
                    .ConfigureAwait(false);
                LastReport = RemovalReportState.Accumulate(LastReport, report);
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    secondConfirm.Enabled = true;
                    if (report.Succeeded || report.TargetsDeleted > 0)
                    {
                        Confirmed = true;
                        _app.RequestStop();
                    }
                    else
                    {
                        status.Text = $" remove failed — {report.ErrorCount} error(s); see logs";
                        operation.Release();
                    }
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LastReport = RemovalReportState.Accumulate(
                    LastReport,
                    RemovalReportState.Canceled(lastProgress));
                _logger.Debug("remove", "removal canceled");
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    status.Text = " removal canceled";
                    _app.RequestStop();
                });
            }
            catch (Exception ex)
            {
                _logger.Error("remove", ex.Message);
                LastReport = RemovalReportState.Accumulate(
                    LastReport,
                    RemovalReportState.Failed(lastProgress, ex.Message));
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    secondConfirm.Enabled = true;
                    status.Text = " remove failed — see logs";
                    operation.Release();
                });
            }
        }

        wizard.Initialized += (_, _) =>
            StartEvaluation(selectedIndex, findFirstExecutable: true);
        _app.Run(wizard);
    }

    internal string BuildSummary()
    {
        if (_legacyValidation is not null)
        {
            return RemoveWizardContent.BuildLegacySummary(_target, _legacyValidation);
        }

        var target = RemoveTargetResolver.BuildTargets(_target, _snapshot)[0];
        return RemoveWizardContent.BuildReviewMarkdown(Evaluate(target));
    }

    private RemoveTargetEvaluation Evaluate(RemoveTarget target)
    {
        if (_legacyValidation is not null && target.Kind == RemoveTargetKind.CurrentInstall)
        {
            return new RemoveTargetEvaluation(
                target,
                [new RemoveTargetItem(_target, _legacyValidation)]);
        }

        return RemoveTargetResolver.Evaluate(target, _snapshot);
    }

    private async Task<RemoveService.BatchRemoveReport> ExecuteAsync(
        RemoveTargetEvaluation evaluation,
        CancellationToken cancellationToken,
        IProgress<RemoveService.RemoveProgress> progress)
    {
        if (evaluation.Target.Kind == RemoveTargetKind.AgentSymlink)
        {
            var report = await _remove.RemoveAsync(
                    evaluation.Items.Single().Validation,
                    cancellationToken: cancellationToken,
                    progress: progress)
                .ConfigureAwait(false);
            return RemoveService.BatchRemoveReport.FromSingle(
                report,
                targetsDeleted: report.Succeeded ? 1 : 0);
        }

        return await _remove.RemoveManyAsync(
            evaluation.Items.Select(item => item.Validation),
            cancellationToken: cancellationToken,
            progress: progress).ConfigureAwait(false);
    }

    private static string FormatProgress(RemoveService.RemoveProgress progress)
    {
        var targetCount = progress.IsCanceled
            ? progress.TargetsDeleted
            : progress.TargetsProcessed;
        var targets = targetCount > 0
            ? $"{targetCount} target(s), "
            : string.Empty;
        return progress.IsCanceled
            ? $" canceling… {targets}{progress.FilesProcessed} file(s), {progress.DirectoriesProcessed} dir(s)"
            : $" removing… {targets}{progress.FilesProcessed} file(s), {progress.DirectoriesProcessed} dir(s)  Esc cancels";
    }

    internal static bool HasSameSafetyContract(
        RemoveTargetEvaluation displayed,
        RemoveTargetEvaluation refreshed)
    {
        if (displayed.Target != refreshed.Target
            || displayed.Items.Length != refreshed.Items.Length)
        {
            return false;
        }

        for (var index = 0; index < displayed.Items.Length; index++)
        {
            var left = displayed.Items[index];
            var right = refreshed.Items[index];
            if (!PathIdentity.Equals(left.Skill.ResolvedPath, right.Skill.ResolvedPath)
                || !PathIdentity.Equals(
                    left.Validation.ResolvedPath,
                    right.Validation.ResolvedPath)
                || !left.Validation.Errors.SequenceEqual(right.Validation.Errors)
                || !left.Validation.Warnings.SequenceEqual(right.Validation.Warnings)
                || !left.Validation.IncomingSymlinkPaths.SequenceEqual(
                    right.Validation.IncomingSymlinkPaths,
                    StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// Returns the index of the checkbox the <see cref="OptionSelector"/> is
    /// currently showing as checked. The selector's own <c>Value</c> can desync
    /// from its visual state during lazy subview layout, so the checked subview
    /// is the reliable read of what the user actually sees selected. Falls back
    /// to <paramref name="fallback"/> when no checkbox is checked yet.
    private static int CurrentSelection(OptionSelector picker, int fallback)
    {
        var index = 0;
        foreach (var box in picker.SubViews.OfType<CheckBox>())
        {
            if (box.Value == CheckState.Checked)
            {
                return index;
            }

            index++;
        }

        return fallback;
    }

}
