using System.Collections.Immutable;
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
/// skill name within the repo, and an optional repo-relative path
/// (when the search produced a directory hit rather than a top-level
/// repo). `InstallScreen` picks the rest of the flags interactively, but the
/// parent screen can seed obvious toggles such as hidden-dir access.
public sealed record InstallRequest(string Repo, string? SkillName, string? RepoPath, bool AllowHiddenDirs = false);

/// Phase 4 install dialog. Consumes an `InstallRequest` (repo + skill +
/// repo-path) and runs `gh skill install` with the flags the user has
/// chosen.
///
/// Capability-gated fields (`--repo-path`, `--upstream`, `--allow-hidden-dirs`,
/// `--from-local`) are hidden entirely when the probe hasn't confirmed them.
/// Since `gh skill` has no future-version preview we can hint at, leaving
/// disabled-but-visible controls in the dialog would only confuse users.
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
    private readonly CapabilityProfile _capabilities;
    private readonly InstallRequest _request;

    public InstallResult? LastResult { get; private set; }

    public InstallScreen(
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

    public void Show()
    {
        using var dialog = new Dialog
        {
            Title = $"Install — {_request.Repo}{(_request.SkillName is null ? "" : "/" + _request.SkillName)}",
            Width = Dim.Percent(85),
            Height = Dim.Percent(85),
        };

        // ── SOURCE ─────────────────────────────────────────────────────
        var sourceFrame = new FrameView
        {
            Title = "Source",
            X = 0, Y = 0, Width = Dim.Fill(), Height = SourceFrameHeight(),
        };

        var skillLabel = new Label { Text = "Skill name :", X = 0, Y = 0 };
        var skillField = new TextField
        {
            X = 13, Y = 0, Width = 32,
            Text = _request.SkillName ?? string.Empty,
        };
        TuiHelpers.ConfigureTextInput(skillField, "Dialog");
        var skillHint = new Label
        {
            X = Pos.Right(skillField) + 2, Y = 0,
            Text = "(blank = repo's default skill)",
        };

        var versionLabel = new Label { Text = "Version    :", X = 0, Y = 1 };
        var versionField = new TextField
        {
            X = 13, Y = 1, Width = 20, Text = string.Empty,
        };
        TuiHelpers.ConfigureTextInput(versionField, "Dialog");
        var pinBox = new CheckBox
        {
            X = Pos.Right(versionField) + 2, Y = 1,
            Text = "_pin to version",
            Enabled = false,
        };
        var versionResolved = new Label
        {
            X = 13, Y = 2, Width = Dim.Fill(2),
            Text = "→ blank uses the latest release",
        };

        // RepoPath / Upstream are capability-gated. We *omit* them entirely
        // when unsupported instead of disabling, because there's no future
        // version of gh skill to direct the user toward (yet).
        TextField? repoPathField = null;
        Label? repoPathLabel = null;
        Label? repoPathHint = null;
        TextField? upstreamField = null;
        Label? upstreamLabel = null;
        Label? upstreamHint = null;
        var nextRow = 3;
        if (_capabilities.SupportsRepoPath)
        {
            repoPathLabel = new Label { Text = "Subdir     :", X = 0, Y = nextRow };
            repoPathField = new TextField
            {
                X = 13, Y = nextRow, Width = 32,
                Text = _request.RepoPath ?? string.Empty,
            };
            TuiHelpers.ConfigureTextInput(repoPathField, "Dialog");
            repoPathHint = new Label
            {
                X = Pos.Right(repoPathField) + 2, Y = nextRow,
                Text = "(install a subdir as the skill)",
            };
            nextRow++;
        }
        if (_capabilities.SupportsUpstream)
        {
            upstreamLabel = new Label { Text = "Upstream   :", X = 0, Y = nextRow };
            upstreamField = new TextField
            {
                X = 13, Y = nextRow, Width = 40, Text = string.Empty,
            };
            TuiHelpers.ConfigureTextInput(upstreamField, "Dialog");
            upstreamHint = new Label
            {
                X = Pos.Right(upstreamField) + 2, Y = nextRow,
                Text = "(override recorded source URL)",
            };
            nextRow++;
        }

        sourceFrame.Add(skillLabel, skillField, skillHint,
            versionLabel, versionField, pinBox, versionResolved);
        if (repoPathLabel is not null) sourceFrame.Add(repoPathLabel, repoPathField!, repoPathHint!);
        if (upstreamLabel is not null) sourceFrame.Add(upstreamLabel, upstreamField!, upstreamHint!);

        // ── WHERE ──────────────────────────────────────────────────────
        var whereFrame = new FrameView
        {
            Title = "Where",
            X = 0, Y = Pos.Bottom(sourceFrame), Width = Dim.Fill(), Height = 6,
        };

        var scopeLabel = new Label { Text = "Scope      :", X = 0, Y = 0 };
        var scopeSelector = new OptionSelector
        {
            X = 13, Y = 0,
            Orientation = Orientation.Horizontal,
            Labels = ScopeChoices.Select(s => s.Label).ToList(),
            Value = DefaultScopeIndex(),
        };
        var scopeHint = new Label
        {
            X = 13, Y = 1, Width = Dim.Fill(2),
            Text = "Project = repo skill dir · Global = home skill dir (everywhere)",
        };

        var pathLabel = new Label { Text = "Custom path:", X = 0, Y = 2 };
        var pathField = new TextField
        {
            X = 13, Y = 2, Width = Dim.Fill(2),
            Text = string.Empty,
            Enabled = false,
        };
        TuiHelpers.ConfigureTextInput(pathField, "Dialog");

        var agentsLabel = new Label { Text = "Agents     :", X = 0, Y = 3 };
        var agentBoxes = new CheckBox[KnownAgentEntries.Length];
        var installedAgents = DetectInstalledAgents();
        var anyInstalled = installedAgents.Count > 0;
        var agentX = 13;
        for (var i = 0; i < KnownAgentEntries.Length; i++)
        {
            var agent = KnownAgentEntries[i];
            agentBoxes[i] = new CheckBox
            {
                X = agentX, Y = 3, Text = agent.Label,
                Value = installedAgents.Contains(agent.GhId) ? CheckState.Checked : CheckState.UnChecked,
            };
            agentX += agent.Label.Length + 6;
        }
        var agentsHint = new Label
        {
            X = 13, Y = 4, Width = Dim.Fill(2),
            Text = anyInstalled
                ? "(pre-checked from detected agents — adjust as needed)"
                : "(blank = let gh skill register all agents)",
        };

        whereFrame.Add(scopeLabel, scopeSelector, scopeHint,
            pathLabel, pathField,
            agentsLabel, agentsHint);
        foreach (var cb in agentBoxes) whereFrame.Add(cb);

        // ── BEHAVIOR ───────────────────────────────────────────────────
        var behaviorFrame = new FrameView
        {
            Title = "Behavior",
            X = 0, Y = Pos.Bottom(whereFrame), Width = Dim.Fill(),
            Height = BehaviorFrameHeight(),
        };

        var forceBox = new CheckBox
        {
            X = 0, Y = 0, Text = "_force overwrite existing install",
        };
        behaviorFrame.Add(forceBox);
        var behaviorRow = 1;

        CheckBox? allowHiddenBox = null;
        if (_capabilities.SupportsAllowHiddenDirs)
        {
            allowHiddenBox = new CheckBox
            {
                X = 0, Y = behaviorRow, Text = "_allow scanning .dot directories",
                Value = _request.AllowHiddenDirs ? CheckState.Checked : CheckState.UnChecked,
            };
            behaviorFrame.Add(allowHiddenBox);
            behaviorRow++;
        }
        CheckBox? fromLocalBox = null;
        if (_capabilities.SupportsFromLocal)
        {
            fromLocalBox = new CheckBox
            {
                X = 0, Y = behaviorRow, Text = "install from _local clone",
            };
            behaviorFrame.Add(fromLocalBox);
        }

        // ── PREVIEW + STATUS ───────────────────────────────────────────
        var previewLabel = new Label
        {
            X = 0, Y = Pos.Bottom(behaviorFrame),
            Width = Dim.Fill(2),
            Text = string.Empty,
        };

        var status = new Label
        {
            X = 0, Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(10),
            Text = " ready — review the options, then press Install",
        };
        var spinner = new SpinnerView
        {
            X = Pos.AnchorEnd(10), Y = Pos.AnchorEnd(3),
            Width = 1, Height = 1,
            Visible = false,
            AutoSpin = false,
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
            return new GhSkillInstallService.Options(
                Agents: agents,
                Scope: scopeValue,
                Path: scopeValue == "custom" ? NullIfEmpty(pathField.Text) : null,
                Version: NullIfEmpty(versionField.Text),
                Pin: pinBox.Value == CheckState.Checked,
                Overwrite: forceBox.Value == CheckState.Checked,
                Upstream: upstreamField is null ? null : NullIfEmpty(upstreamField.Text),
                AllowHiddenDirs: allowHiddenBox?.Value == CheckState.Checked,
                RepoPath: repoPathField is null ? null : NullIfEmpty(repoPathField.Text),
                FromLocal: fromLocalBox?.Value == CheckState.Checked);
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
            var args = GhSkillInstallService.BuildArgs(_request.Repo, NullIfEmpty(skillField.Text), _capabilities, BuildOptions());
            previewLabel.Text = "$ gh " + string.Join(' ', args);
        }

        versionField.TextChanged += (_, _) => Refresh();
        pinBox.ValueChanged += (_, _) => Refresh();
        skillField.TextChanged += (_, _) => Refresh();
        if (repoPathField is not null) repoPathField.TextChanged += (_, _) => Refresh();
        if (upstreamField is not null) upstreamField.TextChanged += (_, _) => Refresh();
        pathField.TextChanged += (_, _) => Refresh();
        scopeSelector.ValueChanged += (_, _) => Refresh();
        forceBox.ValueChanged += (_, _) => Refresh();
        if (allowHiddenBox is not null) allowHiddenBox.ValueChanged += (_, _) => Refresh();
        if (fromLocalBox is not null) fromLocalBox.ValueChanged += (_, _) => Refresh();
        foreach (var cb in agentBoxes) cb.ValueChanged += (_, _) => Refresh();

        TuiHelpers.ApplyScheme("Dialog",
            dialog, sourceFrame, whereFrame, behaviorFrame,
            skillLabel, skillField, skillHint,
            versionLabel, versionField, pinBox, versionResolved,
            scopeLabel, scopeSelector, scopeHint,
            pathLabel, pathField,
            agentsLabel, agentsHint,
            forceBox,
            previewLabel, status, spinner);
        if (repoPathLabel is not null) TuiHelpers.ApplyScheme("Dialog", repoPathLabel, repoPathField!, repoPathHint!);
        if (upstreamLabel is not null) TuiHelpers.ApplyScheme("Dialog", upstreamLabel, upstreamField!, upstreamHint!);
        if (allowHiddenBox is not null) TuiHelpers.ApplyScheme("Dialog", allowHiddenBox);
        if (fromLocalBox is not null) TuiHelpers.ApplyScheme("Dialog", fromLocalBox);
        foreach (var cb in agentBoxes) TuiHelpers.ApplyScheme("Dialog", cb);

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
                    _capabilities,
                    options).ConfigureAwait(false);
                _app.Invoke(() =>
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
            catch (Exception ex)
            {
                _logger.Error("install", ex.Message);
                _app.Invoke(() =>
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
            dialog.RequestStop();
        };

        dialog.Add(sourceFrame, whereFrame, behaviorFrame,
            previewLabel, status, spinner,
            installButton, cancelButton);

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                dialog.RequestStop();
                key.Handled = true;
            }
        };

        Refresh();
        installButton.SetFocus();
        _app.Run(dialog);
    }

    private int SourceFrameHeight()
    {
        // 2 frame borders + skill row + version row + version-hint row
        var rows = 3;
        if (_capabilities.SupportsRepoPath) rows++;
        if (_capabilities.SupportsUpstream) rows++;
        return rows + 2;
    }

    private int BehaviorFrameHeight()
    {
        var rows = 1; // force
        if (_capabilities.SupportsAllowHiddenDirs) rows++;
        if (_capabilities.SupportsFromLocal) rows++;
        return rows + 2;
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
