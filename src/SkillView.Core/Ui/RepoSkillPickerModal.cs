using System.Collections.Immutable;
using System.Drawing;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Per-repo discovery picker. Given a repo's already-discovered skills
/// (from `gh skill install <repo>` run non-interactively — gh ≥ 2.97.0,
/// cli/cli#13548), shows a checklist so the user installs a chosen subset
/// instead of the blunt all-or-one choices. Selecting every skill collapses
/// to a single `gh skill install <repo> --all`; a subset installs each by name.
///
/// Scope and agent pickers mirror <see cref="InstallConfirmModal"/> and reuse
/// its pure option-builder so the two install paths stay consistent. The modal
/// operates on data the caller already fetched in the background, so it never
/// runs discovery on the UI thread.
internal sealed class RepoSkillPickerModal
{
    internal enum Outcome
    {
        Cancelled,
        Installed,
        Failed,
    }

    internal sealed record Result(Outcome Outcome, int InstalledCount, int FailedCount, string? FirstError,
        InstallAttemptSummary? Attempts = null, InstalledInstallHandoff? Handoff = null);

    // Fixed visible height of the scrollable agent-checkbox grid — matches
    // the 3-row budget the previous fixed AnchorEnd(6)..(4) layout had before
    // colliding with the status row at AnchorEnd(3).
    private const int AgentsVisibleRows = 3;

    private readonly InstallAttemptRun _attempts;
    private readonly ProcessRunner _runner;
    private readonly Func<CancellationToken, Task<InstalledInstallHandoff>>? _browseInstalled;
    private readonly Func<InstalledInstallHandoff, bool>? _canPublish;
    private readonly Func<bool> _contextCurrent;
    private readonly IApplication _app;
    private readonly GhSkillInstallService _install;
    private readonly Logger _logger;
    private readonly string _ghPath;
    private readonly InstallRequest _request;
    private readonly ImmutableArray<RepoSkill> _skills;

    internal RepoSkillPickerModal(
        IApplication app,
        GhSkillInstallService install,
        Logger logger,
        string ghPath,
        InstallRequest request,
        ImmutableArray<RepoSkill> skills,
        InstallAttemptRun? attempts = null,
        Func<CancellationToken, Task<InstalledInstallHandoff>>? browseInstalled = null,
        Func<InstalledInstallHandoff, bool>? canPublish = null)
    {
        _attempts = attempts ?? new();
        _browseInstalled = browseInstalled;
        _canPublish = canPublish;
        _app = app;
        _runner = install.CaptureContext();
        var contextKey = InstallNavigationEvidence.Context(_runner);
        _contextCurrent = () => contextKey == InstallNavigationEvidence.Context(install.CaptureContext());
        _install = new(_runner, logger);
        _logger = logger;
        _ghPath = ghPath;
        _request = request;
        _skills = skills;
    }

    internal Result Show()
    {
        var outcome = Outcome.Cancelled;
        var preparing = true;
        InstalledInstallHandoff? handoff = null;
        var installedCount = 0;
        var failedCount = 0;
        string? firstError = null;

        using var dialog = new Dialog
        {
            Title = $" Install skills from {_request.Repo} ",
            Width = Dim.Func(_ => Math.Min(100, _app.Screen.Width - 4)),
            Height = Dim.Percent(85),
        };
        dialog.SchemeName = SchemeNames.Dialog;
        using var operation = new ModalOperationTracker(_app, _logger, "install.picker.ui");

        var header = new Label
        {
            X = 1,
            Y = 0,
            Text = $"{_skills.Length} skills · Space toggles · A all · N none",
            Width = Dim.Fill(2),
        };

        // Scrollable checklist of discovered skills, pre-checked. The frame
        // leaves the bottom rows for scope/agents/status/buttons.
        var listFrame = new FrameView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(10),
        };
        listFrame.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;

