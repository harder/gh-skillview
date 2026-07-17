using System.Collections.Immutable;
using System.Drawing;
using SkillView.Diagnostics;
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

/// Compact install dialog matching winget-tui's "press i, see one screen,
/// done" flow. Shows the skill, Scope radio (Project / User / Custom), agent
/// multi-checkbox row pre-populated from what's installed in the user's home
/// directory, and three buttons: Install · Advanced… · Cancel.
///
/// Advanced… escalates to the existing multi-step <see cref="InstallScreen"/>
/// wizard, preserving the entered values via the original
/// <see cref="InstallRequest"/>.
///
/// In `installAll` mode the same screen drives `gh skill install <repo> --all`
/// (gh 2.94): the skill name is dropped, the title/labels say "all skills",
/// and Advanced… is hidden (there's no single-skill wizard to escalate to).
///
/// This modal renders as an overlay (app.Run(dialog) / app.RequestStop()),
/// matching the new dialog pattern established by <see cref="HelpOverlay"/>.
internal sealed class InstallConfirmModal
{
    internal enum Outcome
    {
        Cancelled,
        Installed,
        EscalateToAdvanced,
        Failed,
    }

    internal sealed record Result(Outcome Outcome, InstallResult? Install);

    // Fixed visible height of the scrollable agent-checkbox grid; the
    // catalog is long enough now (gh skill install --agent lists ~47) that
    // it must scroll rather than grow the dialog.
    private const int AgentsVisibleRows = 4;

    private readonly IApplication _app;
    private readonly GhSkillInstallService _install;
    private readonly Logger _logger;
    private readonly string _ghPath;
    private readonly InstallRequest _request;
    private readonly bool _installAll;

    internal InstallConfirmModal(
        IApplication app,
        GhSkillInstallService install,
        Logger logger,
        string ghPath,
        InstallRequest request,
        bool installAll = false)
    {
        _app = app;
        _install = install;
        _logger = logger;
        _ghPath = ghPath;
        _request = request;
        _installAll = installAll;
    }

