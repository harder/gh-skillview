using System.Collections.Immutable;
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

    private readonly IApplication _app;
    private readonly GhSkillInstallService _install;
    private readonly Logger _logger;
    private readonly string _ghPath;
    private readonly CapabilityProfile _capabilities;
    private readonly InstallRequest _request;

    internal InstallConfirmModal(
        IApplication app,
        GhSkillInstallService install,
        Logger logger,
        string ghPath,
        CapabilityProfile capabilities,
        InstallRequest request)
    {
        _app = app;
        _install = install;
        _logger = logger;
        _ghPath = ghPath;
        _capabilities = capabilities;
        _request = request;
    }

    internal Result Show()
    {
        var outcome = Outcome.Cancelled;
        InstallResult? installResult = null;

        var dialog = new Dialog
        {
            Title = $" Install {_request.SkillName ?? _request.Repo} ",
            Width = Dim.Percent(60),
            Height = 18,
        };
        dialog.SchemeName = SchemeNames.Dialog;

        var repoLabel = new Label
        {
            X = 1, Y = 0,
            Text = $"Repo:  {_request.Repo}{(string.IsNullOrEmpty(_request.SkillName) ? "" : " · " + _request.SkillName)}",
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
        scopeSelector.ValueChanged += (_, _) =>
        {
            var isCustom = scopeSelector.Value == 2;
            customPathLabel.Visible = isCustom;
            customPathField.Visible = isCustom;
        };

        var agentsLabel = new Label { X = 1, Y = 6, Text = "Agents:" };
        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var preChecked = InstallAgentCatalog.DetectInstalledGhIds(home ?? string.Empty);
        var entries = InstallAgentCatalog.Entries;
        var agentBoxes = new CheckBox[entries.Length];
        var col = 9;
        var row = 6;
        const int colWidth = 14;
        var perRow = 4;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var box = new CheckBox
            {
                X = col + (i % perRow) * colWidth,
                Y = row + (i / perRow),
                Text = entry.Label,
                Value = preChecked.Contains(entry.GhId) ? CheckState.Checked : CheckState.UnChecked,
            };
            agentBoxes[i] = box;
        }

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
        };
        var cancelButton = new Button
        {
            X = Pos.Center() + 4, Y = Pos.AnchorEnd(1),
            Text = "Cancel",
        };

        installButton.Accepting += async (_, ev) =>
        {
            ev.Handled = true;
            if (spinner.Visible) return;
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
                    entries);
                installResult = await _install.InstallAsync(
                    _ghPath,
                    _request.Repo,
                    _request.SkillName,
                    _capabilities,
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
                        installButton.Enabled = true;
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
                    installButton.Enabled = true;
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
                   agentsLabel);
        foreach (var box in agentBoxes) dialog.Add(box);
        dialog.Add(status, spinner, installButton, advancedButton, cancelButton);

        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName,
            dialog, repoLabel, scopeLabel, scopeSelector,
            customPathLabel, customPathField,
            agentsLabel, status, spinner,
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
        ImmutableArray<InstallAgentCatalog.Entry> entries)
    {
        var selectedAgents = new List<string>();
        for (var i = 0; i < entries.Length; i++)
        {
            if (i < agentBoxes.Count && agentBoxes[i].Value == CheckState.Checked)
            {
                selectedAgents.Add(entries[i].GhId);
            }
        }

        return BuildOptionsFromSelection(scopeIndex, customPath, selectedAgents);
    }

    /// Pure mapping from compact-modal field state to a
    /// <see cref="GhSkillInstallService.Options"/>. Extracted so callers and
    /// tests don't need to construct Terminal.Gui CheckBox views.
    /// scopeIndex: 0=Project, 1=User, 2=Custom; for index 2, customPath
    /// becomes the install path. Empty agent list yields null (no --agent
    /// flags emitted at all).
    internal static GhSkillInstallService.Options BuildOptionsFromSelection(
        int scopeIndex,
        string customPath,
        IReadOnlyList<string> selectedAgentIds)
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

        return new GhSkillInstallService.Options(
            Agents: selectedAgentIds.Count > 0 ? selectedAgentIds : null,
            Scope: scope,
            Path: path);
    }
}
