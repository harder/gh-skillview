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
        using var lifetime = new CancellationTokenSource();
        var targets = RemoveTargetResolver.BuildTargets(_target, _snapshot);
        var selectedIndex = FindInitialSelection(targets);
        var currentEvaluation = Evaluate(targets[selectedIndex]);
        RemoveService.RemoveProgress? lastProgress = null;
        Task? activeOperation = null;
        var wizardActive = 1;

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
            Height = Dim.Fill(4),
            Labels = targets.Select(target => target.Title).ToArray(),
            Value = selectedIndex,
        };
        var choiceDescription = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Text = targets[0].Description,
        };
        chooseStep.Add(choiceLabel, choicePicker, choiceDescription);

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
            Text = RemoveWizardContent.BuildReviewMarkdown(currentEvaluation),
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
            Text = RemoveWizardContent.BuildConfirmMarkdown(currentEvaluation),
        };
        TuiHelpers.ConfigureMarkdownPane(confirmText, SkillViewStyling.BaseSchemeName);
        var secondConfirm = new CheckBox
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Text = "_I understand the warnings and want to continue",
            Visible = currentEvaluation.RequiresSecondConfirm,
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
            review,
            confirmText,
            secondConfirm,
            spinner,
            status);

        wizard.AddStep(chooseStep);
        wizard.AddStep(reviewStep);
        wizard.AddStep(confirmStep);

        void RefreshEvaluation()
        {
            // The radio the user *sees* is the source of truth. The
            // OptionSelector builds its checkbox subviews lazily during layout
            // and can raise a transient ValueChanged that leaves both the cached
            // selectedIndex and the selector's own Value out of sync with the
            // checkbox that is visually selected (so the default lands on the
            // first executable option visually, but the evaluation stays on the
            // blocked first option). Read the checked checkbox directly so
            // Review/Confirm always match what's on screen.
            var checkedIndex = CurrentSelection(choicePicker, selectedIndex);
            if (checkedIndex >= 0 && checkedIndex < targets.Length)
            {
                selectedIndex = checkedIndex;
            }

            var target = targets[selectedIndex];
            currentEvaluation = Evaluate(target);
            choiceDescription.Text = target.Description;
            review.Text = RemoveWizardContent.BuildReviewMarkdown(currentEvaluation);
            confirmText.Text = RemoveWizardContent.BuildConfirmMarkdown(currentEvaluation);
            secondConfirm.Visible = currentEvaluation.RequiresSecondConfirm;
            secondConfirm.Value = CheckState.UnChecked;
            status.Text = currentEvaluation.CanExecute
                ? " ready"
                : " blocked — choose a different option or close";
            reviewStep.NextButtonText = currentEvaluation.CanExecute ? "Continue" : "Close";
            reviewStep.HelpText = currentEvaluation.CanExecute
                ? "Review the impact, then continue to confirm."
                : "SkillView can't do this safely. Use Close or go Back to choose a less destructive option.";
            confirmStep.Enabled = currentEvaluation.CanExecute;
            confirmStep.NextButtonText = RemoveWizardContent.ActionText(target);
            confirmStep.HelpText = currentEvaluation.RequiresSecondConfirm
                ? "Final confirmation is required because SkillView found related installs or repository state."
                : "Finish to apply this removal.";
            if (!currentEvaluation.CanExecute && wizard.CurrentStep == confirmStep)
            {
                wizard.CurrentStep = reviewStep;
            }
        }

        choicePicker.ValueChanged += (_, _) =>
        {
            if (choicePicker.Value is int value && value >= 0 && value < targets.Length)
            {
                selectedIndex = value;
                RefreshEvaluation();
            }
        };

        // Re-sync the evaluation from the live selection whenever the wizard
        // moves between steps. This guarantees the Review and Confirm steps are
        // rebuilt from whatever the radio currently shows, even if an init-time
        // ValueChanged left selectedIndex stale before the user advanced.
        wizard.StepChanged += (_, _) => RefreshEvaluation();

        // The OptionSelector opens with keyboard focus on its first checkbox,
        // which may differ from the checked default (FindInitialSelection picks
        // the first *executable* option, often not the first one). Pressing
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

            if (activeOperation is not null)
            {
                return;
            }

            if (wizard.CurrentStep != confirmStep)
            {
                _app.RequestStop();
                return;
            }

            if (currentEvaluation.RequiresSecondConfirm && secondConfirm.Value != CheckState.Checked)
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
            var cancellationToken = lifetime.Token;
            activeOperation = RunRemovalAsync(evaluation, cancellationToken);
        };

        wizard.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Esc)
            {
                return;
            }

            key.Handled = true;
            if (activeOperation is { IsCompleted: false })
            {
                lifetime.Cancel();
                status.Text = " canceling removal…";
                return;
            }

            lifetime.Cancel();
            _app.RequestStop();
        };

        void InvokeIfActive(Action action)
        {
            if (Volatile.Read(ref wizardActive) == 0)
            {
                return;
            }

            try
            {
                _app.Invoke(() =>
                {
                    if (Volatile.Read(ref wizardActive) == 0)
                    {
                        return;
                    }

                    try { action(); }
                    catch (Exception ex) { _logger.Error("remove.ui", ex.Message); }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("remove.ui", ex.Message);
            }
        }

        async Task RunRemovalAsync(
            RemoveTargetEvaluation evaluation,
            CancellationToken cancellationToken)
        {
            var progress = new CallbackProgress<RemoveService.RemoveProgress>(value =>
            {
                lastProgress = value;
                InvokeIfActive(() => status.Text = FormatProgress(value));
            });

            try
            {
                var report = await ExecuteAsync(evaluation, cancellationToken, progress)
                    .ConfigureAwait(false);
                LastReport = RemovalReportState.Accumulate(LastReport, report);
                InvokeIfActive(() =>
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
                        activeOperation = null;
                        status.Text = $" remove failed — {report.ErrorCount} error(s); see logs";
                    }
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LastReport = RemovalReportState.Accumulate(
                    LastReport,
                    RemovalReportState.Canceled(lastProgress));
                _logger.Debug("remove", "removal canceled");
                InvokeIfActive(() =>
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
                InvokeIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    secondConfirm.Enabled = true;
                    activeOperation = null;
                    status.Text = " remove failed — see logs";
                });
            }
        }

        RefreshEvaluation();
        try
        {
            _app.Run(wizard);
        }
        finally
        {
            Interlocked.Exchange(ref wizardActive, 0);
            lifetime.Cancel();
            activeOperation?.GetAwaiter().GetResult();
        }
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
        if (evaluation.Target.Kind == RemoveTargetKind.AgentSymlink && evaluation.Target.AgentMembership is { } agent)
        {
            var report = await _remove.RemoveLinkAsync(agent.Path, cancellationToken, progress)
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

    private int FindInitialSelection(ImmutableArray<RemoveTarget> targets)
    {
        for (var i = 0; i < targets.Length; i++)
        {
            if (Evaluate(targets[i]).CanExecute)
            {
                return i;
            }
        }

        return 0;
    }
}
