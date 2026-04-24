using System.Collections.Immutable;
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

/// Phase 6 cleanup screen. Lists cleanup candidates with a Space-to-toggle
/// checkbox column (same pattern as `UpdateScreen`), and surfaces the cleanup
/// actions: remove, mark ignored, rescan, export.
public sealed class CleanupScreen
{
    private readonly IApplication _app;
    private readonly RemoveService _remove;
    private readonly Logger _logger;
    private readonly ImmutableArray<CleanupClassifier.Candidate> _candidates;
    private readonly IReadOnlyList<ScanRoot> _scanRoots;
    private readonly IReadOnlyList<InstalledSkill> _allSkills;

    public int RemovedCount { get; private set; }
    public int IgnoredCount { get; private set; }

    public CleanupScreen(
        IApplication app,
        RemoveService remove,
        Logger logger,
        ImmutableArray<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<ScanRoot> scanRoots,
        IReadOnlyList<InstalledSkill> allSkills)
    {
        _app = app;
        _remove = remove;
        _logger = logger;
        _candidates = candidates;
        _scanRoots = scanRoots;
        _allSkills = allSkills;
    }

    public void Show()
    {
        using var dialog = new Dialog
        {
            Title = $"Cleanup — {_candidates.Length} candidate(s)",
            Width = Dim.Percent(90),
            Height = Dim.Percent(90),
        };

        var header = new Label
        {
            X = 0, Y = 0,
            Text = "Space toggles row. R remove, I ignore, X export report, Esc close",
        };

        var checkStates = new bool[_candidates.Length];
        var table = new TableView
        {
            X = 0, Y = 1,
            Width = Dim.Percent(55),
            Height = Dim.Fill(2),
            FullRowSelect = true,
        };
        table.Table = new EnumerableTableSource<(int Idx, CleanupClassifier.Candidate C)>(
            _candidates.Select((c, i) => (i, c)).ToList(),
            new Dictionary<string, Func<(int Idx, CleanupClassifier.Candidate C), object>>
            {
                [" "] = row => checkStates[row.Idx] ? "✓" : " ",
                ["Kind"] = row => TuiHelpers.ShortKind(row.C.Kind),
                ["Name"] = row => row.C.Skill?.Name ?? System.IO.Path.GetFileName(row.C.Path),
                ["Path"] = row => TuiHelpers.ShortenPath(row.C.Path),
            });

        var detail = new TextView
        {
            X = Pos.Right(table) + 1, Y = 1,
            Width = Dim.Fill(), Height = Dim.Fill(2),
            ReadOnly = true, WordWrap = true,
            Text = _candidates.Length == 0 ? "(no cleanup candidates)" : RenderDetail(_candidates[0]),
        };

        table.SelectedCellChanged += (_, _) =>
        {
            var i = table.SelectedRow;
            if (i >= 0 && i < _candidates.Length) detail.Text = RenderDetail(_candidates[i]);
        };

        var status = new Label
        {
            X = 0, Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Text = _candidates.Length == 0
                ? " no cleanup candidates"
                : $" {_candidates.Length} candidate(s) — R remove selected, I ignore selected, X export report",
        };

        // Space-to-toggle.
        table.KeyDown += (_, key) =>
        {
            if (key.AsRune.Value == ' ' && _candidates.Length > 0)
            {
                var idx = table.SelectedRow;
                if (idx >= 0 && idx < _candidates.Length)
                {
                    checkStates[idx] = !checkStates[idx];
                    table.SetNeedsDraw();
                    key.Handled = true;
                }
            }
        };

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                _app.RequestStop();
                key.Handled = true;
                return;
            }
            var r = key.AsRune.Value;
            if (r == 'r' || r == 'R') { DoRemove(checkStates, status); key.Handled = true; }
            else if (r == 'i' || r == 'I') { DoIgnore(checkStates, status); key.Handled = true; }
            else if (r == 'x' || r == 'X') { DoExport(status); key.Handled = true; }
        };

        dialog.Add(header, table, detail, status);
        table.SetFocus();
        _app.Run(dialog);
    }

    private void DoRemove(bool[] checkStates, Label status)
    {
        var removed = 0;
        var failed = 0;
        for (var i = 0; i < _candidates.Length; i++)
        {
            if (!checkStates[i]) continue;
            var c = _candidates[i];
            // For skill-backed candidates, run full validator. For empty dirs,
            // build a synthetic `Allowed` validation (empty dir is safe when
            // under a known scan root).
            RemoveValidator.RemoveValidation validation;
            if (c.Skill is not null)
            {
                validation = RemoveValidator.Validate(c.Skill, _scanRoots, _allSkills);
            }
            else
            {
                validation = ValidateEmptyDir(c.Path);
            }
            if (!validation.Allowed || validation.RequiresSecondConfirm)
            {
                failed++;
                _logger.Warn("cleanup", $"skipped {c.Path}: {(validation.Allowed ? "needs second confirm" : "validation refused")}");
                continue;
            }
            try
            {
                var report = _remove.Remove(validation);
                if (report.Succeeded) removed++; else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.Error("cleanup.remove", $"{c.Path}: {ex.Message}");
            }
        }
        RemovedCount += removed;
        status.Text = $" removed {removed}, skipped/failed {failed}";
    }

    private void DoIgnore(bool[] checkStates, Label status)
    {
        var marked = 0;
        for (var i = 0; i < _candidates.Length; i++)
        {
            if (!checkStates[i]) continue;
            var c = _candidates[i];
            var dir = c.Skill?.ResolvedPath ?? c.Path;
            if (!System.IO.Directory.Exists(dir)) continue;
            try
            {
                if (IgnoreMarker.Write(dir, _logger)) marked++;
            }
            catch (Exception ex)
            {
                _logger.Error("cleanup.ignore", $"{dir}: {ex.Message}");
            }
        }
        IgnoredCount += marked;
        status.Text = $" marked {marked} directory(ies) as ignored";
    }

    private void DoExport(Label status)
    {
        try
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"skillview-cleanup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.txt");
            System.IO.File.WriteAllText(path, RenderReport());
            _logger.Info("cleanup.export", $"wrote {path}");
            status.Text = $" exported report → {path}";
        }
        catch (Exception ex)
        {
            _logger.Error("cleanup.export", ex.Message);
            status.Text = " export failed — see logs";
        }
    }

    private string RenderReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# SkillView cleanup report — {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"candidates: {_candidates.Length}");
        foreach (var c in _candidates)
        {
            sb.AppendLine();
            sb.AppendLine($"- kind : {c.Kind}");
            sb.AppendLine($"  path : {c.Path}");
            sb.AppendLine($"  why  : {c.Reason}");
        }
        return sb.ToString();
    }

    private static string RenderDetail(CleanupClassifier.Candidate c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"kind   : {c.Kind}");
        sb.AppendLine($"path   : {c.Path}");
        sb.AppendLine($"reason : {c.Reason}");
        if (c.Skill is { } s)
        {
            sb.AppendLine();
            sb.AppendLine(InstalledScreen.RenderDetail(s));
        }
        return sb.ToString();
    }

    private RemoveValidator.RemoveValidation ValidateEmptyDir(string path)
    {
        // Empty-dir candidates don't look like skills (no SKILL.md), so the
        // standard validator would refuse. Build a scoped validation that
        // only enforces scan-root containment + no-.git.
        var errors = ImmutableArray.CreateBuilder<RemoveValidator.Error>();
        var inside = false;
        foreach (var root in _scanRoots)
        {
            if (PathResolver.IsInside(path, root.Path)) { inside = true; break; }
        }
        if (!inside)
        {
            errors.Add(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.OutsideKnownRoots,
                $"'{path}' not inside any scan root"));
        }
        if (System.IO.Directory.Exists(System.IO.Path.Combine(path, ".git")))
        {
            errors.Add(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.ContainsGitDirectory,
                $"'{path}' contains .git"));
        }
        return new RemoveValidator.RemoveValidation(
            errors.ToImmutable(),
            ImmutableArray<RemoveValidator.Warning>.Empty,
            path,
            ImmutableArray<string>.Empty);
    }
}
