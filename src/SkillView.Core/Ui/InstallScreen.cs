using System.Collections.Immutable;
using System.Drawing;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Request to install a skill from a search result. Carries the
/// minimum a user has selected in the results table: repo, optional
/// skill name within the repo. `InstallScreen` picks the rest of the flags
/// interactively, but the parent screen can seed obvious toggles such as
/// hidden-dir access.
public sealed record InstallRequest(string Repo, string? SkillName, bool AllowHiddenDirs = false, string? SourcePath = null);

/// Phase 4 install dialog. Consumes an `InstallRequest` (repo + skill) and
/// runs `gh skill install` with the flags the user has chosen. SkillView
/// requires gh ≥ 2.97.0, so all flags (`--upstream`, `--allow-hidden-dirs`,
/// `--from-local`, …) are always available and shown.
public sealed class InstallScreen
{
    // Known agent IDs for the multi-select checkboxes. This list is static
    // because AOT forbids reflection-based discovery, and `gh skill install
    // --help` doesn't enumerate valid agent names. Update this array when new
    // agents are added to the gh skill ecosystem.
    public static readonly string[] KnownAgents = InstallAgentCatalog.GhIds.ToArray();

    private static readonly ImmutableArray<InstallAgentCatalog.Entry> KnownAgentEntries = InstallAgentCatalog.Entries;

    // Labels are user-facing; values are the literals `gh skill install --scope`
    // accepts. User scope installs into home folders across projects.
    public static readonly (string Label, string Value)[] ScopeChoices =
    {
        ("Project", "project"),
        ("User", "user"),
        ("Custom", "custom"),
    };

    private readonly IApplication _app;
    private readonly GhSkillInstallService _install;
    private readonly Logger _logger;
    private readonly string _ghPath;
    private readonly InstallRequest _request;

    private readonly InstallAttemptRun _attempts;
    private readonly ProcessRunner _runner;
    private readonly Func<bool> _contextCurrent;
    private readonly Func<InstallNavigationEvidence, CancellationToken, Task<InstalledInstallHandoff>>? _findInstalled;
    private readonly Func<InstalledInstallHandoff, bool>? _canPublish;
    internal InstalledInstallHandoff? Handoff { get; private set; }

    internal IReadOnlyList<InstallAttemptReceipt> Attempts => _attempts.Receipts;

    public InstallResult? LastResult { get; private set; }

    public InstallScreen(IApplication app, GhSkillInstallService install, Logger logger, string ghPath, InstallRequest request)
        : this(app, install, logger, ghPath, request, new InstallAttemptRun()) { }

    internal InstallScreen(
        IApplication app,
        GhSkillInstallService install,
        Logger logger,
        string ghPath,
        InstallRequest request,
        InstallAttemptRun attempts,
        Func<InstallNavigationEvidence, CancellationToken, Task<InstalledInstallHandoff>>? findInstalled = null,
        Func<InstalledInstallHandoff, bool>? canPublish = null)
    {
        _attempts = attempts;
        _app = app;
        _runner = install.CaptureContext();
        var contextKey = InstallNavigationEvidence.Context(_runner);
        _contextCurrent = () => contextKey == InstallNavigationEvidence.Context(install.CaptureContext());
        _install = new(_runner, logger);
        _findInstalled = findInstalled;
        _canPublish = canPublish;
        _logger = logger;
        _ghPath = ghPath;
        _request = request;
    }

    public void Show() => ShowCore(null);

    internal void ShowForTests(Action<Dialog, ModalOperationTracker> ready) => ShowCore(ready);

    private void ShowCore(Action<Dialog, ModalOperationTracker>? ready)
    {
        using var dialog = new Dialog
        {
            Title = $"Install — {_request.Repo}{(_request.SkillName is null ? "" : "/" + _request.SkillName)}",
            Width = Dim.Func(_ => Math.Min(100, _app.Screen.Width - 4)),
            Height = Dim.Fill(2),
        };
        using var operation = new ModalOperationTracker(_app, _logger, "install.ui");

        var form = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(6), CanFocus = true };
        form.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        var preparing = true;
        form.FrameChanged += (_, _) => form.SetContentSize(new Size(Math.Max(1, form.Viewport.Width),
            form.GetContentSize().Height));

        // ── SOURCE ─────────────────────────────────────────────────────
        var sourceFrame = new FrameView
        {
            Title = "Source",
            CanFocus = true,
            TabStop = TabBehavior.TabStop,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = SourceFrameHeight(),
        };