        var skillBoxes = new CheckBox[_skills.Length];
        var contentWidth = 20;
        for (var i = 0; i < _skills.Length; i++)
        {
            var skill = _skills[i];
            // Keep labels bounded so the (non-horizontally-scrolling) content
            // region stays readable; the full description lives in the repo.
            var desc = skill.Description.Length > 70
                ? skill.Description[..70].TrimEnd() + "…"
                : skill.Description;
            var label = string.IsNullOrEmpty(desc) ? skill.Name : $"{skill.Name} — {desc}";
            contentWidth = Math.Max(contentWidth, label.Length + 4);
            var box = new CheckBox
            {
                X = 0,
                Y = i,
                Text = label,
                Value = CheckState.Checked,
            };
            skillBoxes[i] = box;
            listFrame.Add(box);
        }
        // Content size drives the vertical scrollbar; the width must be wide
        // enough for the labels (a width of 1 would clip every label to the
        // checkbox glyph). Height is the row count so all skills scroll.
        listFrame.SetContentSize(new Size(contentWidth, Math.Max(_skills.Length, 1)));

        var scopeLabel = new Label { X = 1, Y = Pos.AnchorEnd(9), Text = "Scope:" };
        var scopeSelector = new OptionSelector
        {
            X = 9,
            Y = Pos.AnchorEnd(9),
            Orientation = Orientation.Horizontal,
            Labels = new List<string> { "Project", "User", "Custom path" },
            Value = 1,
            FocusedItem = 1,
            Enabled = false,
        };
        var customPathLabel = new Label { X = 1, Y = Pos.AnchorEnd(8), Text = "Path:", Visible = false };
        var customPathField = new TextField
        {
            X = 9,
            Y = Pos.AnchorEnd(8),
            Width = Dim.Fill(2),
            Text = string.Empty,
            Visible = false,
        };
        TuiHelpers.ConfigureTextInput(customPathField, SkillViewStyling.DialogSchemeName);

        var agentsLabel = new Label { X = 1, Y = Pos.AnchorEnd(7), Text = "Agents:" };
        var entries = InstallAgentCatalog.Entries;
        var agentsView = new View
        {
            X = 9,
            Y = Pos.AnchorEnd(7),
            Width = Dim.Fill(2),
            Height = AgentsVisibleRows,
        };
        agentsView.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        var agentSelection = new InstallAgentSelection(agentsView);
        var agentBoxes = agentSelection.Boxes;
        agentsView.Enabled = false;
        var placementText = "Placement observation pending.";
        using var placement = new InstallPlacementPreview(_runner, _logger, _app.Invoke,
            observation => placementText = observation.Detail, _contextCurrent);
        void RefreshPlacement()
        {
            if (preparing) return;
            var selected = skillBoxes.Select((box, i) => (box, i)).Where(row => row.box.Value == CheckState.Checked)
                .Select(row => _skills[row.i].Name).ToArray();
            var options = InstallConfirmModal.BuildOptionsFromSelection(scopeSelector.Value ?? 0, customPathField.Text,
                agentSelection.Selected, selected.Length == _skills.Length) with
            { AllowHiddenDirs = _request.AllowHiddenDirs };
            placement.Update(_ghPath, _request.Repo, selected.Length == 1 ? selected[0] : null, options);
        }

        var status = new Label
        {
            X = 3,
            Y = Pos.AnchorEnd(4),
            Height = 3,
            Width = Dim.Fill(2),
            Text = " ready",
        };
        var spinner = new SpinnerView
        {
            X = 1,
            Y = Pos.AnchorEnd(3),
            Width = 1,
            Height = 1,
            Visible = false,
            AutoSpin = false,
            Style = new SpinnerStyle.Dots(),
        };

