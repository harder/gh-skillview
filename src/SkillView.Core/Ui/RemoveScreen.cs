using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Phase 6 safe-remove dialog. Renders the `RemoveValidator` output, blocks
/// when errors are present, and requires a second confirmation when the
/// validator reports warnings (tracked-by-git / incoming-symlinks).
public sealed class RemoveScreen
{
    private readonly IApplication _app;
    private readonly RemoveService _remove;
    private readonly Logger _logger;
    private readonly InstalledSkill _target;
    private readonly RemoveValidator.RemoveValidation _validation;

    public RemoveService.RemoveReport? LastReport { get; private set; }
    public bool Confirmed { get; private set; }

    public RemoveScreen(
        IApplication app,
        RemoveService remove,
        Logger logger,
        InstalledSkill target,
        RemoveValidator.RemoveValidation validation)
    {
        _app = app;
        _remove = remove;
        _logger = logger;
        _target = target;
        _validation = validation;
    }

    public void Show()
    {
        using var window = new Window
        {
            Title = $"Remove — {_target.Name}",
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        // Picker: "All agents" + one row per AgentMembership. With 0 agents
        // the picker degenerates to just "All", so skip the radio entirely.
        var pickerLabels = new List<string> { "_All agents (full skill removal)" };
        foreach (var a in _target.Agents)
        {
            var kind = a.IsSymlink ? "symlink" : "direct ⚠ canonical";
            pickerLabels.Add($"{a.AgentId} — {kind}: {TuiHelpers.Truncate(a.Path, 48)}");
        }
        var hasAgentChoice = _target.Agents.Length > 0;
        var pickerRows = hasAgentChoice ? pickerLabels.Count + 1 /* label */ : 0;
        var bottomReserve = 4 + pickerRows;

        var summary = new Markdown
        {
            X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(bottomReserve),
            Text = BuildSummary(),
        };
        TuiHelpers.ConfigureMarkdownPane(summary, "Base");

        Label? pickerLabel = null;
        OptionSelector? agentPicker = null;
        // Track the picker's selected index ourselves; OptionSelector exposes
        // `Value` as the label string, and 1–9/a keys need to drive selection.
        var selectedAgent = 0;
        if (hasAgentChoice)
        {
            pickerLabel = new Label
            {
                X = 0, Y = Pos.AnchorEnd(pickerRows + 3),
                Text = "Remove from (1-9 / a):",
            };
            agentPicker = new OptionSelector
            {
                X = 0, Y = Pos.AnchorEnd(pickerRows + 2),
                Labels = pickerLabels,
                Value = 0,
            };
            agentPicker.ValueChanged += (_, _) =>
            {
                if (agentPicker.Value is int idx) selectedAgent = idx;
            };
        }

        var secondConfirm = new CheckBox
        {
            X = 0, Y = Pos.AnchorEnd(3),
            Text = "_I understand the warnings and want to proceed",
            Visible = _validation.RequiresSecondConfirm,
        };

        var status = new Label
        {
            X = 0, Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(36),
            Text = BuildStatusLine(),
        };

        var removeButton = new Button
        {
            Text = "_Remove",
            X = Pos.AnchorEnd(30),
            Y = Pos.AnchorEnd(2),
            Enabled = _validation.Allowed,
        };
        var cancelButton = new Button
        {
            Text = "_Cancel",
            X = Pos.AnchorEnd(12),
            Y = Pos.AnchorEnd(2),
            IsDefault = true,
        };

        var statusBar = hasAgentChoice
            ? new StatusBar(
            [
                new Shortcut { Title = "a", HelpText = "All" },
                new Shortcut { Title = "1-9", HelpText = "Pick agent" },
                new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Cancel" },
            ])
            : new StatusBar(
            [
                new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Cancel" },
            ]);

        TuiHelpers.ApplyScheme("Base", window, summary, secondConfirm, status, removeButton, cancelButton, statusBar);
        if (pickerLabel is not null) TuiHelpers.ApplyScheme("Base", pickerLabel);
        if (agentPicker is not null) TuiHelpers.ApplyScheme("Base", agentPicker);

        removeButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            var pick = selectedAgent;
            // Per-agent symlink removal doesn't trigger the canonical
            // validator's safety rules (those guard the canonical path).
            // Direct memberships fall back to the canonical removal flow,
            // which means they DO need _validation.Allowed.
            var perAgent = pick > 0 && pick - 1 < _target.Agents.Length
                ? _target.Agents[pick - 1]
                : (AgentMembership?)null;
            var canonicalPath = perAgent is null || !perAgent.IsSymlink;
            if (canonicalPath && !_validation.Allowed) return;
            if (canonicalPath && _validation.RequiresSecondConfirm && secondConfirm.Value != CheckState.Checked)
            {
                status.Text = " second-confirm required (check the box)";
                return;
            }
            Confirmed = true;
            status.Text = " removing…";
            try
            {
                if (perAgent is { IsSymlink: true } symlinkAgent)
                {
                    System.IO.File.Delete(symlinkAgent.Path);
                    _logger.Info("remove.agent", $"unlinked {symlinkAgent.AgentId}: {symlinkAgent.Path}");
                    status.Text = $" unlinked {symlinkAgent.AgentId} ({symlinkAgent.Path})";
                    LastReport = new RemoveService.RemoveReport(
                        Succeeded: true,
                        ResolvedPath: symlinkAgent.Path,
                        FilesDeleted: 1,
                        DirectoriesDeleted: 0,
                        Errors: System.Collections.Immutable.ImmutableArray<string>.Empty,
                        DryRun: false);
                    _app.RequestStop();
                    return;
                }
                var report = _remove.Remove(_validation);
                LastReport = report;
                if (report.Succeeded)
                {
                    status.Text = $" removed ({report.FilesDeleted} file(s), {report.DirectoriesDeleted} dir(s))";
                    _app.RequestStop();
                }
                else
                {
                    status.Text = $" remove failed — {report.Errors.Length} error(s); see logs";
                }
            }
            catch (Exception ex)
            {
                _logger.Error("remove", ex.Message);
                status.Text = " remove failed — see logs";
            }
        };
        cancelButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            _app.RequestStop();
        };