        var skillLabel = new Label { Text = "Skill name :", X = 0, Y = 0 };
        var skillField = new TextField
        {
            X = 13,
            Y = 0,
            Width = 32,
            Text = _request.SkillName ?? string.Empty,
        };
        TuiHelpers.ConfigureTextInput(skillField, SkillViewStyling.DialogSchemeName);
        var skillHint = new Label
        {
            X = Pos.Right(skillField) + 2,
            Y = 0,
            Text = "(blank lists the repository; no install)",
            Visible = false,
        };

        var versionLabel = new Label { Text = "Version    :", X = 0, Y = 1 };
        var versionField = new TextField
        {
            X = 13,
            Y = 1,
            Width = 20,
            Text = string.Empty,
        };
        TuiHelpers.ConfigureTextInput(versionField, SkillViewStyling.DialogSchemeName);
        // Discover's path was observed at this initial version only. A later
        // version choice cannot borrow that source-path navigation evidence.
        var navigationVersion = NullIfEmpty(versionField.Text);
        var pinBox = new CheckBox
        {
            X = Pos.Right(versionField) + 2,
            Y = 1,
            Text = "_pin to version",
            Enabled = false,
        };
        var versionResolved = new Label
        {
            X = 13,
            Y = 2,
            Width = Dim.Fill(2),
            Text = "→ gh default (not resolved)",
        };

        // gh uses a boolean flag to install from the upstream source when a
        // republished skill is detected; it does not accept an override URL.
        var upstreamBox = new CheckBox
        {
            X = 0,
            Y = 3,
            Text = "Use _upstream source for republished skill",
        };

        sourceFrame.Add(skillLabel, skillField, skillHint,
            versionLabel, versionField, pinBox, versionResolved,
            upstreamBox);

        // ── WHERE ──────────────────────────────────────────────────────
        var whereFrame = new FrameView
        {
            Title = "Where",
            CanFocus = true,
            TabStop = TabBehavior.TabStop,
            X = 0,
            Y = Pos.Bottom(sourceFrame),
            Width = Dim.Fill(),
            Height = WhereFrameHeight(),
        };

        var scopeLabel = new Label { Text = "Scope      :", X = 0, Y = 0 };
        var scopeSelector = new OptionSelector
        {
            X = 13,
            Y = 0,
            Orientation = Orientation.Horizontal,
            Labels = ScopeChoices.Select(s => s.Label).ToList(),
            Value = 1,
            FocusedItem = 1,
            Enabled = false,
        };
        var scopeHint = new Label
        {
            X = 13,
            Y = 1,
            Width = Dim.Fill(2),
            Text = "Project: this project · User: across projects",
        };

        var pathLabel = new Label { Text = "Custom path:", X = 0, Y = 2 };
        var pathField = new TextField
        {
            X = 13,
            Y = 2,
            Width = Dim.Fill(2),
            Text = string.Empty,
            Enabled = false,
        };
        TuiHelpers.ConfigureTextInput(pathField, SkillViewStyling.DialogSchemeName);

        var agentsLabel = new Label { Text = "Agents     :", X = 0, Y = 3 };
        var agentsView = new View
        {
            X = 13,
            Y = 3,
            Width = Dim.Fill(2),
            Height = AgentsVisibleRows,
            Enabled = false,
        };
        agentsView.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        var agentSelection = new InstallAgentSelection(agentsView);
        var agentBoxes = agentSelection.Boxes;
        var agentsHint = new Label
        {
            X = 0,
            Y = 3 + AgentsVisibleRows,
            Width = Dim.Fill(2),
            Text = "Detected config first; no selection = Universal/shared.",
        };

        whereFrame.Add(scopeLabel, scopeSelector, scopeHint,
            pathLabel, pathField,
            agentsLabel, agentsView, agentsHint);

        // ── BEHAVIOR ───────────────────────────────────────────────────
        var behaviorFrame = new FrameView
        {
            Title = "Behavior",
            CanFocus = true,
            TabStop = TabBehavior.TabStop,
            X = 0,
            Y = Pos.Bottom(whereFrame),
            Width = Dim.Fill(),
            Height = BehaviorFrameHeight(),
        };

        var forceBox = new CheckBox
        {
            X = 0,
            Y = 0,
            Text = "_force fresh install (occupied folders stay refused)",
        };
        behaviorFrame.Add(forceBox);
        var behaviorRow = 1;

