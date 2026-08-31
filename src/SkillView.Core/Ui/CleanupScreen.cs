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
    internal sealed record RemovalSummary(int Removed, int Failed, bool Confirmed)
    {
        internal int Skipped { get; init; }
    }

    internal sealed class RemovalAttemptState
    {
        private int _preValidationSkipped;

        internal int PreValidationSkipped =>
            Volatile.Read(ref _preValidationSkipped);

        internal void SetPreValidationSkipped(int value) =>
            Volatile.Write(ref _preValidationSkipped, value);
    }

    private readonly IApplication _app;
    private readonly RemoveService _remove;
    private readonly Logger _logger;
    private readonly ImmutableArray<CleanupClassifier.Candidate> _candidates;
    private readonly IReadOnlyList<ScanRoot> _scanRoots;
    private readonly IReadOnlyList<InstalledSkill> _allSkills;
    private readonly Func<string, int> _confirmBatchRemoval;

    public int RemovedCount { get; private set; }
    public int RemovedFileCount { get; private set; }
    public int RemovedDirectoryCount { get; private set; }
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
        using var lifetime = new CancellationTokenSource();
        Task? activeOperation = null;
        RemoveService.RemoveProgress? lastProgress = null;
        var windowActive = 1;
        var closeAfterCancellation = 0;

        using var window = new Window
        {
            Title = $"Cleanup — {_candidates.Length} candidate(s)",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        var header = new Label
        {
            X = 0,
            Y = 0,
            Text = BuildHeaderText(),
        };

        var table = new TableView
        {
            X = 0,
            Y = 1,
            Width = Dim.Percent(55),
            Height = Dim.Fill(3),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(table);
        TuiHelpers.ConfigureTableChrome(table);

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
            X = Pos.Right(table) + 1,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
            Text = _candidates.Length == 0 ? "(no cleanup candidates)" : RenderDetail(_candidates[0]),
        };
        TuiHelpers.ConfigureMarkdownPane(detail, SkillViewStyling.BaseSchemeName);

        table.ValueChanged += (_, _) =>
        {
            var i = table.GetSelectedRow();
            if (i >= 0 && i < _candidates.Length) detail.Text = RenderDetail(_candidates[i]);
        };

        var status = new Label
        {
            X = 2,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(2),
            Text = _candidates.Length == 0
                ? " no cleanup candidates"
                : $" {_candidates.Length} candidate(s)",
        };
        var spinner = new SpinnerView
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Visible = false,
            AutoSpin = false,
        };

        var statusBar = new StatusBar(TuiHelpers.WithMarkdownShortcuts(
            BuildShortcuts(),
            includeOpenLink: false));

        TuiHelpers.ApplyScheme(SkillViewStyling.BaseSchemeName,
            window, header, table, detail, spinner, status, statusBar);

        void InvokeIfActive(Action action)
        {
            if (Volatile.Read(ref windowActive) == 0)
            {
                return;
            }

            try
            {
                _app.Invoke(() =>
                {
                    if (Volatile.Read(ref windowActive) == 0)
                    {
                        return;
                    }

                    try { action(); }
                    catch (Exception ex) { _logger.Error("cleanup.ui", ex.Message); }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("cleanup.ui", ex.Message);
            }
        }

        async Task RunRemovalAsync(HashSet<int> selectedRows, CancellationToken cancellationToken)
        {
            var selectedCount = selectedRows.Count(index => index >= 0 && index < _candidates.Length);
            var attemptState = new RemovalAttemptState();
            var progress = new CallbackProgress<RemoveService.RemoveProgress>(value =>
            {
                lastProgress = value;
                InvokeIfActive(() => status.Text = FormatProgress(value));
            });

            try
            {
                var summary = await RemoveSelectedAsync(
                        selectedRows,
                        cancellationToken,
                        progress,
                        attemptState)
                    .ConfigureAwait(false);
                InvokeIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    activeOperation = null;
                    status.Text = summary.Confirmed
                        ? $" removed {summary.Removed}, skipped {summary.Skipped}, failed {summary.Failed}"
                        : " cleanup removal canceled";
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var removed = lastProgress?.TargetsDeleted ?? 0;
                RemovedCount += removed;
                RemovedFileCount += lastProgress?.FilesProcessed ?? 0;
                RemovedDirectoryCount += lastProgress?.DirectoriesProcessed ?? 0;
                var skipped = attemptState.PreValidationSkipped;
                var failed = CountFailedSelections(
                    selectedCount,
                    removed,
                    skipped);
                _logger.Debug("cleanup.remove", "cleanup removal canceled");
                InvokeIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    status.Text = $" removal canceled after {removed}; skipped {skipped}, failed {failed}";
                    if (Volatile.Read(ref closeAfterCancellation) != 0)
                    {
                        _app.RequestStop();
                    }
                    else
                    {
                        activeOperation = null;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("cleanup.remove", ex.Message);
                InvokeIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    activeOperation = null;
                    status.Text = " cleanup removal failed — see logs";
                });
            }
        }

        window.KeyDown += (_, key) =>
        {
            var r = key.AsRune.Value;
            if (r == 'r' || r == 'R')
            {
                key.Handled = true;
                if (activeOperation is not null)
                {
                    return;
                }

                var selectedRows = wrapper.CheckedRows
                    .Where(index => index >= 0 && index < _candidates.Length)
                    .ToHashSet();
                if (selectedRows.Count == 0)
                {
                    status.Text = " no cleanup candidates selected";
                    return;
                }
                spinner.Visible = true;
                spinner.AutoSpin = true;
                lastProgress = null;
                status.Text = " removing…  Esc cancels";
                var cancellationToken = lifetime.Token;
                activeOperation = RunRemovalAsync(selectedRows, cancellationToken);
            }
            else if ((r == 'i' || r == 'I') && activeOperation is null)
            {
                DoIgnore(wrapper.CheckedRows, status);
                key.Handled = true;
            }
            else if ((r == 'x' || r == 'X') && activeOperation is null)
            {
                DoExport(status);
                key.Handled = true;
            }
            else if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                if (activeOperation is { IsCompleted: false })
                {
                    Interlocked.Exchange(ref closeAfterCancellation, 1);
                    lifetime.Cancel();
                    status.Text = " canceling removal…";
                    return;
                }

                lifetime.Cancel();
                _app.RequestStop();
            }
        };

        window.Add(header, table, detail, spinner, status, statusBar);
        table.SetFocus();
        try
        {
            _app.Run(window);
        }
        finally
        {
            Interlocked.Exchange(ref windowActive, 0);
            lifetime.Cancel();
            activeOperation?.GetAwaiter().GetResult();
        }
    }

    internal async Task<RemovalSummary> RemoveSelectedAsync(
        HashSet<int> checkedRows,
        CancellationToken cancellationToken = default,
        IProgress<RemoveService.RemoveProgress>? progress = null,
        RemovalAttemptState? attemptState = null)
    {
        var selected = checkedRows
            .Where(i => i >= 0 && i < _candidates.Length)
            .Select(i => _candidates[i])
            .ToImmutableArray();
        if (selected.IsDefaultOrEmpty)
        {
            return new RemovalSummary(Removed: 0, Failed: 0, Confirmed: false);
        }

        var response = _confirmBatchRemoval(BuildRemoveConfirmationText(selected));
        if (response != 1)
        {
            return new RemovalSummary(Removed: 0, Failed: 0, Confirmed: false);
        }

        // Resolve every selected key before the first lazy validation/deletion,
        // and publish the skip count outside this worker so cancellation can
        // still account for duplicates when RemoveManyAsync throws.
        var selection = CleanupClassifier.DeduplicateByPath(
            selected,
            cancellationToken);
        var skippedBeforeValidation = selection.Duplicates.Length;
        attemptState?.SetPreValidationSkipped(skippedBeforeValidation);
        foreach (var candidate in selection.Duplicates)
        {
            _logger.Debug(
                "cleanup",
                $"skipped duplicate selected path {candidate.Path}");
        }

        var report = await _remove.RemoveManyAsync(
            ValidateImmediatelyBeforeRemoval(
                selection.Unique,
                cancellationToken),
            cancellationToken: cancellationToken,
            progress: progress).ConfigureAwait(false);
        if (skippedBeforeValidation > 0)
        {
            report = report with
            {
                TargetsSkipped = checked(
                    report.TargetsSkipped + skippedBeforeValidation),
            };
        }
        var removed = report.TargetsDeleted;
        var failed = CountFailedSelections(selected.Length, report);
        RemovedCount += removed;
        RemovedFileCount += report.FilesDeleted;
        RemovedDirectoryCount += report.DirectoriesDeleted;
        return new RemovalSummary(removed, failed, Confirmed: true)
        {
            Skipped = report.TargetsSkipped,
        };
    }

    internal static int CountFailedSelections(
        int selectedCount,
        RemoveService.BatchRemoveReport report) =>
        CountFailedSelections(
            selectedCount,
            report.TargetsDeleted,
            report.TargetsSkipped);

    internal static int CountFailedSelections(
        int selectedCount,
        int removedCount,
        int skippedCount) =>
        Math.Max(0, selectedCount - removedCount - skippedCount);

    private IEnumerable<RemoveValidator.RemoveValidation>
        ValidateImmediatelyBeforeRemoval(
            ImmutableArray<CleanupClassifier.Candidate> unique,
            CancellationToken cancellationToken)
    {
        foreach (var candidate in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // RemoveMany enumerates this sequence immediately before each
            // target executes. Do not materialize it: deleting an earlier
            // sibling changes the shared parent's native generation, so later
            // link identities must be captured after that owned mutation.
            var validation = candidate.Kind switch
            {
                CleanupClassifier.CandidateKind.BrokenSymlink =>
                    RemoveValidator.ValidateBrokenSymlink(candidate.Path, _scanRoots),
                CleanupClassifier.CandidateKind.EmptyDirectory =>
                    RemoveValidator.ValidateEmptyDirectory(candidate.Path, _scanRoots),
                _ when candidate.Skill is not null =>
                    RemoveValidator.Validate(candidate.Skill, _scanRoots, _allSkills),
                _ => RefuseUnsupportedCandidate(candidate),
            };
            if (!validation.Allowed || validation.RequiresSecondConfirm)
            {
                _logger.Warn("cleanup", $"skipped {candidate.Path}: {(validation.Allowed ? "needs second confirm" : "validation refused")}");
                continue;
            }
            yield return validation;
        }
    }

    private static string FormatProgress(RemoveService.RemoveProgress progress) =>
        progress.IsCanceled
            ? $" canceling… removed {progress.TargetsDeleted} target(s)"
            : $" removing… {progress.TargetsProcessed} target(s), {progress.FilesProcessed} file(s), {progress.DirectoriesProcessed} dir(s)  Esc cancels";

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
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    internal static string RenderDetail(CleanupClassifier.Candidate c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Selected");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Kind | **{MarkdownTableFormatter.FormatTableCell(c.Kind.ToString())}** |");
        sb.AppendLine($"| Path | {MarkdownTableFormatter.FormatCodeSpan(c.Path)} |");
        sb.AppendLine($"| Reason | {MarkdownTableFormatter.FormatTableCell(c.Reason)} |");
        if (c.Skill is { } s)
        {
            sb.AppendLine();
            sb.AppendLine("## Skill");
            sb.AppendLine();
            sb.AppendLine(InstalledScreen.RenderDetail(s));
        }
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    internal static Shortcut[] BuildShortcutsForTests() => BuildShortcuts();

    internal static string BuildHeaderTextForTests() => BuildHeaderText();

    private static Shortcut[] BuildShortcuts() =>
    [
        new Shortcut { Title = "Space", HelpText = "Select" },
        new Shortcut { Title = "r", HelpText = "Remove" },
        new Shortcut { Title = "i", HelpText = "Ignore" },
        new Shortcut { Title = "x", HelpText = "Export" },
        new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Back" },
    ];

    private static string BuildHeaderText() => "Select with Space.";

    private static RemoveValidator.RemoveValidation RefuseUnsupportedCandidate(CleanupClassifier.Candidate candidate) =>
        new(
            ImmutableArray.Create(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.NotASkillDirectory,
                $"cleanup candidate kind '{candidate.Kind}' requires installed-skill metadata")),
            ImmutableArray<RemoveValidator.Warning>.Empty,
            candidate.Path,
            ImmutableArray<string>.Empty);

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
