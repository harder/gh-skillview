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
        var targets = RemoveTargetResolver.BuildTargets(_target, _snapshot);
        var selectedIndex = FindInitialSelection(targets);
        var currentEvaluation = Evaluate(targets[selectedIndex]);

        using var wizard = new Wizard
        {
            Title = $"Remove — {_target.Name}",
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
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Text = string.Empty,
        };
        confirmStep.Add(confirmText, secondConfirm, status);

        TuiHelpers.ApplyScheme(
            SkillViewStyling.BaseSchemeName,
            wizard,
            choiceLabel,
            choicePicker,
            choiceDescription,
            review,
            confirmText,
            secondConfirm,
            status);

        wizard.AddStep(chooseStep);
        wizard.AddStep(reviewStep);
        wizard.AddStep(confirmStep);

        void RefreshEvaluation()
        {
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

        wizard.Accepting += (_, e) =>
        {
            e.Handled = true;

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

            status.Text = " removing…";

            try
            {
                var report = Execute(currentEvaluation);
                LastReport = report;
                if (report.Succeeded || report.TargetsDeleted > 0)
                {
                    Confirmed = true;
                    _app.RequestStop();
                }
                else
                {
                    status.Text = $" remove failed — {report.Errors.Length} error(s); see logs";
                }
            }
            catch (Exception ex)
            {
                _logger.Error("remove", ex.Message);
                status.Text = " remove failed — see logs";
            }
        };

        wizard.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Esc)
            {
                return;
            }

            _app.RequestStop();
            key.Handled = true;
        };

        RefreshEvaluation();
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

    private RemoveService.BatchRemoveReport Execute(RemoveTargetEvaluation evaluation)
    {
        if (evaluation.Target.Kind == RemoveTargetKind.AgentSymlink && evaluation.Target.AgentMembership is { } agent)
        {
            System.IO.File.Delete(agent.Path);
            _logger.Info("remove.agent", $"unlinked {agent.AgentId}: {agent.Path}");
            return new RemoveService.BatchRemoveReport(
                Succeeded: true,
                TargetsDeleted: 1,
                FilesDeleted: 1,
                DirectoriesDeleted: 0,
                Errors: ImmutableArray<string>.Empty,
                DryRun: false);
        }

        return _remove.RemoveMany(evaluation.Items.Select(item => item.Validation));
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