        window.Add(summary, secondConfirm, status, removeButton, cancelButton, statusBar);
        if (pickerLabel is not null) window.Add(pickerLabel);
        if (agentPicker is not null) window.Add(agentPicker);
        window.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                _app.RequestStop();
                key.Handled = true;
                return;
            }
            if (agentPicker is null) return;
            var rune = key.AsRune.Value;
            if (rune == 'a' || rune == 'A')
            {
                selectedAgent = 0;
                agentPicker.Value = 0;
                key.Handled = true;
            }
            else if (rune >= '1' && rune <= '9')
            {
                var idx = rune - '0'; // 1-based agent index
                if (idx <= _target.Agents.Length)
                {
                    selectedAgent = idx;
                    agentPicker.Value = idx;
                    key.Handled = true;
                }
            }
        };
        cancelButton.SetFocus();
        _app.Run(window);
    }

    private string BuildSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Remove — {_target.Name}");
        sb.AppendLine();
        sb.AppendLine($"**path**: `{_target.ResolvedPath}`  ");
        sb.AppendLine($"**resolved**: `{_validation.ResolvedPath}`  ");
        sb.AppendLine($"**scope**: {_target.Scope}  ");
        sb.AppendLine($"**symlink**: {_target.IsSymlinked}  ");
        sb.AppendLine($"**pinned**: {_target.Pinned}  ");
        if (_target.Agents.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Agents");
            sb.AppendLine();
            foreach (var a in _target.Agents)
                sb.AppendLine($"- **{a.AgentId}** ({(a.IsSymlink ? "symlink" : "direct")}) `{a.Path}`");
        }
        if (_validation.Errors.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### ⛔ REFUSED — safety rules triggered");
            sb.AppendLine();
            foreach (var e in _validation.Errors)
                sb.AppendLine($"- ✗ **{e.Kind}:** {e.Detail}");
        }
        if (_validation.Warnings.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### ⚠️ WARNINGS — second confirmation required");
            sb.AppendLine();
            foreach (var w in _validation.Warnings)
                sb.AppendLine($"- ! **{w.Kind}:** {w.Detail}");
            if (_validation.IncomingSymlinkPaths.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Incoming symlinks:**");
                sb.AppendLine();
                foreach (var link in _validation.IncomingSymlinkPaths)
                    sb.AppendLine($"- `{link}`");
            }
        }
        return sb.ToString();
    }

    private string BuildStatusLine()
    {
        if (!_validation.Allowed) return " refused — see errors above";
        if (_validation.RequiresSecondConfirm) return " review warnings, then check the box and press Remove";
        return " ready — press Remove to delete (irreversible)";
    }
}
