using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Phase 4 install dialog. Picks up where `SearchScreen` left off: consumes
/// an `InstallRequest` (repo + skill + repo-path) and runs `gh skill install`
/// with the flags the user has chosen.
///
/// Capability-gated fields (`--repo-path`, `--upstream`, `--allow-hidden-dirs`,
/// `--from-local`) are hidden or disabled when the probe hasn't confirmed
/// them, so the UI stays honest about what `gh skill install` will actually
/// accept.
public sealed class InstallScreen
{
    // Known agent IDs for the multi-select checkboxes. This list is static
    // because AOT forbids reflection-based discovery, and `gh skill install
    // --help` doesn't enumerate valid agent names. Update this array when new
    // agents are added to the gh skill ecosystem.
    public static readonly string[] KnownAgents =
    {
        "claude", "copilot", "cursor", "codex", "gemini", "antigravity",
    };

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

        var skillLabel = new Label { Text = "Skill   :", X = 0, Y = 0 };
        var skillField = new TextField
        {
            X = 10, Y = 0, Width = Dim.Fill(2),
            Text = _request.SkillName ?? string.Empty,
        };

        var versionLabel = new Label { Text = "Version :", X = 0, Y = 1 };
        var versionField = new TextField
        {
            X = 10, Y = 1, Width = 24, Text = string.Empty,
        };
        var pinBox = new CheckBox
        {
            X = 36, Y = 1, Text = "_pin",
        };

        var repoPathLabel = new Label { Text = "Repo-path:", X = 0, Y = 2 };
        var repoPathField = new TextField
        {
            X = 10, Y = 2, Width = Dim.Fill(2),
            Text = _request.RepoPath ?? string.Empty,
            Enabled = _capabilities.SupportsRepoPath,
        };
        if (!_capabilities.SupportsRepoPath)
        {
            repoPathField.Text = "(not supported by this gh build)";
        }

        var upstreamLabel = new Label { Text = "Upstream:", X = 0, Y = 3 };
        var upstreamField = new TextField
        {
            X = 10, Y = 3, Width = Dim.Fill(2),
            Text = string.Empty,
            Enabled = _capabilities.SupportsUpstream,
        };

        var agentsLabel = new Label { Text = "Agents  :", X = 0, Y = 5 };
        // TODO(tg2): upstream — we'd prefer FlagSelector here per §14.2, but
        // the rc.4 dictionary API is in flux; a checkbox row is the safer
        // portable choice until the API stabilizes.
        var agentBoxes = new CheckBox[KnownAgents.Length];
        var agentX = 10;
        for (var i = 0; i < KnownAgents.Length; i++)
        {
            agentBoxes[i] = new CheckBox
            {
                X = agentX, Y = 5,
                Text = KnownAgents[i],
            };
            agentX += KnownAgents[i].Length + 6;
        }

        var scopeLabel = new Label { Text = "Scope   :", X = 0, Y = 7 };
        var scopeSelector = new OptionSelector
        {
            X = 10, Y = 7,
            Orientation = Orientation.Horizontal,
            Labels = ScopeChoices.Select(s => s.Label).ToList(),
            Value = 0,
        };

        var pathLabel = new Label { Text = "Path    :", X = 0, Y = 9 };
        var pathField = new TextField
        {
            X = 10, Y = 9, Width = Dim.Fill(2),
            Text = string.Empty,
        };
        var pathHint = new Label
        {
            Text = "(only applied when scope=custom)",
            X = 10, Y = 10, Width = Dim.Fill(2),
        };

        var forceBox = new CheckBox
        {
            X = 0, Y = 12, Text = "_force (overwrite existing)",
        };
        var allowHiddenBox = new CheckBox
        {
            X = 34, Y = 12,
            Text = "_allow-hidden-dirs",
            Enabled = _capabilities.SupportsAllowHiddenDirs,
        };
        var fromLocalBox = new CheckBox
        {
            X = 0, Y = 13,
            Text = "from-_local",
            Enabled = _capabilities.SupportsFromLocal,
        };

        var status = new Label
        {
            X = 0, Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(10),
            Text = " ready — review fields, press Install",
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

        installButton.Accepting += async (_, ev) =>
        {
            ev.Handled = true;
            if (spinner.Visible) return;
            spinner.Visible = true;
            spinner.AutoSpin = true;
            status.Text = $" installing {_request.Repo}…";

            var agents = new List<string>();
            for (var i = 0; i < agentBoxes.Length; i++)
            {
                if (agentBoxes[i].Value == CheckState.Checked)
                {
                    agents.Add(KnownAgents[i]);
                }
            }
            var scopeIdx = scopeSelector.Value ?? 0;
            var scopeValue = ScopeChoices[Math.Clamp(scopeIdx, 0, ScopeChoices.Length - 1)].Value;

            var options = new GhSkillInstallService.Options(
                Agents: agents,
                Scope: scopeValue,
                Path: scopeValue == "custom" ? NullIfEmpty(pathField.Text) : null,
                Version: NullIfEmpty(versionField.Text),
                Pin: pinBox.Value == CheckState.Checked,
                Overwrite: forceBox.Value == CheckState.Checked,
                Upstream: _capabilities.SupportsUpstream ? NullIfEmpty(upstreamField.Text) : null,
                AllowHiddenDirs: allowHiddenBox.Value == CheckState.Checked,
                RepoPath: _capabilities.SupportsRepoPath ? NullIfEmpty(repoPathField.Text) : null,
                FromLocal: fromLocalBox.Value == CheckState.Checked);

            var skillName = NullIfEmpty(skillField.Text);
            try
            {
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
                        _app.RequestStop();
                    }
                    else
                    {
                        var snippet = TuiHelpers.ErrorSnippet(result.ErrorMessage);
                        status.Text = snippet.Length > 0
                            ? $" install failed (exit {result.ExitCode}): {snippet}"
                            : $" install failed (exit {result.ExitCode}) — see logs";
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
                });
            }
        };

        cancelButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _app.RequestStop();
        };

        dialog.Add(
            skillLabel, skillField,
            versionLabel, versionField, pinBox,
            repoPathLabel, repoPathField,
            upstreamLabel, upstreamField,
            agentsLabel);
        foreach (var cb in agentBoxes) dialog.Add(cb);
        dialog.Add(
            scopeLabel, scopeSelector,
            pathLabel, pathField, pathHint,
            forceBox, allowHiddenBox, fromLocalBox,
            status, spinner,
            installButton, cancelButton);

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                _app.RequestStop();
                key.Handled = true;
            }
        };

        installButton.SetFocus();
        _app.Run(dialog);
    }

    private static string? NullIfEmpty(string? s)
    {
        var trimmed = s?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
