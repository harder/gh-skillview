using System.Collections.Immutable;
using System.Drawing;
using SkillView.Diagnostics;
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

    internal sealed record Result(Outcome Outcome, InstallResult? Install, InstalledInstallHandoff? Handoff = null);

    // Fixed visible height of the scrollable agent-checkbox grid; the
    // catalog is long enough now (gh skill install --agent lists ~47) that
    // it must scroll rather than grow the dialog.
    private const int AgentsVisibleRows = 4;
    internal const int MaxBasePathDisplayCharacters = 4096;

    private readonly IApplication _app;
    private readonly GhSkillInstallService _install;
    private readonly Logger _logger;
    private readonly string _ghPath;
    private readonly InstallRequest _request;
    private readonly bool _installAll;
    private readonly ProcessRunner _runner;
    private readonly Func<InstallNavigationEvidence, CancellationToken, Task<InstalledInstallHandoff>>? _findInstalled;
    private readonly Func<InstalledInstallHandoff, bool>? _canPublish;

    internal InstallConfirmModal(
        IApplication app,
        GhSkillInstallService install,
        Logger logger,
        string ghPath,
        InstallRequest request,
        bool installAll = false,
        Func<InstallNavigationEvidence, CancellationToken, Task<InstalledInstallHandoff>>? findInstalled = null,
        Func<InstalledInstallHandoff, bool>? canPublish = null)
    {
        _app = app;
        _runner = install.CaptureContext();
        _install = new GhSkillInstallService(_runner, logger);
        _logger = logger;
        _ghPath = ghPath;
        _request = request;
        _installAll = installAll;
        _findInstalled = findInstalled;
        _canPublish = canPublish;
    }

    internal Result Show(Action<Dialog, ModalOperationTracker>? readyForTests = null, Action<Action>? invokeForTests = null)
    {
        var outcome = Outcome.Cancelled;
        InstallResult? installResult = null;
        InstalledInstallHandoff? handoff = null;
        var operationName = "project lookup";
        var preparing = true;
        var preparationCancelled = false;

        using var dialog = new Dialog
        {
            Title = _installAll
                ? $" Install all skills from {_request.Repo} "
                : $" Install {_request.SkillName ?? _request.Repo} ",
            Width = Dim.Func(_ => Math.Min(90, _app.Screen.Width - 4)),
            Height = 18,
        };
        dialog.SchemeName = SchemeNames.Dialog;
        using var operation = new ModalOperationTracker(invokeForTests ?? _app.Invoke, _logger, "install.compact.ui");

        var repoLabel = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Text = _installAll
                ? $"Repo:  {_request.Repo} · ALL skills in repo"
                : $"Repo:  {_request.Repo}{(string.IsNullOrEmpty(_request.SkillName) ? "" : " · " + _request.SkillName)}",
        };

        var scopeLabel = new Label { X = 1, Y = 2, Text = "Scope:" };
        var scopeSelector = new OptionSelector
        {
            X = 9,
            Y = 2,
            Orientation = Orientation.Horizontal,
            Labels = new List<string> { "Project", "User", "Custom path" },
            // Resolve the initial choice under the same frozen context as gh.
            Enabled = false,
            Value = 1,
            FocusedItem = 1,
        };
        var customPathLabel = new Label
        {
            X = 1,
            Y = 4,
            Text = "Path:",
            Visible = false,
        };
        var customPathField = new TextField
        {
            X = 9,
            Y = 4,
            Width = Dim.Fill(2),
            Text = string.Empty,
            Visible = false,
        };
        TuiHelpers.ConfigureTextInput(customPathField, SkillViewStyling.DialogSchemeName);
        var agentsLabel = new Label { X = 1, Y = 6, Text = "Agents:" };
        var home = _runner.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        home = Path.GetFullPath(home, _runner.EffectiveWorkingDirectory);
        string? projectRoot = null;
        var scopeDescription = new Label { X = 1, Y = 3, Width = Dim.Fill(2), Height = 1 };
        var baseLabel = new Label { X = 1, Y = 4, Text = "Base:" };
        var basePath = new TextField { X = 9, Y = 4, Width = Dim.Fill(2), Text = FormatBasePath(home), ReadOnly = true };
        TuiHelpers.ConfigureTextInput(basePath, SkillViewStyling.DialogSchemeName);
        void RefreshScopeDescription()
        {
            var custom = scopeSelector.Value == 2;
            baseLabel.Visible = !custom;
            basePath.Visible = !custom;
            basePath.Text = FormatBasePath(scopeSelector.Value == 0 ? projectRoot ?? "Unresolved; placement must pass install checks" : home);
            scopeDescription.Text = scopeSelector.Value switch
            {
                0 => "Project: available to agents in this project.",
                1 => "User: your home, across projects.",
                _ => "Custom: install directly in the folder below.",
            };
        }
        scopeSelector.ValueChanged += (_, _) => RefreshScopeDescription();
        RefreshScopeDescription();
        string? ConfigPath(string key) => _runner.GetEffectiveEnvironmentVariable(key) is { Length: > 0 } value
            ? Path.GetFullPath(value, _runner.EffectiveWorkingDirectory) : null;
        var preChecked = InstallAgentCatalog.DetectInstalledGhIds(
            home,
            ConfigPath("PI_CODING_AGENT_DIR"), ConfigPath("CLAUDE_CONFIG_DIR"));
        var entries = InstallAgentCatalog.Entries;
        var agentsView = new View
        {
            X = 9,
            Y = 6,
            Width = Dim.Fill(2),
            Height = AgentsVisibleRows,
            Enabled = false,
        };
        agentsView.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        var agentGrid = AgentCheckboxGrid.Build(entries, preChecked, perRow: 3);
        var agentBoxes = agentGrid.Boxes;
        foreach (var box in agentBoxes) agentsView.Add(box);
        agentsView.SetContentSize(new Size(agentGrid.ContentWidth, agentGrid.RowCount));

        var status = new Label
        {
            X = 3,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(2),
            Text = " ready",
            CanFocus = true,
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

        var placementHint = new Label { X = 1, Y = 11, Width = Dim.Fill(2), Height = 1 };
        void RefreshPlacementHint()
        {
            placementHint.Text = scopeSelector.Value == 2
                ? "Custom folder overrides scope and agent folders."
                : agentBoxes.Any(box => box.Value == CheckState.Checked)
                    ? "Selected agents use their gh installation folders."
                    : "No agents checked: Universal, shared .agents/skills folder.";
        }
        scopeSelector.ValueChanged += (_, _) => RefreshPlacementHint();
        foreach (var box in agentBoxes) box.ValueChanged += (_, _) => RefreshPlacementHint();
        RefreshPlacementHint();

        var installButton = new SingleRowButton
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Text = "Install",
            IsDefault = true,
        };
        var advancedButton = new SingleRowButton
        {
            X = Pos.Right(installButton) + 2,
            Y = Pos.AnchorEnd(1),
            Text = "Advanced…",
            // The advanced wizard is single-skill; there's nothing to escalate
            // to for an install-all, so hide it in that mode.
            Visible = !_installAll,
            Enabled = !_installAll,
        };
        var cancelButton = new SingleRowButton
        {
            X = _installAll ? Pos.Right(installButton) + 2 : Pos.Right(advancedButton) + 2,
            Y = Pos.AnchorEnd(1),
            Text = "Cancel",
        };

        // Keep Enter local to the editor. Selector/checkbox Accept commands
        // otherwise bubble into Dialog's implicit close behavior.
        scopeSelector.KeyBindings.ReplaceCommands(KeyCode.Enter, Command.Activate);
        foreach (var box in scopeSelector.SubViews.OfType<CheckBox>().Concat(agentBoxes))
            box.KeyBindings.ReplaceCommands(KeyCode.Enter, Command.Activate);
        foreach (var field in new[] { customPathField, basePath })
            field.KeyDown += (_, key) =>
            {
                if (key.KeyCode != KeyCode.Enter) return;
                key.Handled = true;
                installButton.InvokeCommand(Command.Accept);
            };

        string? CurrentValidationError() => ValidateSelection(
            scopeSelector.Value ?? 0,
            customPathField.Text.ToString() ?? string.Empty);

        WireValidation(scopeSelector, customPathLabel, customPathField, installButton, status, spinner);

        bool FinishPreparation(string? project, bool hasProject)
        {
            if (preparationCancelled || operation.Token.IsCancellationRequested)
            {
                outcome = Outcome.Cancelled;
                _app.RequestStop();
                return false;
            }
            preparing = false;
            projectRoot = project;
            scopeSelector.Value = hasProject ? 0 : 1;
            RefreshScopeDescription();
            scopeSelector.FocusedItem = scopeSelector.Value!.Value;
            scopeSelector.Enabled = true;
            agentsView.Enabled = true;
            customPathField.Enabled = true;
            spinner.Visible = false;
            spinner.AutoSpin = false;
            installButton.Enabled = true;
            advancedButton.Enabled = !_installAll;
            status.Text = " ready";
            operation.Release();
            // The loading status owns initial focus. Preserve any focus the
            // user moved to Cancel or the base field while lookup was pending.
            if (status.HasFocus || dialog.Focused is null) installButton.SetFocus();
            status.CanFocus = false;
            return true;
        }

        status.KeyDown += (_, key) =>
        {
            if (preparing && key.KeyCode == KeyCode.Enter) key.Handled = true;
        };

        // Preparation owns the modal just like installation. Enter cannot
        // submit a provisional choice, and Esc cancels its child Git probe.
        installButton.Enabled = false;
        advancedButton.Enabled = false;
        spinner.Visible = true;
        spinner.AutoSpin = true;
        status.Text = " checking project scope…";
        operation.TryStart(async token =>
        {
            try
            {
                var project = await GhPlacementCatalog.ProjectRootAsync(
                    _runner, new(), _runner.EffectiveWorkingDirectory, token).ConfigureAwait(false);
                var hasProject = InstallAgentCatalog.HasProjectScopeCandidate(project);
                token.ThrowIfCancellationRequested();
                operation.InvokeTerminalIfActive(() =>
                {
                    FinishPreparation(project, hasProject);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                operation.InvokeTerminalIfActive(() => _app.RequestStop());
            }
            catch (Exception ex)
            {
                _logger.Error("install.compact", ex.Message);
                operation.InvokeTerminalIfActive(() =>
                {
                    if (FinishPreparation(null, false))
                        status.Text = " project lookup failed; User or Custom available";
                });
            }
        });

        async Task RunInstallAsync(
            GhSkillInstallService.Options options,
            CancellationToken cancellationToken)
        {
            try
            {
                installResult = await _install.InstallAsync(
                    _ghPath,
                    _request.Repo,
                    _request.SkillName,
                    options,
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    if (installResult.Succeeded)
                    {
                        outcome = Outcome.Installed;
                        if (_installAll) { _app.RequestStop(); return; }
                        operation.Release();
                        dialog.Title = " Install completed ";
                        foreach (var control in new View[] { scopeLabel, scopeSelector, scopeDescription, baseLabel, basePath,
                            customPathLabel, customPathField, agentsLabel, agentsView, placementHint }) control.Visible = false;
                        dialog.Height = 9;
                        dialog.Y = Pos.Center();
                        repoLabel.Height = 1;
                        scopeDescription.Y = 1;
                        scopeDescription.Visible = true;
                        scopeDescription.Text = options.Scope switch
                        {
                            "project" => "Selected scope: Project",
                            "user" => "Selected scope: User",
                            _ => "Selected scope: Custom folder",
                        };
                        status.Y = 3;
                        status.Height = 3;
                        spinner.Y = 3;
                        status.Text = " Install completed";
                        installButton.Text = "View installed skill";
                        installButton.Enabled = true;
                        advancedButton.Visible = false;
                        cancelButton.X = Pos.Right(installButton) + 2;
                        cancelButton.Text = "Back to Discover";
                        cancelButton.Enabled = true;
                        installButton.SetFocus();
                    }
                    else
                    {
                        operation.Release();
                        outcome = Outcome.Failed;
                        var snippet = TuiHelpers.ErrorSnippet(installResult.ErrorMessage);
                        status.Text = snippet.Length > 0
                            ? $" install failed (exit {installResult.ExitCode}): {snippet}"
                            : $" install failed (exit {installResult.ExitCode}) — see logs";
                        scopeSelector.Enabled = true;
                        agentsView.Enabled = true;
                        customPathField.Enabled = true;
                        installButton.Enabled = CurrentValidationError() is null;
                        advancedButton.Enabled = !_installAll;
                        cancelButton.Enabled = true;
                    }
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("install.compact", "install canceled because the dialog closed");
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    outcome = Outcome.Cancelled;
                    _app.RequestStop();
                });
            }
            catch (Exception ex)
            {
                _logger.Error("install.compact", ex.Message);
                operation.InvokeTerminalIfActive(() =>
                {
                    operation.Release();
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    outcome = Outcome.Failed;
                    status.Text = $" install failed: {TuiHelpers.ErrorSnippet(ex.Message)}";
                    scopeSelector.Enabled = true;
                    agentsView.Enabled = true;
                    customPathField.Enabled = true;
                    installButton.Enabled = CurrentValidationError() is null;
                    advancedButton.Enabled = !_installAll;
                    cancelButton.Enabled = true;
                });
            }
        }

        async Task FindInstalledAsync(CancellationToken token)
        {
            try
            {
                if (installResult?.Navigation is not { } evidence || _findInstalled is null)
                    throw new IOException(scopeSelector.Value == 2
                        ? "Automatic navigation is not supported for custom folders. Outside the current scan, add an explicit scan root."
                        : "Automatic navigation is available for a named Discover skill with a known source path at User or Project scope.");
                var found = await _findInstalled(evidence, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                operation.InvokeTerminalIfActive(() =>
                {
                    if (token.IsCancellationRequested || dialog.StopRequested || !dialog.IsRunning || !ReferenceEquals(_app.TopRunnableView, dialog)) return;
                    if (_canPublish?.Invoke(found) != true)
                    {
                        FinishFinding("The inventory or installation context changed; refresh Installed before choosing a copy.");
                        return;
                    }
                    handoff = found;
                    _app.RequestStop();
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.Error("install.handoff", ex.Message);
                operation.InvokeTerminalIfActive(() =>
                {
                    if (!token.IsCancellationRequested) FinishFinding(TuiHelpers.ErrorSnippet(ex.Message));
                });
            }
        }

        void FinishFinding(string message)
        {
            operation.Release();
            spinner.AutoSpin = false;
            spinner.Visible = false;
            // Keep the source and selected scope visible above the explanation.
            status.Text = "Install completed; the exact installed copy could not be verified.\n" + message;
            installButton.Enabled = true;
            cancelButton.Enabled = true;
            cancelButton.SetFocus();
        }

        installButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            if (outcome == Outcome.Installed)
            {
                operationName = "finding installed skill";
                status.Text = " Finding installed skill…";
                spinner.Visible = true;
                spinner.AutoSpin = true;
                installButton.Enabled = false;
                cancelButton.SetFocus();
                operation.TryStart(FindInstalledAsync);
                return;
            }

            var validationError = CurrentValidationError();
            if (validationError is not null)
            {
                status.Text = $" {validationError}";
                return;
            }

            var options = BuildOptions(
                scopeSelector.Value ?? 0,
                customPathField.Text.ToString() ?? string.Empty,
                agentBoxes,
                entries,
                _installAll) with
            { NavigationSource = InstallNavigationEvidence.SourceAnchor(_request.Repo, _request.SourcePath) };
            operationName = "install";
            scopeSelector.Enabled = false;
            agentsView.Enabled = false;
            customPathField.Enabled = false;
            spinner.Visible = true;
            spinner.AutoSpin = true;
            installButton.Enabled = false;
            advancedButton.Enabled = false;
            cancelButton.Enabled = false;
            status.Text = $" installing {_request.Repo}…";
            operation.TryStart(token => RunInstallAsync(options, token));
        };

        advancedButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            {
                status.Text = operation.CurrentOwnership == ModalOperationTracker.Ownership.Running
                    ? $" {operationName} in progress — Esc cancels"
                    : $" finishing {operationName}…";
                return;
            }
            outcome = Outcome.EscalateToAdvanced;
            _app.RequestStop();
        };
        void CancelOrClose()
        {
            if (outcome == Outcome.Installed)
            {
                handoff = null;
                operation.Cancel();
                _app.RequestStop();
                return;
            }
            var ownership = operation.CurrentOwnership;
            if (ownership != ModalOperationTracker.Ownership.None)
            {
                if (preparing || ownership == ModalOperationTracker.Ownership.Running)
                {
                    if (preparing) preparationCancelled = true;
                    operation.Cancel();
                    status.Text = $" canceling {operationName}…";
                }
                else
                {
                    status.Text = $" finishing {operationName}…";
                }
                return;
            }
            outcome = Outcome.Cancelled;
            _app.RequestStop();
        }
        cancelButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            CancelOrClose();
        };

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                CancelOrClose();
            }
        };

        // Only the explicit actions close this review. An Accept bubbling
        // from the scope selector or base field must not dismiss an owned
        // operation (or silently cancel a review before Install is chosen).
        dialog.Accepting += (_, ev) => ev.Handled = true;

        dialog.Add(status, repoLabel, scopeLabel, scopeSelector, scopeDescription, baseLabel, basePath, customPathLabel, customPathField,
                   agentsLabel, agentsView);
        dialog.Add(placementHint, spinner, installButton, advancedButton, cancelButton);

        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName,
            dialog, repoLabel, scopeLabel, scopeSelector, scopeDescription, baseLabel, basePath,
            customPathLabel, customPathField,
            agentsLabel, agentsView, placementHint, status, spinner,
            installButton, advancedButton, cancelButton);
        foreach (var box in agentBoxes)
        {
            TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, box);
        }

        try
        {
            status.SetFocus();
            readyForTests?.Invoke(dialog, operation);
            _app.Run(dialog);
        }
        finally
        {
            operation.Cancel();
        }

        return new Result(outcome, installResult, handoff);
    }

    private static string FormatBasePath(string path)
    {
        var truncated = path.Length > MaxBasePathDisplayCharacters;
        var bounded = path[..Math.Min(path.Length, MaxBasePathDisplayCharacters - (truncated ? 1 : 0))];
        var sanitized = TerminalEscapeSanitizer.Sanitize(bounded) ?? string.Empty;
        var singleLine = new string(sanitized.Select(character => char.IsControl(character) ? '\uFFFD' : character).ToArray());
        return truncated ? singleLine + "…" : singleLine;
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
