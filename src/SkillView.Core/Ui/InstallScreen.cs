using System.Collections.Immutable;
using System.Drawing;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Logging;
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
public sealed record InstallRequest(string Repo, string? SkillName, bool AllowHiddenDirs = false);

/// Phase 4 install dialog. Consumes an `InstallRequest` (repo + skill) and
/// runs `gh skill install` with the flags the user has chosen. SkillView
/// requires gh ≥ 2.95.0, so all flags (`--upstream`, `--allow-hidden-dirs`,
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
    // accepts. "Global" reads more clearly than "User" — per `gh skill install
    // --help`, the user scope installs into the home directory and is
    // available everywhere, which is what people mean by "global".
    public static readonly (string Label, string Value)[] ScopeChoices =
    {
        ("Project", "project"),
        ("Global", "user"),
        ("Custom", "custom"),
    };

    private readonly IApplication _app;
    private readonly GhSkillInstallService _install;
    private readonly Logger _logger;
    private readonly string _ghPath;
    private readonly InstallRequest _request;

    public InstallResult? LastResult { get; private set; }

    public InstallScreen(
        IApplication app,
        GhSkillInstallService install,
        Logger logger,
        string ghPath,
        InstallRequest request)
    {
        _app = app;
        _install = install;
        _logger = logger;
        _ghPath = ghPath;
        _request = request;
    }

    public void Show()
    {
        using var lifetime = new CancellationTokenSource();
        using var dialog = new Dialog
        {
            Title = $"Install — {_request.Repo}{(_request.SkillName is null ? "" : "/" + _request.SkillName)}",
            Width = Dim.Percent(85),
            Height = Dim.Percent(85),
        };

        void InvokeIfActive(Action action)
        {
            if (lifetime.IsCancellationRequested)
            {
                return;
            }

            _app.Invoke(() =>
            {
                if (lifetime.IsCancellationRequested)
                {
                    return;
                }

                action();
            });
        }

        // ── SOURCE ─────────────────────────────────────────────────────
        var sourceFrame = new FrameView
        {
            Title = "Source",
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
            Text = "(blank = repo's default skill)",
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
            Text = "→ blank uses the latest release",
        };

        // `--upstream` overrides the recorded source URL. gh ≥ 2.95 is
        // required, so every flag here is guaranteed and always shown.
        var upstreamLabel = new Label { Text = "Upstream   :", X = 0, Y = 3 };
        var upstreamField = new TextField
        {
            X = 13,
            Y = 3,
            Width = 40,
            Text = string.Empty,
        };
        TuiHelpers.ConfigureTextInput(upstreamField, SkillViewStyling.DialogSchemeName);
        var upstreamHint = new Label
        {
            X = Pos.Right(upstreamField) + 2,
            Y = 3,
            Text = "(override recorded source URL)",
        };

        sourceFrame.Add(skillLabel, skillField, skillHint,
            versionLabel, versionField, pinBox, versionResolved,
            upstreamLabel, upstreamField, upstreamHint);

        // ── WHERE ──────────────────────────────────────────────────────
        var whereFrame = new FrameView
        {
            Title = "Where",
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
            Value = DefaultScopeIndex(),
        };
        var scopeHint = new Label
        {
            X = 13,
            Y = 1,
            Width = Dim.Fill(2),
            Text = "Project = repo skill dir · Global = home skill dir (everywhere)",
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
        var installedAgents = DetectInstalledAgents();
        var anyInstalled = installedAgents.Count > 0;
        var agentsView = new View
        {
            X = 13,
            Y = 3,
            Width = Dim.Fill(2),
            Height = AgentsVisibleRows,
        };
        agentsView.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        var agentGrid = AgentCheckboxGrid.Build(KnownAgentEntries, installedAgents, perRow: 4);
        var agentBoxes = agentGrid.Boxes;
        foreach (var cb in agentBoxes) agentsView.Add(cb);
        agentsView.SetContentSize(new Size(agentGrid.ContentWidth, agentGrid.RowCount));
        var agentsHint = new Label
        {
            X = 13,
            Y = 3 + AgentsVisibleRows,
            Width = Dim.Fill(2),
            Text = anyInstalled
                ? "(pre-checked from detected agents — adjust as needed)"
                : "(blank = universal — shared .agents/skills, not agent-specific)",
        };

        whereFrame.Add(scopeLabel, scopeSelector, scopeHint,
            pathLabel, pathField,
            agentsLabel, agentsView, agentsHint);

        // ── BEHAVIOR ───────────────────────────────────────────────────
        var behaviorFrame = new FrameView
        {
            Title = "Behavior",
            X = 0,
            Y = Pos.Bottom(whereFrame),
            Width = Dim.Fill(),
            Height = BehaviorFrameHeight(),
        };

        var forceBox = new CheckBox
        {
            X = 0,
            Y = 0,
            Text = "_force overwrite existing install",
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
        var previewLabel = new Label
        {
            X = 0,
            Y = Pos.Bottom(behaviorFrame),
            Width = Dim.Fill(2),
            Text = string.Empty,
        };

        var status = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(10),
            Text = " ready — review the options, then press Install",
        };
        var spinner = new SpinnerView
        {
            X = Pos.AnchorEnd(10),
            Y = Pos.AnchorEnd(3),
            Width = 1,
            Height = 1,
            Visible = false,
            AutoSpin = false,
            Style = new SpinnerStyle.Dots(),
        };

        var installButton = new Button
        {
            Text = "_Install",
            X = Pos.Center() - 12,
            Y = Pos.AnchorEnd(1),
            IsDefault = true,
        };
        var cancelButton = new Button
        {
            Text = "_Cancel",
            X = Pos.Right(installButton) + 4,
            Y = Pos.AnchorEnd(1),
        };

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
                Upstream: NullIfEmpty(upstreamField.Text),
                AllowHiddenDirs: allowHiddenBox.Value == CheckState.Checked,
                FromLocal: fromLocalBox.Value == CheckState.Checked);
        }

        void Refresh()
        {
            // Version-resolved hint
            var hasVersion = !string.IsNullOrWhiteSpace(versionField.Text);
            pinBox.Enabled = hasVersion;
            if (!hasVersion) pinBox.Value = CheckState.UnChecked;
            versionResolved.Text = hasVersion
                ? $"→ will install ref '{versionField.Text!.Trim()}'" + (pinBox.Value == CheckState.Checked ? " (pinned)" : "")
                : "→ blank uses the latest release";

            // Custom-path enable
            var scopeIdx = scopeSelector.Value ?? 0;
            var isCustom = ScopeChoices[Math.Clamp(scopeIdx, 0, ScopeChoices.Length - 1)].Value == "custom";
            pathField.Enabled = isCustom;
            if (!isCustom && pathField.Text.Length > 0) pathField.Text = string.Empty;

            // Validation: Custom scope needs a path
            var customMissing = isCustom && string.IsNullOrWhiteSpace(pathField.Text);
            installButton.Enabled = !customMissing && !spinner.Visible;
            if (customMissing) status.Text = " custom scope needs a path";
            else if (!spinner.Visible) status.Text = " ready — review the options, then press Install";

            // Command preview
            var args = GhSkillInstallService.BuildArgs(_request.Repo, NullIfEmpty(skillField.Text), BuildOptions());
            previewLabel.Text = "$ gh " + string.Join(' ', args);
        }

        versionField.TextChanged += (_, _) => Refresh();
        pinBox.ValueChanged += (_, _) => Refresh();
        skillField.TextChanged += (_, _) => Refresh();
        upstreamField.TextChanged += (_, _) => Refresh();
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
            upstreamLabel, upstreamField, upstreamHint,
            allowHiddenBox, fromLocalBox);
        foreach (var cb in agentBoxes) TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, cb);

        installButton.Accepting += async (_, ev) =>
        {
            ev.Handled = true;
            try
            {
                if (spinner.Visible) return;
                spinner.Visible = true;
                spinner.AutoSpin = true;
                installButton.Enabled = false;
                status.Text = $" installing {_request.Repo}…";

                var options = BuildOptions();
                var skillName = NullIfEmpty(skillField.Text);
                var result = await _install.InstallAsync(
                    _ghPath,
                    _request.Repo,
                    skillName,
                    options,
                    lifetime.Token).ConfigureAwait(false);
                InvokeIfActive(() =>
                {
                    LastResult = result;
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    if (result.Succeeded)
                    {
                        status.Text = $" install succeeded — closing";
                        dialog.RequestStop();
                    }
                    else
                    {
                        var snippet = TuiHelpers.ErrorSnippet(result.ErrorMessage);
                        status.Text = snippet.Length > 0
                            ? $" install failed (exit {result.ExitCode}): {snippet}"
                            : $" install failed (exit {result.ExitCode}) — see logs";
                        installButton.Enabled = true;
                    }
                });
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                _logger.Debug("install", "install canceled because the dialog closed");
            }
            catch (Exception ex)
            {
                _logger.Error("install", ex.Message);
                InvokeIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    var snippet = TuiHelpers.ErrorSnippet(ex.Message);
                    status.Text = snippet.Length > 0
                        ? $" install failed: {snippet}"
                        : " install failed — see logs";
                    installButton.Enabled = true;
                });
            }
        };

        cancelButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            lifetime.Cancel();
            dialog.RequestStop();
        };

        dialog.Add(sourceFrame, whereFrame, behaviorFrame,
            previewLabel, status, spinner,
            installButton, cancelButton);

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                lifetime.Cancel();
                dialog.RequestStop();
                key.Handled = true;
            }
        };

        Refresh();
        installButton.SetFocus();
        try
        {
            _app.Run(dialog);
        }
        finally
        {
            lifetime.Cancel();
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

    private int DefaultScopeIndex()
    {
        // If any project-scope agent dir exists in cwd, default to Project,
        // otherwise default to User. Saves the user a click in the common
        // "I'm not in a project" case.
        try
        {
            if (InstallAgentCatalog.HasProjectScopeCandidate(Directory.GetCurrentDirectory())) return 0;
        }
        catch { /* fall through to User */ }
        return 1;
    }

    private static HashSet<string> DetectInstalledAgents()
    {
        // Heuristic: an agent is "installed" if its conventional home
        // directory exists. Cheap, AOT-safe, and good enough to pre-check
        // the right boxes for most users. False negatives just mean the
        // user toggles the box themselves.
        var found = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return found;
            return InstallAgentCatalog.DetectInstalledGhIds(home);
        }
        catch { /* best-effort detection */ }
        return found;
    }

    private static string? NullIfEmpty(string? s)
    {
        var trimmed = s?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
