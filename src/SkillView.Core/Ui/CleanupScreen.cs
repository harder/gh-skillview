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
    private readonly Func<string, int> _confirmBatchRemoval;

    public int RemovedCount { get; private set; }
    public int IgnoredCount { get; private set; }

    public CleanupScreen(
        IApplication app,
        RemoveService remove,
        Logger logger,
        ImmutableArray<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<ScanRoot> scanRoots,
        IReadOnlyList<InstalledSkill> allSkills,
        Func<string, int>? confirmBatchRemoval = null)
    {
        _app = app;
        _remove = remove;
        _logger = logger;
        _candidates = candidates;
        _scanRoots = scanRoots;
        _allSkills = allSkills;
        _confirmBatchRemoval = confirmBatchRemoval ?? ConfirmBatchRemoval;
    }

    public void Show()
    {
        using var window = new Window
        {
            Title = $"Cleanup — {_candidates.Length} candidate(s)",
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        var header = new Label
        {
            X = 0, Y = 0,
            Text = "Space toggles row.",
        };

        var table = new TableView
        {
            X = 0, Y = 1,
            Width = Dim.Percent(55),
            Height = Dim.Fill(3),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(table);

        // Width state shared between the column-projection lambdas and the
        // resize handler. Mutating these does not require rebuilding the
        // table source; the closures re-read on every render.
        var widths = new ColumnWidths { Name = 12, Path = 15 };
        var rowsList = _candidates.Select((c, i) => (i, c)).ToList();
        var inner = new EnumerableTableSource<(int Idx, CleanupClassifier.Candidate C)>(
            rowsList,
            new Dictionary<string, Func<(int Idx, CleanupClassifier.Candidate C), object>>
            {
                ["Kind"] = row => TuiHelpers.ShortKind(row.C.Kind),
                ["Name"] = row => TuiHelpers.Truncate(
                    row.C.Skill?.Name ?? System.IO.Path.GetFileName(row.C.Path),
                    widths.Name),
                ["Path"] = row => TuiHelpers.Truncate(TuiHelpers.ShortenPath(row.C.Path), widths.Path),
            });
        // RC5's CheckBoxTableSourceWrapperByIndex inserts the checkbox column,
        // hooks Space-to-toggle and click-to-toggle on the table, and tracks
        // checked rows in a HashSet<int> we can read directly. Replaces the
        // old manual `bool[] checkStates` + " " column + Space KeyDown handler.
        var wrapper = new CheckBoxTableSourceWrapperByIndex(table, inner);
        table.Table = wrapper;
        var style = table.Style;
        style.ExpandLastColumn = true;
        // Wrapper inserts " " at column 0, so Name is column 2, Path column 3.
        var nameStyle = style.GetOrCreateColumnStyle(2);
        nameStyle.MinWidth = 8;
        var pathStyle = style.GetOrCreateColumnStyle(3);
        pathStyle.MinWidth = 10;

        void Recompute()
        {
            var viewportWidth = table.Viewport.Width;
            var available = viewportWidth > 0 ? Math.Max(40, viewportWidth - 4) : 70;
            // Fixed: checkbox(1) + Kind(11). Remainder split Name (35%) / Path (65%).
            var remaining = Math.Max(20, available - 1 - 11);
            widths.Name = Math.Max(12, (int)Math.Round(remaining * 0.35));
            widths.Path = Math.Max(15, remaining - widths.Name);
            nameStyle.MaxWidth = widths.Name;
            table.Update();
        }
        Recompute();
        var lastCleanupWidth = -1;
        table.FrameChanged += (_, _) =>
        {
            var w = table.Viewport.Width;
            if (w > 0 && w != lastCleanupWidth)
            {
                lastCleanupWidth = w;
                Recompute();
            }
        };

        var detail = new Markdown
        {
            X = Pos.Right(table) + 1, Y = 1,
            Width = Dim.Fill(), Height = Dim.Fill(3),
            Text = _candidates.Length == 0 ? "(no cleanup candidates)" : RenderDetail(_candidates[0]),
        };
        TuiHelpers.ConfigureMarkdownPane(detail, "Base");

        table.ValueChanged += (_, _) =>
        {
            var i = table.GetSelectedRow();
            if (i >= 0 && i < _candidates.Length) detail.Text = RenderDetail(_candidates[i]);
        };

        var status = new Label
        {
            X = 0, Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Text = _candidates.Length == 0
                ? " no cleanup candidates"
                : $" {_candidates.Length} candidate(s)",
        };

        var statusBar = new StatusBar(
        [
            new Shortcut { Title = "Space", HelpText = "Toggle" },
            new Shortcut { Title = "r", HelpText = "Remove" },
            new Shortcut { Title = "i", HelpText = "Ignore" },
            new Shortcut { Title = "x", HelpText = "Export" },
            new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Back" },
        ]);

        TuiHelpers.ApplyScheme("Base", window, header, table, detail, status, statusBar);

        // Space-to-toggle is wired by CheckBoxTableSourceWrapperByIndex in
        // its constructor; no manual handler needed here. RC5 also routes
        // letter shortcuts through table.KeyDown so we still need to catch
        // them before the table swallows them in OnKeyDownNotHandled.
        table.KeyDown += (_, key) =>
        {
            var r = key.AsRune.Value;
            if (r == 'r' || r == 'R') { DoRemove(wrapper.CheckedRows, status); key.Handled = true; }
            else if (r == 'i' || r == 'I') { DoIgnore(wrapper.CheckedRows, status); key.Handled = true; }
            else if (r == 'x' || r == 'X') { DoExport(status); key.Handled = true; }
        };

        window.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                _app.RequestStop();
                key.Handled = true;
            }
        };

        window.Add(header, table, detail, status, statusBar);
        table.SetFocus();
        _app.Run(window);
    }

    private void DoRemove(HashSet<int> checkedRows, Label status)
    {
        var selected = checkedRows
            .Where(i => i >= 0 && i < _candidates.Length)
            .Select(i => _candidates[i])
            .ToImmutableArray();
        if (selected.IsDefaultOrEmpty)
        {
            status.Text = " no cleanup candidates selected";
            return;
        }

        var response = _confirmBatchRemoval(BuildRemoveConfirmationText(selected));
        if (response != 1)
        {
            status.Text = " cleanup removal canceled";
            return;
        }

        var removed = 0;
        var failed = 0;
        for (var i = 0; i < _candidates.Length; i++)
        {
            if (!checkedRows.Contains(i)) continue;
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

    internal static string BuildRemoveConfirmationText(
        IReadOnlyList<CleanupClassifier.Candidate> selected)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Remove {selected.Count} cleanup candidate(s)?");
        sb.AppendLine();
        foreach (var group in selected
                     .GroupBy(candidate => candidate.Kind)
                     .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal))
        {
            sb.AppendLine($"- {TuiHelpers.ShortKind(group.Key)}: {group.Count()}");
        }

        sb.AppendLine();
        sb.AppendLine("Paths:");
        foreach (var candidate in selected.Take(3))
        {
            sb.AppendLine($"- {candidate.Path}");
        }

        if (selected.Count > 3)
        {
            sb.AppendLine($"- …and {selected.Count - 3} more");
        }

        return sb.ToString().TrimEnd();
    }

    private void DoIgnore(HashSet<int> checkedRows, Label status)
    {
        var marked = 0;
        for (var i = 0; i < _candidates.Length; i++)
        {
            if (!checkedRows.Contains(i)) continue;
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
        sb.AppendLine("## Candidate");
        sb.AppendLine();
        sb.AppendLine($"**kind**: **{c.Kind}**  ");
        sb.AppendLine($"**path**: `{c.Path}`  ");
        sb.AppendLine($"**reason**: {c.Reason}  ");
        if (c.Skill is { } s)
        {
            sb.AppendLine();
            sb.AppendLine("---");
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

    private sealed class ColumnWidths
    {
        public int Name;
        public int Path;
    }

    private int ConfirmBatchRemoval(string message) =>
        MessageBox.Query(
            _app,
            "Confirm cleanup removal",
            message,
            "Cancel",
            "Remove") ?? 0;
}