    internal Result Show()
    {
        var outcome = Outcome.Cancelled;
        InstallResult? installResult = null;

        var dialog = new Dialog
        {
            Title = _installAll
                ? $" Install all skills from {_request.Repo} "
                : $" Install {_request.SkillName ?? _request.Repo} ",
            Width = Dim.Percent(60),
            Height = 18,
        };
        dialog.SchemeName = SchemeNames.Dialog;

        var repoLabel = new Label
        {
            X = 1, Y = 0,
            Text = _installAll
                ? $"Repo:  {_request.Repo} · ALL skills in repo"
                : $"Repo:  {_request.Repo}{(string.IsNullOrEmpty(_request.SkillName) ? "" : " · " + _request.SkillName)}",
        };

        var scopeLabel = new Label { X = 1, Y = 2, Text = "Scope:" };
        var scopeSelector = new OptionSelector
        {
            X = 9, Y = 2,
            Orientation = Orientation.Horizontal,
            Labels = new List<string> { "Project", "User (global)", "Custom path" },
            // Pick a sensible default: Project if cwd has a known agent seed,
            // otherwise User.
            Value = InstallAgentCatalog.HasProjectScopeCandidate(Environment.CurrentDirectory) ? 0 : 1,
        };
        var customPathLabel = new Label
        {
            X = 1, Y = 4, Text = "Path:", Visible = false,
        };
        var customPathField = new TextField
        {
            X = 9, Y = 4, Width = Dim.Fill(2),
            Text = string.Empty, Visible = false,
        };
        TuiHelpers.ConfigureTextInput(customPathField, SkillViewStyling.DialogSchemeName);
        var agentsLabel = new Label { X = 1, Y = 6, Text = "Agents:" };
        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var preChecked = InstallAgentCatalog.DetectInstalledGhIds(home ?? string.Empty);
        var entries = InstallAgentCatalog.Entries;
        var agentsView = new View
        {
            X = 9, Y = 6, Width = Dim.Fill(2), Height = AgentsVisibleRows,
        };
        agentsView.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        var agentGrid = AgentCheckboxGrid.Build(entries, preChecked, perRow: 3);
        var agentBoxes = agentGrid.Boxes;
        foreach (var box in agentBoxes) agentsView.Add(box);
        agentsView.SetContentSize(new Size(agentGrid.ContentWidth, agentGrid.RowCount));

        var status = new Label
        {
            X = 1, Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(2),
            Text = " ready",
        };
        var spinner = new SpinnerView
        {
            X = Pos.AnchorEnd(2), Y = Pos.AnchorEnd(3),
            Width = 1, Height = 1, Visible = false, AutoSpin = false,
            Style = new SpinnerStyle.Dots(),
        };

        var installButton = new Button
        {
            X = Pos.Center() - 22, Y = Pos.AnchorEnd(1),
            Text = "Install",
            IsDefault = true,
        };
        var advancedButton = new Button
        {
            X = Pos.Center() - 10, Y = Pos.AnchorEnd(1),
            Text = "Advanced…",
            // The advanced wizard is single-skill; there's nothing to escalate
            // to for an install-all, so hide it in that mode.
            Visible = !_installAll,
            Enabled = !_installAll,
        };
        var cancelButton = new Button
        {
            X = Pos.Center() + 4, Y = Pos.AnchorEnd(1),
            Text = "Cancel",
        };

        string? CurrentValidationError() => ValidateSelection(
            scopeSelector.Value ?? 0,
            customPathField.Text.ToString() ?? string.Empty);

        WireValidation(scopeSelector, customPathLabel, customPathField, installButton, status, spinner);

        installButton.Accepting += async (_, ev) =>
        {
            ev.Handled = true;
            if (spinner.Visible) return;

            var validationError = CurrentValidationError();
            if (validationError is not null)
            {
                status.Text = $" {validationError}";
                return;
            }

            spinner.Visible = true;
            spinner.AutoSpin = true;
            installButton.Enabled = false;
            advancedButton.Enabled = false;
            status.Text = $" installing {_request.Repo}…";

            try
            {
                var options = BuildOptions(
                    scopeSelector.Value ?? 0,
                    customPathField.Text.ToString() ?? string.Empty,
                    agentBoxes,
                    entries,
                    _installAll);
                installResult = await _install.InstallAsync(
                    _ghPath,
                    _request.Repo,
                    _request.SkillName,
                    options).ConfigureAwait(false);

                _app.Invoke(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    if (installResult.Succeeded)
                    {
                        outcome = Outcome.Installed;
                        _app.RequestStop();
                    }
                    else
                    {
                        outcome = Outcome.Failed;
                        var snippet = TuiHelpers.ErrorSnippet(installResult.ErrorMessage);
                        status.Text = snippet.Length > 0
                            ? $" install failed (exit {installResult.ExitCode}): {snippet}"
                            : $" install failed (exit {installResult.ExitCode}) — see logs";
                        installButton.Enabled = CurrentValidationError() is null;
                        advancedButton.Enabled = true;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("install.compact", ex.Message);
                _app.Invoke(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    outcome = Outcome.Failed;
                    status.Text = $" install failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
                    installButton.Enabled = CurrentValidationError() is null;
                    advancedButton.Enabled = true;
                });
            }
        };

        advancedButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            outcome = Outcome.EscalateToAdvanced;
            _app.RequestStop();
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
        };

        dialog.Add(repoLabel, scopeLabel, scopeSelector, customPathLabel, customPathField,
                   agentsLabel, agentsView);
        dialog.Add(status, spinner, installButton, advancedButton, cancelButton);

        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName,
            dialog, repoLabel, scopeLabel, scopeSelector,
            customPathLabel, customPathField,
            agentsLabel, agentsView, status, spinner,
            installButton, advancedButton, cancelButton);
        foreach (var box in agentBoxes)
        {
            TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, box);
        }

        _app.Run(dialog);
        dialog.Dispose();

        return new Result(outcome, installResult);
    }

    private static GhSkillInstallService.Options BuildOptions(
        int scopeIndex,
        string customPath,
        IReadOnlyList<CheckBox> agentBoxes,
        ImmutableArray<InstallAgentCatalog.Entry> entries,
        bool installAll = false)
    {
        var selectedAgents = new List<string>();
        for (var i = 0; i < entries.Length; i++)
        {
            if (i < agentBoxes.Count && agentBoxes[i].Value == CheckState.Checked)
            {
                selectedAgents.Add(entries[i].GhId);
            }
        }

        return BuildOptionsFromSelection(scopeIndex, customPath, selectedAgents, installAll);
    }

    /// Pure mapping from compact-modal field state to a
    /// <see cref="GhSkillInstallService.Options"/>. Extracted so callers and
    /// tests don't need to construct Terminal.Gui CheckBox views.
    /// scopeIndex: 0=Project, 1=User, 2=Custom; for index 2, customPath
    /// becomes the install path. An empty agent list defaults to `universal`
    /// (gh 2.96's shared, agent-agnostic target — installs to `.agents/skills`
    /// rather than a specific agent's home dir) — unless a custom `--dir` path
    /// is set, since gh's `--dir` overrides `--agent` entirely and a default
    /// there would be meaningless.
    internal static readonly string[] DefaultAgents = ["universal"];

    internal static GhSkillInstallService.Options BuildOptionsFromSelection(
        int scopeIndex,
        string customPath,
        IReadOnlyList<string> selectedAgentIds,
        bool installAll = false)
    {
        var scope = scopeIndex switch
        {
            0 => "project",
            1 => "user",
            _ => null,
        };

        var path = scopeIndex == 2 && !string.IsNullOrWhiteSpace(customPath)
            ? customPath.Trim()
            : null;

        var agents = selectedAgentIds.Count > 0
            ? selectedAgentIds
            : path is null ? DefaultAgents : null;

        return new GhSkillInstallService.Options(
            Agents: agents,
            Scope: scope,
            Path: path,
            All: installAll);
    }

    internal static string? ValidateSelection(int scopeIndex, string customPath) =>
        scopeIndex == 2 && string.IsNullOrWhiteSpace(customPath)
            ? "enter a custom install path"
            : null;

    internal static void WireValidation(
        OptionSelector scopeSelector,
        Label customPathLabel,
        TextField customPathField,
        Button installButton,
        Label status,
        SpinnerView spinner)
    {
        void RefreshInstallValidity()
        {
            var validationError = ValidateSelection(
                scopeSelector.Value ?? 0,
                customPathField.Text.ToString() ?? string.Empty);
            installButton.Enabled = !spinner.Visible && validationError is null;
            if (!spinner.Visible)
            {
                status.Text = validationError is null ? " ready" : $" {validationError}";
            }
        }

        scopeSelector.ValueChanged += (_, _) =>
        {
            var isCustom = scopeSelector.Value == 2;
            customPathLabel.Visible = isCustom;
            customPathField.Visible = isCustom;
            RefreshInstallValidity();
        };
        customPathField.TextChanged += (_, _) => RefreshInstallValidity();

        RefreshInstallValidity();
    }
}
