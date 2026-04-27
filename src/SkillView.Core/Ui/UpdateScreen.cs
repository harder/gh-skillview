using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Phase 5 update dialog. The left side is a skill picker (installed
/// inventory filtered to non-pinned by default, with a toggle); the right
/// side is the dry-run preview pane. Run / Dry-run / Cancel drive the
/// `GhSkillUpdateService`.
///
/// Pinned-skill handling: pinned rows are rendered with a `p` flag and
/// are skipped unless the user flips the `--force` or `--unpin` toggles.
/// The "--all" toggle hands off to `gh skill update --all`; when the probe
/// hasn't reported `--yes` or `--non-interactive`, the UI refuses the combo
/// to avoid hanging on v2.91.0's interactive prompt.
public sealed class UpdateScreen
{
    private readonly IApplication _app;
    private readonly GhSkillUpdateService _update;
    private readonly Logger _logger;
    private readonly string _ghPath;
    private readonly CapabilityProfile _capabilities;
    private readonly IReadOnlyList<InstalledSkill> _skills;

    public UpdateResult? LastResult { get; private set; }

    public UpdateScreen(
        IApplication app,
        GhSkillUpdateService update,
        Logger logger,
        string ghPath,
        CapabilityProfile capabilities,
        IReadOnlyList<InstalledSkill> skills)
    {
        _app = app;
        _update = update;
        _logger = logger;
        _ghPath = ghPath;
        _capabilities = capabilities;
        _skills = skills;
    }

    public void Show()
    {
        using var window = new Window
        {
            Title = "Update skills",
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        var tableLabel = new Label { Text = "Select skills to update. Press Space to toggle.", X = 0, Y = 0 };
        var table = new TableView
        {
            X = 0, Y = 1,
            Width = Dim.Percent(45),
            Height = Dim.Fill(5),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(table);

        var nameW = 12;
        var rowsList = _skills.Select((s, i) => (i, s)).ToList();
        var inner = new EnumerableTableSource<(int Idx, InstalledSkill S)>(
            rowsList,
            new Dictionary<string, Func<(int Idx, InstalledSkill S), object>>
            {
                ["Name"] = row => TuiHelpers.Truncate(row.S.Name, nameW),
                ["Scope"] = row => row.S.Scope.ToString(),
                ["Flags"] = row => (row.S.Pinned ? "p" : "-") + (row.S.IsSymlinked ? "s" : "-"),
            });
        // RC5 wrapper: inserts checkbox column at index 0, hooks Space and
        // click toggling on the table, exposes the checked set as CheckedRows.
        var wrapper = new CheckBoxTableSourceWrapperByIndex(table, inner);
        table.Table = wrapper;
        var style = table.Style;
        style.ExpandLastColumn = true;
        // Wrapper inserts " " at column 0, so Name is column 2.
        var nameStyle = style.GetOrCreateColumnStyle(2);
        nameStyle.MinWidth = 8;

        void Recompute()
        {
            var viewportWidth = table.Viewport.Width;
            var available = viewportWidth > 0 ? Math.Max(30, viewportWidth - 4) : 50;
            // Fixed: checkbox(1) + Scope(6) + Flags(5). Remainder → Name.
            nameW = Math.Max(12, available - 1 - 6 - 5);
            nameStyle.MaxWidth = nameW;
            table.Update();
        }
        Recompute();
        var lastUpdateWidth = -1;
        table.FrameChanged += (_, _) =>
        {
            var w = table.Viewport.Width;
            if (w > 0 && w != lastUpdateWidth)
            {
                lastUpdateWidth = w;
                Recompute();
            }
        };

        var preview = new Markdown
        {
            X = Pos.Right(table) + 1, Y = 1,
            Width = Dim.Fill(), Height = Dim.Fill(5),
            Text = "## Dry-run results\n\n_Press **Dry-run** to preview pending updates._",
        };
        TuiHelpers.ConfigureMarkdownPane(preview, "Base");

        var allBox = new CheckBox
        {
            X = 0, Y = Pos.AnchorEnd(4),
            Text = _capabilities.SupportsUpdateAll ? "_all" : "_all (not supported)",
            Enabled = _capabilities.SupportsUpdateAll,
        };
        var forceBox = new CheckBox
        {
            X = 10, Y = Pos.AnchorEnd(4),
            Text = "_force",
            Enabled = _capabilities.SupportsUpdateForce,
        };
        var unpinBox = new CheckBox
        {
            X = 22, Y = Pos.AnchorEnd(4),
            Text = "_unpin",
            Enabled = _capabilities.SupportsUpdateUnpin,
        };
        var yesBox = new CheckBox
        {
            X = 34, Y = Pos.AnchorEnd(4),
            Text = _capabilities.SupportsUpdateYes ? "_yes" : "yes (needs gh --yes)",
            Enabled = _capabilities.SupportsUpdateYes,
            Value = _capabilities.SupportsUpdateYes ? CheckState.Checked : CheckState.UnChecked,
        };

        var status = new Label
        {
            X = 0, Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(10),
            Text = " ready — select skills or enable --all, then choose Dry-run or Update",
        };
        var spinner = new SpinnerView
        {
            X = Pos.AnchorEnd(10), Y = Pos.AnchorEnd(3),
            Width = 1, Height = 1,
            Visible = false, AutoSpin = false,
        };

        var dryRunButton = new Button
        {
            Text = "_Dry-run",
            X = Pos.Center() - 22,
            Y = Pos.AnchorEnd(2),
            Enabled = _capabilities.SupportsUpdateDryRun,
        };
        var updateButton = new Button
        {
            Text = "_Update",
            X = Pos.Right(dryRunButton) + 2,
            Y = Pos.AnchorEnd(2),
            IsDefault = true,
        };
        var cancelButton = new Button
        {
            Text = "_Cancel",
            X = Pos.Right(updateButton) + 2,
            Y = Pos.AnchorEnd(2),
        };

        var statusBar = new StatusBar(
        [
            new Shortcut { Title = "Space", HelpText = "Toggle" },
            new Shortcut { Title = "d", HelpText = "Dry-run" },
            new Shortcut { Title = "u", HelpText = "Update" },
            new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Back" },
        ]);

        TuiHelpers.ApplyScheme("Base",
            window, tableLabel, table, preview,
            allBox, forceBox, unpinBox, yesBox,
            status, spinner, dryRunButton, updateButton, cancelButton, statusBar);

        // Space-to-toggle is wired by CheckBoxTableSourceWrapperByIndex; no
        // manual handler needed here.

        async Task RunAsync(bool dryRun)
        {
            if (spinner.Visible) return;

            var allChecked = allBox.Value == CheckState.Checked;
            var yesChecked = yesBox.Value == CheckState.Checked;
            var skills = new List<string>();
            for (var i = 0; i < _skills.Count; i++)
            {
                if (wrapper.CheckedRows.Contains(i)) skills.Add(_skills[i].Name);
            }

            if (!allChecked && skills.Count == 0)
            {
                status.Text = dryRun
                    ? " pick at least one skill or enable --all to dry-run"
                    : " pick at least one skill or enable --all";
                return;
            }
            if (allChecked && !dryRun && !yesChecked && !_capabilities.SupportsUpdateYes)
            {
                status.Text = " refusing --all without --yes (would hang on gh's prompt)";
                return;
            }

            spinner.Visible = true;
            spinner.AutoSpin = true;
            status.Text = dryRun ? " running dry-run…" : " updating…";

            var options = new GhSkillUpdateService.Options(
                Skills: skills,
                All: allChecked,
                DryRun: dryRun,
                Force: forceBox.Value == CheckState.Checked,
                Unpin: unpinBox.Value == CheckState.Checked,
                Yes: yesChecked,
                Json: false);

            try
            {
                var result = await _update.UpdateAsync(
                    _ghPath, _capabilities, options).ConfigureAwait(false);
                _app.Invoke(() =>
                {
                    LastResult = result;
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    preview.Text = RenderResult(result, dryRun, allChecked, skills);
                    if (dryRun)
                    {
                        if (!result.Succeeded)
                        {
                            status.Text = $" dry-run failed (exit {result.ExitCode}): {TuiHelpers.ErrorSnippet(result.ErrorMessage)}";
                        }
                        else if (string.IsNullOrWhiteSpace(result.StdOut))
                        {
                            status.Text = " dry-run complete · no updates available";
                        }
                        else
                        {
                            status.Text = $" dry-run complete · {result.Entries.Length} entries parsed";
                        }
                    }
                    else if (result.Succeeded)
                    {
                        status.Text = $" update succeeded — closing";
                        _app.RequestStop();
                    }
                    else
                    {
                        var snippet = TuiHelpers.ErrorSnippet(result.ErrorMessage);
                        status.Text = snippet.Length > 0
                            ? $" update failed (exit {result.ExitCode}): {snippet}"
                            : $" update failed (exit {result.ExitCode}) — see logs";
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("update", ex.Message);
                _app.Invoke(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    var snippet = TuiHelpers.ErrorSnippet(ex.Message);
                    status.Text = snippet.Length > 0
                        ? $" update failed: {snippet}"
                        : " update failed — see logs";
                });
            }
        }

        dryRunButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _ = RunAsync(dryRun: true);
        };
        updateButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _ = RunAsync(dryRun: false);
        };
        cancelButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _app.RequestStop();
        };

        window.Add(
            tableLabel, table, preview,
            allBox, forceBox, unpinBox, yesBox,
            status, spinner,
            dryRunButton, updateButton, cancelButton,
            statusBar);

        window.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                _app.RequestStop();
                key.Handled = true;
            }
        };

        updateButton.SetFocus();
        _app.Run(window);
    }

    private static string RenderResult(UpdateResult result, bool dryRun, bool allChecked, List<string> skills)
    {
        var sb = new System.Text.StringBuilder();
        var heading = dryRun ? "Dry-run results" : "Update results";
        sb.AppendLine($"## {heading}");
        sb.AppendLine();
        var scope = allChecked
            ? "_all skills_"
            : skills.Count == 1
                ? $"`{skills[0]}`"
                : $"{skills.Count} skills";
        sb.AppendLine($"**Scope:** {scope}  ·  **Exit:** {result.ExitCode}  ·  **Entries:** {result.Entries.Length}");
        sb.AppendLine();
        if (!result.Succeeded)
        {
            sb.AppendLine($"### ⛔ Failed");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                sb.AppendLine("```");
                sb.AppendLine(result.ErrorMessage.TrimEnd());
                sb.AppendLine("```");
            }
            return sb.ToString();
        }
        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            sb.AppendLine(dryRun
                ? "_No updates available for the selected skill(s)._"
                : "_Completed with no output._");
            return sb.ToString();
        }
        sb.AppendLine("```");
        sb.AppendLine(result.StdOut.TrimEnd());
        sb.AppendLine("```");
        return sb.ToString();
    }
}