        var allowHiddenBox = new CheckBox
        {
            X = 0,
            Y = behaviorRow,
            Text = "_allow scanning .dot directories",
            Value = _request.AllowHiddenDirs ? CheckState.Checked : CheckState.UnChecked,
        };
        behaviorFrame.Add(allowHiddenBox);
        behaviorRow++;
        var fromLocalBox = new CheckBox
        {
            X = 0,
            Y = behaviorRow,
            Text = "install from _local clone",
        };
        behaviorFrame.Add(fromLocalBox);

        // ── PREVIEW + STATUS ───────────────────────────────────────────
        behaviorFrame.Visible = false;
        behaviorFrame.Height = 0;
        var moreOptions = new SingleRowButton { X = 0, Y = Pos.Bottom(whereFrame), Text = "More options" };
        behaviorFrame.Y = Pos.Bottom(moreOptions);
        moreOptions.Accepting += (_, e) =>
        {
            e.Handled = true;
            behaviorFrame.Visible = !behaviorFrame.Visible;
            behaviorFrame.Height = behaviorFrame.Visible ? BehaviorFrameHeight() : 0;
            moreOptions.Text = behaviorFrame.Visible ? "Fewer options" : "More options";
            form.SetContentSize(new Size(Math.Max(1, form.Viewport.Width), SourceFrameHeight() + WhereFrameHeight() + 3 + (behaviorFrame.Visible ? BehaviorFrameHeight() : 0)));
        };

        var previewLabel = new Label
        {
            X = 0,
            Y = Pos.Bottom(behaviorFrame),
            Width = Dim.Fill(2),
            Text = string.Empty,
        };

        var status = new Label
        {
            X = 2,
            Y = Pos.AnchorEnd(5),
            Width = Dim.Fill(2),
            Height = 3,
            Text = " ready — review the options, then press Install",
        };
        var spinner = new SpinnerView
        {
            X = 0,
            Y = Pos.AnchorEnd(5),
            Width = 1,
            Height = 1,
            Visible = false,
            AutoSpin = false,
            Style = new SpinnerStyle.Dots(),
        };

