using System.Collections.Immutable;
using System.Drawing;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Per-repo discovery picker. Given a repo's already-discovered skills
/// (from `gh skill install <repo>` run non-interactively — gh ≥ 2.95.0,
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

    internal sealed record Result(Outcome Outcome, int InstalledCount, int FailedCount, string? FirstError);

    // Fixed visible height of the scrollable agent-checkbox grid — matches
    // the 3-row budget the previous fixed AnchorEnd(6)..(4) layout had before
    // colliding with the status row at AnchorEnd(3).
    private const int AgentsVisibleRows = 3;

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
        ImmutableArray<RepoSkill> skills)
    {
        _app = app;
        _install = install;
        _logger = logger;
        _ghPath = ghPath;
        _request = request;
        _skills = skills;
    }

    internal Result Show()
    {
        var outcome = Outcome.Cancelled;
        var installedCount = 0;
        var failedCount = 0;
        string? firstError = null;

        var dialog = new Dialog
        {
            Title = $" Install skills from {_request.Repo} ",
            Width = Dim.Percent(70),
            Height = Dim.Percent(85),
        };
        dialog.SchemeName = SchemeNames.Dialog;

        var header = new Label
        {
            X = 1, Y = 0,
            Text = $"Select skills to install — {_skills.Length} found (Space toggles · A all · N none):",
        };

        // Scrollable checklist of discovered skills, pre-checked. The frame
        // leaves the bottom rows for scope/agents/status/buttons.
        var listFrame = new FrameView
        {
            X = 1, Y = 1,
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
                X = 0, Y = i,
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
            X = 9, Y = Pos.AnchorEnd(9),
            Orientation = Orientation.Horizontal,
            Labels = new List<string> { "Project", "User (global)", "Custom path" },
            Value = InstallAgentCatalog.HasProjectScopeCandidate(Environment.CurrentDirectory) ? 0 : 1,
        };
        var customPathLabel = new Label { X = 1, Y = Pos.AnchorEnd(8), Text = "Path:", Visible = false };
        var customPathField = new TextField
        {
            X = 9, Y = Pos.AnchorEnd(8), Width = Dim.Fill(2),
            Text = string.Empty, Visible = false,
        };
        TuiHelpers.ConfigureTextInput(customPathField, SkillViewStyling.DialogSchemeName);

        var agentsLabel = new Label { X = 1, Y = Pos.AnchorEnd(6), Text = "Agents:" };
        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var preChecked = InstallAgentCatalog.DetectInstalledGhIds(home ?? string.Empty);
        var entries = InstallAgentCatalog.Entries;
        var agentsView = new View
        {
            X = 9, Y = Pos.AnchorEnd(6), Width = Dim.Fill(2), Height = AgentsVisibleRows,
        };
        agentsView.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        var agentGrid = AgentCheckboxGrid.Build(entries, preChecked, perRow: 4);
        var agentBoxes = agentGrid.Boxes;
        foreach (var box in agentBoxes) agentsView.Add(box);
        agentsView.SetContentSize(new Size(agentGrid.ContentWidth, agentGrid.RowCount));

        var status = new Label
        {
            X = 1, Y = Pos.AnchorEnd(3), Width = Dim.Fill(2), Text = " ready",
        };
        var spinner = new SpinnerView
        {
            X = Pos.AnchorEnd(2), Y = Pos.AnchorEnd(3),
            Width = 1, Height = 1, Visible = false, AutoSpin = false,
            Style = new SpinnerStyle.Dots(),
        };

        var installButton = new Button
        {
            X = Pos.Center() - 14, Y = Pos.AnchorEnd(1), Text = "Install", IsDefault = true,
        };
        var cancelButton = new Button
        {
            X = Pos.Center() + 4, Y = Pos.AnchorEnd(1), Text = "Cancel",
        };

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
            installButton.Enabled = !spinner.Visible && error is null;
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

        installButton.Accepting += async (_, ev) =>
        {
            ev.Handled = true;
            if (spinner.Visible) return;

            var error = CurrentValidationError();
            if (error is not null)
            {
                status.Text = $" {error}";
                return;
            }

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

            spinner.Visible = true;
            spinner.AutoSpin = true;
            installButton.Enabled = false;

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
            };

            try
            {
                if (plan.UseAll)
                {
                    status.Text = $" installing all {_skills.Length} skills…";
                    var r = await _install.InstallAsync(_ghPath, _request.Repo, null, baseOptions).ConfigureAwait(false);
                    if (r.Succeeded) installedCount = _skills.Length;
                    else { failedCount = _skills.Length; firstError ??= r.ErrorMessage; }
                }
                else
                {
                    for (var i = 0; i < plan.SkillNames.Length; i++)
                    {
                        var name = plan.SkillNames[i];
                        var idx = i;
                        _app.Invoke(() => status.Text = $" installing {idx + 1}/{plan.SkillNames.Length}: {name}…");
                        var r = await _install.InstallAsync(_ghPath, _request.Repo, name, baseOptions).ConfigureAwait(false);
                        if (r.Succeeded) installedCount++;
                        else { failedCount++; firstError ??= r.ErrorMessage; }
                    }
                }

                _app.Invoke(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    if (failedCount == 0)
                    {
                        outcome = Outcome.Installed;
                        _app.RequestStop();
                    }
                    else if (installedCount > 0)
                    {
                        // Partial success — report and let the user close.
                        outcome = Outcome.Installed;
                        status.Text = $" installed {installedCount}, {failedCount} failed — see logs (l)";
                        installButton.Enabled = false;
                    }
                    else
                    {
                        outcome = Outcome.Failed;
                        var snippet = TuiHelpers.ErrorSnippet(firstError);
                        status.Text = snippet.Length > 0
                            ? $" install failed: {snippet}"
                            : " install failed — see logs (l)";
                        RefreshValidity();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("install.picker", ex.Message);
                _app.Invoke(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    outcome = installedCount > 0 ? Outcome.Installed : Outcome.Failed;
                    status.Text = $" install failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
                    RefreshValidity();
                });
            }
        };

        cancelButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            outcome = Outcome.Cancelled;
            _app.RequestStop();
        };

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                outcome = Outcome.Cancelled;
                _app.RequestStop();
            }
            else if (!customPathField.HasFocus && key.AsRune.Value is 'a' or 'A')
            {
                key.Handled = true;
                SetAll(CheckState.Checked);
            }
            else if (!customPathField.HasFocus && key.AsRune.Value is 'n' or 'N')
            {
                key.Handled = true;
                SetAll(CheckState.UnChecked);
            }
        };

        dialog.Add(header, listFrame, scopeLabel, scopeSelector, customPathLabel, customPathField,
            agentsLabel, agentsView);
        dialog.Add(status, spinner, installButton, cancelButton);

        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName,
            dialog, header, listFrame, scopeLabel, scopeSelector,
            customPathLabel, customPathField, agentsLabel, agentsView, status, spinner,
            installButton, cancelButton);
        foreach (var box in skillBoxes) TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, box);
        foreach (var box in agentBoxes) TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, box);

        RefreshValidity();
        _app.Run(dialog);
        dialog.Dispose();

        return new Result(outcome, installedCount, failedCount, firstError);
    }
}
