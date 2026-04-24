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

        var summary = new Markdown
        {
            X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(4),
            Text = BuildSummary(),
        };
        TuiHelpers.ConfigureMarkdownPane(summary, "Base");

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

        var statusBar = new StatusBar(
        [
            new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Cancel" },
        ]);

        TuiHelpers.ApplyScheme("Base", window, summary, secondConfirm, status, removeButton, cancelButton, statusBar);

        removeButton.Accepting += (_, ev) =>
        {
            ev.Handled = true;
            if (!_validation.Allowed) return;
            if (_validation.RequiresSecondConfirm && secondConfirm.Value != CheckState.Checked)
            {
                status.Text = " second-confirm required (check the box)";
                return;
            }
            Confirmed = true;
            status.Text = " removing…";
            try
            {
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
        window.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                _app.RequestStop();
                key.Handled = true;
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