        var installButton = new Button
        {
            Text = "_Install",
            X = 0,
            Y = Pos.AnchorEnd(1),
            IsDefault = true,
        };
        var detailsButton = new Button { Text = "_Details", X = 1, Y = Pos.AnchorEnd(1), Visible = false };
        detailsButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            if (operation.CurrentOwnership == ModalOperationTracker.Ownership.None && _attempts.Last is { } receipt)
                InstallFailureDetails.Show(_app, InstallFailureDetails.Render(receipt));
        };
        var cancelButton = new Button
        {
            Text = "_Cancel",
            X = Pos.Right(installButton) + 2,
            Y = Pos.AnchorEnd(1),
        };

        var placementText = "Placement observation pending.";
        var placementButton = new SingleRowButton { X = Pos.Right(installButton) + 2, Y = Pos.AnchorEnd(1), Text = "Review placement" };
        cancelButton.X = Pos.Right(placementButton) + 2;
        placementButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (operation.CurrentOwnership == ModalOperationTracker.Ownership.None)
                InstallFailureDetails.Show(_app, placementText, title: " Placement and conflicts (guidance) ");
        };
        using var placement = new InstallPlacementPreview(_runner, _logger, _app.Invoke,
            observation => placementText = observation.Detail, _contextCurrent);

        scopeSelector.KeyBindings.ReplaceCommands(KeyCode.Enter, Command.Activate);
        foreach (var box in scopeSelector.SubViews.OfType<CheckBox>().Concat(new[] { pinBox, upstreamBox, forceBox, allowHiddenBox, fromLocalBox }))
            box.KeyBindings.ReplaceCommands(KeyCode.Enter, Command.Activate);

        // ── LIVE BEHAVIOR ──────────────────────────────────────────────
        // Build Options from the current widget state. Used both by the
        // live command preview and by the actual install handler.
        GhSkillInstallService.Options BuildOptions()
        {
            var agents = new List<string>();
            for (var i = 0; i < agentBoxes.Length; i++)
            {
                if (agentBoxes[i].Value == CheckState.Checked) agents.Add(KnownAgentEntries[i].GhId);
            }
            var scopeIdx = scopeSelector.Value ?? 0;
            var scopeValue = ScopeChoices[Math.Clamp(scopeIdx, 0, ScopeChoices.Length - 1)].Value;
            var path = scopeValue == "custom" ? NullIfEmpty(pathField.Text) : null;
            // Default to `universal` (gh 2.96's shared, agent-agnostic target)
            // when nothing was explicitly checked, unless a custom --dir path
            // is set — gh's --dir overrides --agent, so a default is moot there.
            IReadOnlyList<string>? resolvedAgents = agents.Count > 0
                ? agents
                : path is null ? InstallConfirmModal.DefaultAgents : null;
            return new GhSkillInstallService.Options(
                Agents: resolvedAgents,
                Scope: scopeValue,
                Path: path,
                Version: NullIfEmpty(versionField.Text),
                Pin: pinBox.Value == CheckState.Checked,
                Overwrite: forceBox.Value == CheckState.Checked,
                Upstream: upstreamBox.Value == CheckState.Checked,
                AllowHiddenDirs: allowHiddenBox.Value == CheckState.Checked,
                FromLocal: fromLocalBox.Value == CheckState.Checked)
            {
                NavigationSource = NullIfEmpty(skillField.Text) == _request.SkillName
                    && NullIfEmpty(versionField.Text) == navigationVersion
                    ? InstallNavigationEvidence.SourceAnchor(_request.Repo, _request.SourcePath) : null,
            };
        }

        void Refresh()
        {
            // Version-resolved hint
            var hasVersion = !string.IsNullOrWhiteSpace(versionField.Text);
            pinBox.Enabled = hasVersion;
            if (!hasVersion) pinBox.Value = CheckState.UnChecked;
            var effectivePin = hasVersion &&
                (pinBox.Value == CheckState.Checked || string.IsNullOrWhiteSpace(skillField.Text));
            versionResolved.Text = hasVersion
                ? $"→ will install ref '{versionField.Text!.Trim()}'" + (effectivePin ? " (pinned)" : "")
                : "→ gh default (not resolved)";

            // Custom-path enable
            var scopeIdx = scopeSelector.Value ?? 0;
            var isCustom = ScopeChoices[Math.Clamp(scopeIdx, 0, ScopeChoices.Length - 1)].Value == "custom";
            pathField.Enabled = isCustom;
            if (!isCustom && pathField.Text.Length > 0) pathField.Text = string.Empty;

            // Validation: Custom scope needs a path
            var customMissing = isCustom && string.IsNullOrWhiteSpace(pathField.Text);
            installButton.Enabled = !preparing && !customMissing && !spinner.Visible;
            if (customMissing) status.Text = " custom scope needs a path";
            else if (!spinner.Visible) status.Text = " ready — review the options, then press Install";

            // Command preview
            var args = GhSkillInstallService.BuildArgs(_request.Repo, NullIfEmpty(skillField.Text), BuildOptions());
            previewLabel.Text = "$ gh " + string.Join(' ', args);
            if (!preparing && LastResult?.Succeeded != true)
                placement.Update(_ghPath, _request.Repo, NullIfEmpty(skillField.Text), BuildOptions());
        }

        versionField.TextChanged += (_, _) => Refresh();
        pinBox.ValueChanged += (_, _) => Refresh();
        skillField.TextChanged += (_, _) => Refresh();
        upstreamBox.ValueChanged += (_, _) => Refresh();
        pathField.TextChanged += (_, _) => Refresh();
        scopeSelector.ValueChanged += (_, _) => Refresh();
        forceBox.ValueChanged += (_, _) => Refresh();
        allowHiddenBox.ValueChanged += (_, _) => Refresh();
        fromLocalBox.ValueChanged += (_, _) => Refresh();
        foreach (var cb in agentBoxes) cb.ValueChanged += (_, _) => Refresh();

        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName,
            dialog, sourceFrame, whereFrame, behaviorFrame,
            skillLabel, skillField, skillHint,
            versionLabel, versionField, pinBox, versionResolved,
            scopeLabel, scopeSelector, scopeHint,
            pathLabel, pathField,
            agentsLabel, agentsView, agentsHint,
            forceBox,
            previewLabel, status, spinner,
            upstreamBox,
            allowHiddenBox, fromLocalBox);
        foreach (var cb in agentBoxes) TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, cb);

        async Task RunInstallAsync(
            GhSkillInstallService.Options options,
            string? skillName,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await _install.InstallAsync(
                    _ghPath,
                    _request.Repo,
                    skillName,
                    options,
                    cancellationToken).ConfigureAwait(false);
                LastResult = result;
                var receipt = await _attempts.CompleteAsync(InstallAttemptSummary.Single(result), pending =>
                    operation.InvokeIfActive(() =>
                    {
                        sourceFrame.Visible = whereFrame.Visible = behaviorFrame.Visible = previewLabel.Visible = false;
                        dialog.Height = 12;
                        dialog.Y = Pos.Center();
                        status.Y = 1;
                        status.Height = 6;
                        spinner.Y = 1;
                        status.Text = pending.Guidance;
                    })).ConfigureAwait(false);
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    if (result.ReadOnlyDiscovery)
                    {
                        operation.Release();
                        form.Visible = false; installButton.Visible = placementButton.Visible = false;
                        cancelButton.Enabled = true; cancelButton.Text = "Back to Discover"; cancelButton.X = 0;
                        status.Text = "Repository listing completed. No install started.";
                        cancelButton.SetFocus();
                    }
                    else if (result.Succeeded)
                    {
                        operation.Release();
                        form.Visible = false;
                        installButton.Text = result.Navigation is null ? "Browse Installed" : "View installed skill";
                        installButton.Enabled = true;
                        placementButton.Visible = false;
                        cancelButton.X = Pos.Right(installButton) + 2;
                        cancelButton.Text = "Back to Discover";
                        cancelButton.Enabled = true;
                        dialog.Height = 12;
                        status.Y = 1;
                        status.Height = 7;
                        status.Text = "Install completed. " + (receipt.Refresh == InstallRefreshState.Completed
                            ? (result.Navigation is null ? "Installed refreshed. Exact-copy navigation is unavailable for this placement/source; browse Installed manually." : "Installed refreshed. Choose View installed skill to verify the exact copy.") : receipt.Guidance);
                        installButton.SetFocus();
                    }
                    else
                    {
                        operation.Release();
                        status.Text = (result.Canceled ? "Install canceled. " : "Install failed. ") + receipt.Guidance;
                        form.Enabled = true;
                        placementButton.Enabled = true;
                        detailsButton.X = Pos.Right(placementButton) + 2;
                        cancelButton.X = Pos.Right(detailsButton) + 2;
                        detailsButton.Visible = true;
                        detailsButton.Enabled = true;
                        installButton.Enabled = !result.Canceled;
                        cancelButton.Enabled = true;
                    }
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("install", "install canceled because the dialog closed");
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    status.Text = " install canceled";
                    dialog.RequestStop();
                });
            }
            catch (Exception ex)
            {
                _logger.Error("install", ex.Message);
                operation.InvokeTerminalIfActive(() =>
                {
                    operation.Release();
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    var snippet = TuiHelpers.ErrorSnippet(ex.Message);
                    form.Enabled = true;
                    placementButton.Enabled = true;
                    status.Text = snippet.Length > 0
                        ? $" install failed: {snippet}"
                        : " install failed — see logs";
                    installButton.Enabled = true;
                });
            }
        }

        installButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;

            if (LastResult?.Succeeded == true)
            {
                operation.TryStart(async token =>
                {
                    try
                    {
                        if (LastResult.Navigation is not { } evidence || _findInstalled is null)
                            throw new IOException("Exact-copy navigation needs the unchanged named Discover source at User or Project scope. Browse Installed after inspection; custom folders may need a scan root.");
                        var found = await _findInstalled(evidence, token).ConfigureAwait(false);
                        operation.InvokeTerminalIfActive(() =>
                        {
                            if (token.IsCancellationRequested || dialog.StopRequested || !dialog.IsRunning || !ReferenceEquals(_app.TopRunnableView, dialog)) return;
                            if (_canPublish?.Invoke(found) != true) { operation.Release(); status.Text = "Inventory or context changed; refresh Installed."; return; }
                            Handoff = found; dialog.RequestStop();
                        });
                    }
                    catch (Exception ex)
                    {
                        operation.InvokeTerminalIfActive(() => { operation.Release(); status.Text = InstallFailureDetails.Literal(ex.Message); cancelButton.SetFocus(); });
                    }
                });
                return;
            }
            if (!_contextCurrent()) { status.Text = "Installation context changed; reopen this dialog."; return; }
            var reviewed = placement.Current;
            placement.Cancel();
            var options = BuildOptions() with
            {
                ReviewedDestinations = reviewed?.Roots.Count > 0 ? reviewed.Roots : null,
                ReviewedContext = reviewed?.ContextKey,
            };
            var skillName = NullIfEmpty(skillField.Text);
            form.Enabled = false;
            placementButton.Enabled = false;
            spinner.Visible = true;
            spinner.AutoSpin = true;
            installButton.Enabled = false;
            cancelButton.Enabled = false;
            status.Text = $" installing {_request.Repo}…";
            operation.TryStart(token => RunInstallAsync(options, skillName, token));
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
            dialog.RequestStop();
        };

        form.Add(sourceFrame, whereFrame, moreOptions, behaviorFrame, previewLabel);
        void KeepFocusVisible(View view)
        {
            foreach (var child in view.SubViews)
            {
                KeepFocusVisible(child);
                child.HasFocusChanged += (_, _) =>
                {
                    if (!child.HasFocus || child.SubViews.Count > 0) return;
                    var top = child.FrameToScreen().Top - form.ViewportToScreen().Top + form.Viewport.Y;
                    var viewport = form.Viewport;
                    var offset = top < viewport.Y ? top : top >= viewport.Bottom ? top - viewport.Height + 1 : viewport.Y;
                    if (offset != viewport.Y) form.Viewport = viewport with { Y = Math.Max(0, offset) };
                };
            }
        }
        KeepFocusVisible(form);
        // Nested frame groups restore their last focused child on re-entry.
        // Keep one ordered Tab route through the entire scrolling form/footer.
        var tabOrder = new View[] { skillField, versionField, pinBox, upstreamBox }
            .Concat(scopeSelector.SubViews.OfType<CheckBox>()).Concat([pathField, agentSelection.More])
            .Concat(agentBoxes).Concat([moreOptions, forceBox, allowHiddenBox, fromLocalBox,
                installButton, placementButton, detailsButton, cancelButton]).ToArray();
        bool Available(View view)
        {
            if (!view.CanFocus) return false;
            for (var current = view; current is not null; current = current.SuperView)
                if (!current.Visible || !current.Enabled) return false;
            return true;
        }
        void Traverse(object? sender, Key key)
        {
            if (key.Handled || key.KeyCode is not KeyCode.Tab && key.KeyCode != (KeyCode.Tab | KeyCode.ShiftMask)) return;
            var choices = tabOrder.Where(Available).ToArray();
            if (choices.Length == 0) return;
            var selected = Array.FindIndex(choices, view => view.HasFocus);
            var step = key.KeyCode == KeyCode.Tab ? 1 : -1;
            var index = selected < 0 ? (step > 0 ? 0 : choices.Length - 1)
                : (selected + step + choices.Length) % choices.Length;
            key.Handled = true;
            choices[index].SetFocus();
        }
        foreach (var control in tabOrder) control.KeyDown += Traverse;
        dialog.KeyDown += Traverse;
        form.SetContentSize(new Size(Math.Max(1, form.Viewport.Width), SourceFrameHeight() + WhereFrameHeight() + 3));
        dialog.Add(form, status, spinner, installButton, placementButton, detailsButton, cancelButton);

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
                dialog.RequestStop();
            }
        };

        Refresh();
        status.Text = "Preparing captured project scope and agent observations…";
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
                    operation.Release(); Refresh(); installButton.SetFocus();
                });
            }
            catch (Exception ex)
            {
                operation.InvokeTerminalIfActive(() => { operation.Release(); status.Text = "Preparation failed: " + InstallFailureDetails.Literal(ex.Message); });
            }
        });
        cancelButton.SetFocus();
        ready?.Invoke(dialog, operation);
        try
        {
            _app.Run(dialog);
        }
        finally
        {
            operation.Cancel();
        }
    }

    private static int SourceFrameHeight()
    {
        // 2 frame borders + skill row + version row + version-hint row + upstream
        return 3 + 1 + 2;
    }

    private static int BehaviorFrameHeight()
    {
        // force + allow-hidden + from-local, plus 2 frame borders
        return 3 + 2;
    }

    // Fixed visible height of the scrollable agent-checkbox grid; the
    // catalog is long enough now (gh skill install --agent lists ~47) that
    // it must scroll rather than grow the dialog.
    private const int AgentsVisibleRows = 4;

    private static int WhereFrameHeight()
    {
        // 2 frame borders + scope row + scope-hint row + path row +
        // agents grid (scrollable, fixed height) + agents hint row
        return 2 + 1 + 1 + 1 + AgentsVisibleRows + 1;
    }

    private static string? NullIfEmpty(string? s)
    {
        var trimmed = s?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