        var installButton = new Button
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Text = "Install",
            IsDefault = true,
        };
        var detailsButton = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Details", Visible = false };
        detailsButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            if (operation.CurrentOwnership == ModalOperationTracker.Ownership.None && _attempts.Last is { } receipt)
                InstallFailureDetails.Show(_app, InstallFailureDetails.Render(receipt));
        };
        var cancelButton = new Button
        {
            X = Pos.Right(installButton) + 2,
            Y = Pos.AnchorEnd(1),
            Text = "Cancel",
        };

        var placementButton = new SingleRowButton { X = Pos.Right(installButton) + 2, Y = Pos.AnchorEnd(1), Text = "Review placement" };
        cancelButton.X = Pos.Right(placementButton) + 2;
        placementButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (operation.CurrentOwnership == ModalOperationTracker.Ownership.None)
                InstallFailureDetails.Show(_app, placementText, title: " Placement and conflicts (guidance) ");
        };
        agentSelection.Changed += RefreshPlacement;
        scopeSelector.KeyBindings.ReplaceCommands(KeyCode.Enter, Command.Activate);
        foreach (var box in scopeSelector.SubViews.OfType<CheckBox>().Concat(skillBoxes))
            box.KeyBindings.ReplaceCommands(KeyCode.Enter, Command.Activate);

        int CheckedCount() => skillBoxes.Count(b => b.Value == CheckState.Checked);

        string? CurrentValidationError()
        {
            if (CheckedCount() == 0)
            {
                return "select at least one skill";
            }
            return InstallConfirmModal.ValidateSelection(
                scopeSelector.Value ?? 0,
                customPathField.Text.ToString() ?? string.Empty);
        }

        void RefreshValidity()
        {
            var error = CurrentValidationError();
            installButton.Enabled = !preparing && !spinner.Visible && error is null;
            RefreshPlacement();
            if (!spinner.Visible)
            {
                status.Text = error is null ? $" {CheckedCount()}/{_skills.Length} selected" : $" {error}";
            }
        }

        scopeSelector.ValueChanged += (_, _) =>
        {
            var isCustom = scopeSelector.Value == 2;
            customPathLabel.Visible = isCustom;
            customPathField.Visible = isCustom;
            RefreshValidity();
        };
        customPathField.TextChanged += (_, _) => RefreshValidity();
        // Update the "N/M selected" count + Install-enabled state when a row is
        // toggled with Space (the CheckBox change event is ValueChanged here).
        foreach (var box in skillBoxes)
        {
            box.ValueChanged += (_, _) => RefreshValidity();
        }

        void SetAll(CheckState state)
        {
            foreach (var box in skillBoxes) box.Value = state;
            RefreshValidity();
        }

        void SetOperationControlsEnabled(bool enabled)
        {
            installButton.Enabled = enabled && CurrentValidationError() is null;
            cancelButton.Enabled = enabled;
            scopeSelector.Enabled = enabled;
            customPathField.Enabled = enabled;
            listFrame.Enabled = enabled;
            agentsView.Enabled = enabled;
            placementButton.Enabled = enabled;
        }

        async Task RunInstallAsync(
            GhSkillInstallService.InstallPlan plan,
            GhSkillInstallService.Options baseOptions,
            CancellationToken cancellationToken)
        {
            try
            {
                var summary = await _install.InstallBatchAsync(_ghPath, _request.Repo, plan,
                    baseOptions, cancellationToken).ConfigureAwait(false);
                installedCount = summary.Results.Count(result => result.Succeeded);
                failedCount = summary.Results.Count(result => !result.Succeeded);
                firstError = summary.Results.FirstOrDefault(result => !result.Succeeded)?.ErrorMessage;
                var receipt = await _attempts.CompleteAsync(summary, pending =>
                    operation.InvokeIfActive(() =>
                    {
                        listFrame.Visible = scopeLabel.Visible = scopeSelector.Visible = customPathLabel.Visible = customPathField.Visible = false;
                        agentsLabel.Visible = agentsView.Visible = placementButton.Visible = false;
                        dialog.Height = 14;
                        dialog.Y = Pos.Center();
                        status.Y = 3;
                        status.Height = 6;
                        status.Text = pending.Guidance;
                    })).ConfigureAwait(false);
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    if (summary.Succeeded)
                    {
                        outcome = Outcome.Installed;
                        operation.Release();
                        installButton.Text = "Browse repository";
                        installButton.Enabled = true;
                        placementButton.Visible = false;
                        cancelButton.X = Pos.Right(installButton) + 2;
                        cancelButton.Text = "Back to Discover";
                        cancelButton.Enabled = true;
                        status.Text = receipt.Refresh == InstallRefreshState.Completed
                            ? "Install completed; Installed refreshed. Browse observed repository sources; no exact copy is inferred." : receipt.Guidance;
                        installButton.SetFocus();
                    }
                    else
                    {
                        operation.Release();
                        outcome = summary.Canceled ? Outcome.Cancelled : Outcome.Failed;
                        header.Text = $"{installedCount} child succeeded; {failedCount} failed/canceled; {summary.NotAttempted.Count} not attempted.";
                        status.Text = receipt.Guidance + " Details for selection outcomes.";
                        installButton.Visible = false;
                        cancelButton.X = Pos.Right(detailsButton) + 2;
                        detailsButton.Visible = true;
                        detailsButton.Enabled = true;
                        // Keep the completed selection visible. A fresh dialog
                        // is required to choose a new submission after a batch.
                        installButton.Enabled = false;
                        cancelButton.Enabled = true;
                    }
                });
            }

            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("install.picker", "install canceled because the dialog closed");
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    outcome = installedCount > 0 ? Outcome.Installed : Outcome.Cancelled;
                    _app.RequestStop();
                });
            }
            catch (Exception ex)
            {
                _logger.Error("install.picker", ex.Message);
                operation.InvokeTerminalIfActive(() =>
                {
                    operation.Release();
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    outcome = installedCount > 0 ? Outcome.Installed : Outcome.Failed;
                    status.Text = $" install failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
                    RefreshValidity();
                    SetOperationControlsEnabled(true);
                });
            }
        }

        installButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;

            if (outcome == Outcome.Installed)
            {
                operation.TryStart(async token =>
                {
                    try
                    {
                        if (_browseInstalled is null) throw new IOException("Browse Installed manually; no repository return is available in this context.");
                        var found = await _browseInstalled(token).ConfigureAwait(false);
                        operation.InvokeTerminalIfActive(() =>
                        {
                            if (token.IsCancellationRequested || dialog.StopRequested || !dialog.IsRunning || !ReferenceEquals(_app.TopRunnableView, dialog)) return;
                            if (_canPublish?.Invoke(found) != true) { operation.Release(); status.Text = "Inventory or context changed; refresh Installed."; return; }
                            handoff = found; _app.RequestStop();
                        });
                    }
                    catch (Exception ex)
                    {
                        operation.InvokeTerminalIfActive(() => { operation.Release(); status.Text = InstallFailureDetails.Literal(ex.Message); cancelButton.SetFocus(); });
                    }
                });
                return;
            }
            var error = CurrentValidationError();
            if (error is not null)
            {
                status.Text = $" {error}";
                return;
            }

            if (!_contextCurrent()) { status.Text = "Installation context changed; reopen this dialog."; return; }
            var reviewed = placement.Current;
            placement.Cancel();
            var checkedNames = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < skillBoxes.Length; i++)
            {
                if (skillBoxes[i].Value == CheckState.Checked)
                {
                    checkedNames.Add(_skills[i].Name);
                }
            }

            var plan = GhSkillInstallService.BuildInstallPlan(_skills, checkedNames);
            if (plan.IsEmpty)
            {
                status.Text = " select at least one skill";
                return;
            }

            var selectedAgentIds = new List<string>();
            for (var i = 0; i < entries.Length; i++)
            {
                if (agentBoxes[i].Value == CheckState.Checked)
                {
                    selectedAgentIds.Add(entries[i].GhId);
                }
            }

            var baseOptions = InstallConfirmModal.BuildOptionsFromSelection(
                scopeSelector.Value ?? 0,
                customPathField.Text.ToString() ?? string.Empty,
                selectedAgentIds,
                installAll: plan.UseAll) with
            {
                AllowHiddenDirs = _request.AllowHiddenDirs,
                ReviewedDestinations = reviewed?.Roots.Count > 0 ? reviewed.Roots : null,
                ReviewedContext = reviewed?.ContextKey,
            };

            spinner.Visible = true;
            spinner.AutoSpin = true;
            SetOperationControlsEnabled(false);
            status.Text = plan.UseAll
                ? $" installing all {_skills.Length} skills…"
                : $" installing 1/{plan.SkillNames.Length}: {plan.SkillNames[0]}…";
            operation.TryStart(token => RunInstallAsync(plan, baseOptions, token));
        };

        cancelButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            {
                if (operation.CurrentOwnership == ModalOperationTracker.Ownership.Running)
                {
                    operation.Cancel();
                    status.Text = " canceling install…";
                }
                else
                {
                    status.Text = " finishing install…";
                }
                return;
            }
            if (outcome != Outcome.Installed) outcome = Outcome.Cancelled;
            _app.RequestStop();
        };

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
                {
                    if (operation.CurrentOwnership == ModalOperationTracker.Ownership.Running)
                    {
                        operation.Cancel();
                        status.Text = " canceling install…";
                    }
                    else
                    {
                        status.Text = " finishing install…";
                    }
                    return;
                }
                if (outcome != Outcome.Installed) outcome = Outcome.Cancelled;
                _app.RequestStop();
            }
            else if (operation.CurrentOwnership == ModalOperationTracker.Ownership.None
                     && !customPathField.HasFocus && key.AsRune.Value is 'a' or 'A')
            {
                key.Handled = true;
                SetAll(CheckState.Checked);
            }
            else if (operation.CurrentOwnership == ModalOperationTracker.Ownership.None
                     && !customPathField.HasFocus && key.AsRune.Value is 'n' or 'N')
            {
                key.Handled = true;
                SetAll(CheckState.UnChecked);
            }
        };

        dialog.Add(header, listFrame, scopeLabel, scopeSelector, customPathLabel, customPathField,
            agentsLabel, agentsView);
        dialog.Add(status, spinner, installButton, placementButton, detailsButton, cancelButton);

        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName,
            dialog, header, listFrame, scopeLabel, scopeSelector,
            customPathLabel, customPathField, agentsLabel, agentsView, status, spinner,
            installButton, cancelButton);
        foreach (var box in skillBoxes) TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, box);
        foreach (var box in agentBoxes) TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, box);

        RefreshValidity();
        status.Text = "Preparing captured scope and agent observations…";
        operation.TryStart(async token =>
        {
            try
            {
                using var deadline = new SkillView.Threading.CancellationSource(token, TimeSpan.FromSeconds(15),
                    SkillView.Threading.CancellationCallbackReporter.For(_logger, "install preparation"));
                var preparationToken = deadline.Token;
                var detected = InstallAgentSelection.Detect(_runner);
                var project = await GhPlacementCatalog.ProjectRootAsync(_runner, new(), _runner.EffectiveWorkingDirectory, preparationToken).ConfigureAwait(false);
                var projectSelected = InstallAgentCatalog.HasProjectScopeCandidate(project);
                operation.InvokeTerminalIfActive(() =>
                {
                    if (token.IsCancellationRequested) { dialog.RequestStop(); return; }
                    agentSelection.SetDetected(detected);
                    scopeSelector.Value = projectSelected ? 0 : 1;
                    scopeSelector.FocusedItem = scopeSelector.Value!.Value;
                    scopeSelector.Enabled = agentsView.Enabled = true;
                    preparing = false;
                    operation.Release(); RefreshValidity(); installButton.SetFocus();
                });
            }
            catch (Exception ex)
            {
                operation.InvokeTerminalIfActive(() => { operation.Release(); status.Text = "Preparation failed: " + InstallFailureDetails.Literal(ex.Message); });
            }
        });
        cancelButton.SetFocus();
        try
        {
            _app.Run(dialog);
        }
        finally
        {
            operation.Cancel();
        }

        operation.Dispose();
        return new Result(outcome, installedCount, failedCount, firstError, _attempts.Last?.Summary, handoff);
    }
}
