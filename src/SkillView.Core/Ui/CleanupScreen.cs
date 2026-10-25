using System.Collections.Immutable;
using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Threading;
using SkillView.Preferences;
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
        internal ImmutableArray<OperationImpact> Impacts { get; init; } = ImmutableArray<OperationImpact>.Empty;
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
    private readonly Func<IReadOnlyList<CleanupClassifier.Candidate>, int> _confirmBatchRemoval;
    private readonly SkillView.Subprocess.ProcessRunner? _ownershipRunner;
    private readonly Func<CancellationToken, Task<InventorySnapshot>>? _captureInventory;

    public int RemovedCount { get; private set; }
    public int RemovedFileCount { get; private set; }
    public int RemovedDirectoryCount { get; private set; }
    public int IgnoredCount { get; private set; }
    public bool PreferenceMutationObserved { get; private set; }
    public bool CancellationObserved { get; private set; }
    public RemoveService.BatchRemoveReport? LastReport { get; private set; }

    public CleanupScreen(
        IApplication app,
        RemoveService remove,
        Logger logger,
        ImmutableArray<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<ScanRoot> scanRoots,
        IReadOnlyList<InstalledSkill> allSkills,
        Func<string, int>? confirmBatchRemoval = null,
        SkillView.Subprocess.ProcessRunner? ownershipRunner = null,
        Func<CancellationToken, Task<InventorySnapshot>>? captureInventory = null)
    {
        _app = app;
        _remove = remove;
        _logger = logger;
        _candidates = candidates;
        _scanRoots = scanRoots;
        _allSkills = allSkills;
        _confirmBatchRemoval = confirmBatchRemoval is null
            ? ConfirmBatchRemoval
            : selected => confirmBatchRemoval(BuildRemoveConfirmationText(selected));
        _ownershipRunner = ownershipRunner;
        _captureInventory = captureInventory;
    }

    public void Show() => Show(null);

    internal void Show(Action<Window>? ready)
    {
        RemoveService.RemoveProgress? lastProgress = null;
        var closeAfterCancellation = 0;
        var showDetails = false;

        using var window = new Window
        {
            Title = $"Cleanup — {_candidates.Length} candidate(s)",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        using var operation = new ModalOperationTracker(_app, _logger, "cleanup.ignore");
        var readyToClose = false;
        bool Busy() => operation.CurrentOwnership != ModalOperationTracker.Ownership.None;

        var header = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
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
        TuiHelpers.ConfigureTableNavigation(table);
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
            if (i >= 0 && i < _candidates.Length) detail.Text = RenderDetail(_candidates[i], showDetails);
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

        var shortcuts = BuildShortcuts();
        var reviewShortcut = shortcuts.Single(shortcut => shortcut.Title == "Enter");
        var detailsShortcut = shortcuts.Single(shortcut => shortcut.Title == "F2");
        var statusBar = new StatusBar(shortcuts);
        // StatusBar sets shortcut alignment (and resets child margins) when
        // adding items. Compact them afterward, keeping a gap before each key.
        foreach (var shortcut in shortcuts)
        {
            if (shortcut.CommandView?.Margin is { } commandMargin) commandMargin.Thickness = new(0);
            if (shortcut.HelpView.Margin is { } helpMargin) helpMargin.Thickness = new(0, 0, 1, 0);
        }

        TuiHelpers.ApplyScheme(SkillViewStyling.BaseSchemeName,
            window, header, table, detail, spinner, status, statusBar);

        async Task RunRemovalAsync(HashSet<int> selectedRows, CancellationToken cancellationToken)
        {
            var selectedCount = selectedRows.Count(index => index >= 0 && index < _candidates.Length);
            var attemptState = new RemovalAttemptState();
            var progress = new CallbackProgress<RemoveService.RemoveProgress>(value =>
            {
                lastProgress = value;
                operation.InvokeIfActive(() => status.Text = FormatProgress(value));
            });

            try
            {
                var summary = await RemoveSelectedAsync(
                        selectedRows,
                        cancellationToken,
                        progress,
                        attemptState,
                        confirmationGranted: true)
                    .ConfigureAwait(false);
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    operation.Release();
                    status.Text = summary.Confirmed
                        ? $" removed {summary.Removed}, skipped {summary.Skipped}, failed {summary.Failed}. Esc returns."
                        : " cleanup removal canceled";
                    if (Volatile.Read(ref closeAfterCancellation) != 0) { readyToClose = true; _app.RequestStop(); }
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var removed = lastProgress?.TargetsDeleted ?? 0;
                var skipped = attemptState.PreValidationSkipped;
                var failed = CountFailedSelections(
                    selectedCount,
                    removed,
                    skipped);
                _logger.Debug("cleanup.remove", "cleanup removal canceled");
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    status.Text = $" removal canceled after {removed}; skipped {skipped}, failed {failed}";
                    if (Volatile.Read(ref closeAfterCancellation) != 0)
                    {
                        readyToClose = true; operation.Release(); _app.RequestStop();
                    }
                    else
                    {
                        operation.Release();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("cleanup.remove", ex.Message);
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false;
                    spinner.Visible = false;
                    operation.Release();
                    status.Text = " cleanup removal failed — see logs";
                    if (Volatile.Read(ref closeAfterCancellation) != 0) { readyToClose = true; _app.RequestStop(); }
                });
            }
        }

        void ReviewSelection()
        {
            if (Busy()) return;
            var selectedRows = wrapper.CheckedRows
                .Where(index => index >= 0 && index < _candidates.Length)
                .ToHashSet();
            if (selectedRows.Count == 0)
            {
                status.Text = " Select a row with Space, then press Enter to review.";
                return;
            }
            if (_confirmBatchRemoval(selectedRows.Select(index => _candidates[index]).ToImmutableArray()) != 1)
            { status.Text = " Review canceled; nothing removed."; return; }
            spinner.Visible = true;
            spinner.AutoSpin = true;
            lastProgress = null;
            status.Text = " removing…  Esc cancels";
            operation.TryStart(token => Task.Run(() => RunRemovalAsync(selectedRows, token)));
        }

        void ToggleSelection()
        {
            if (Busy()) return;
            var index = table.GetSelectedRow();
            if (index < 0 || index >= _candidates.Length) return;
            if (!wrapper.CheckedRows.Remove(index)) wrapper.CheckedRows.Add(index);
            table.Update();
        }

        void ToggleDetails()
        {
            showDetails = !showDetails;
            var i = table.GetSelectedRow();
            if (i >= 0 && i < _candidates.Length) detail.Text = RenderDetail(_candidates[i], showDetails);
            UpdateHints();
        }

        void UpdateHints()
        {
            var action = showDetails ? "Summary" : "Details";
            detailsShortcut.HelpText = action;
            reviewShortcut.Title = detail.HasFocus ? "r" : "Enter";
            header.Text = detail.HasFocus
                ? $"r Review · F2 {action} · Ctrl+A Select · Ctrl+C Copy"
                : BuildHeaderText().Replace("F2 Details", $"F2 {action}", StringComparison.Ordinal);
        }

        void IgnoreSelection()
        {
            if (Busy()) return;
            var selected = wrapper.CheckedRows.Where(index => index >= 0 && index < _candidates.Length)
                .Select(index => _candidates[index]).ToArray();
            if (selected.Length == 0) { status.Text = " no cleanup candidates selected"; return; }
            if (_captureInventory is null || _ownershipRunner is null)
            { status.Text = " fresh inventory unavailable; no preferences changed"; return; }
            spinner.Visible = true; spinner.AutoSpin = true;
            status.Text = " suppressing cleanup suggestions for selected physical installs across consumer contexts… Esc cancels";
            operation.TryStart(token => Task.Run(async () =>
            {
                string text;
                try
                {
                    var result = await CleanupSuppressionWorkflow.SetAsync(selected,
                        CleanupSuppressionStore.For(_ownershipRunner), _captureInventory,
                        () => PreferenceMutationObserved = true, () => IgnoredCount++, token).ConfigureAwait(false);
                    foreach (var error in result.Errors) _logger.Error("cleanup.ignore", error);
                    CancellationObserved |= result.Canceled;
                    text = $" ignored {result.Changed}, skipped {result.Skipped}, failed {result.Failed}" + (result.Canceled ? "; canceled" : "")
                        + (result.Errors.Length > 0 ? " — see logs" : "");
                }
                catch (Exception ex) { _logger.Error("cleanup.ignore", ex.Message); text = " preference update failed — see logs"; }
                operation.InvokeTerminalIfActive(() =>
                {
                    spinner.AutoSpin = false; spinner.Visible = false; status.Text = text;
                    operation.Release();
                    if (Volatile.Read(ref closeAfterCancellation) != 0) { readyToClose = true; _app.RequestStop(); }
                });
            }));
        }

        void Export()
        {
            if (!Busy()) DoExport(status);
        }

        void Close()
        {
            if (Busy())
            {
                Interlocked.Exchange(ref closeAfterCancellation, 1);
                operation.Cancel();
                status.Text = " canceling cleanup operation…";
                return;
            }
            readyToClose = true;
            _app.RequestStop();
        }

        void Bind(string title, Action action)
        {
            var shortcut = shortcuts.Single(shortcut => shortcut.Title == title);
            // Terminal.Gui invokes Action for Activate, but a programmatic
            // Accept takes its separate Accepting route. Consume that route so
            // both explicit commands run the action exactly once.
            shortcut.Action = action;
            shortcut.Accepting += (_, args) => { args.Handled = true; action(); };
        }
        Bind("Space", ToggleSelection);
        Bind("Enter", ReviewSelection);
        Bind("F2", ToggleDetails);
        Bind("i", IgnoreSelection);
        Bind("x", Export);
        Bind("Esc", Close);
        table.HasFocusChanged += (_, _) => UpdateHints();
        detail.HasFocusChanged += (_, _) => UpdateHints();
        // The window owns keyboard routing, including contextual Enter/Space.
        // Footer labels do not install competing global bindings; their actions
        // remain available to mouse and explicit command activation.

        window.KeyDown += (_, key) =>
        {
            if (key.Handled) return;
            var r = key.AsRune.Value;
            if (key.KeyCode == KeyCode.Enter && table.HasFocus)
            {
                key.Handled = true;
                ReviewSelection();
            }
            else if (key.KeyCode == KeyCode.F2)
            {
                key.Handled = true;
                ToggleDetails();
            }
            else if (r == 'r' || r == 'R')
            {
                key.Handled = true;
                ReviewSelection();
            }
            else if (r == 'i' || r == 'I')
            {
                key.Handled = true;
                IgnoreSelection();
            }
            else if (r == 'x' || r == 'X')
            {
                key.Handled = true;
                Export();
            }
            else if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                Close();
            }
        };
        window.IsRunningChanging += (_, args) =>
        {
            if (args.NewValue || readyToClose || !Busy()) return;
            args.Cancel = true; Interlocked.Exchange(ref closeAfterCancellation, 1);
            operation.Cancel(); status.Text = " canceling cleanup operation…";
        };

        window.Add(header, table, detail, spinner, status, statusBar);
        table.SetFocus();
        ready?.Invoke(window);
        try
        {
            _app.Run(window);
        }
        finally
        {
            operation.Cancel();
        }
    }

    internal async Task<RemovalSummary> RemoveSelectedAsync(
        HashSet<int> checkedRows,
        CancellationToken cancellationToken = default,
        IProgress<RemoveService.RemoveProgress>? progress = null,
        RemovalAttemptState? attemptState = null,
        bool confirmationGranted = false)
    {
        attemptState ??= new RemovalAttemptState();
        attemptState.SetPreValidationSkipped(0);
        var impacts = checkedRows.Where(index => index >= 0 && index < _candidates.Length)
            .Select(index => _candidates[index].Impact).OfType<OperationImpact>().ToImmutableArray();
        if (impacts.IsEmpty) impacts = ImmutableArray<OperationImpact>.Empty;
        LastReport = null;
        RemoveService.RemoveProgress? last = null;
        var capture = new CallbackProgress<RemoveService.RemoveProgress>(value => { last = value; progress?.Report(value); });
        try
        {
            var summary = await RemoveSelectedCoreAsync(checkedRows, cancellationToken, capture, attemptState, confirmationGranted).ConfigureAwait(false);
            if (LastReport is { } report) LastReport = report with { Impacts = impacts };
            return summary with { Impacts = impacts };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LastReport = RemovalReportState.Canceled(last) with { Impacts = impacts, TargetsSkipped = attemptState.PreValidationSkipped };
            throw;
        }
        catch (Exception ex)
        {
            LastReport = RemovalReportState.Failed(last, ex.Message) with { Impacts = impacts, TargetsSkipped = attemptState.PreValidationSkipped };
            throw;
        }
        finally
        {
            if (LastReport is { } report)
            {
                CancellationObserved |= report.IsCanceled;
                RemovedCount += report.TargetsDeleted;
                RemovedFileCount += report.FilesDeleted;
                RemovedDirectoryCount += report.DirectoriesDeleted;
            }
        }
    }

    private async Task<RemovalSummary> RemoveSelectedCoreAsync(
        HashSet<int> checkedRows, CancellationToken cancellationToken,
        IProgress<RemoveService.RemoveProgress>? progress, RemovalAttemptState? attemptState, bool confirmationGranted)
    {
        var selected = checkedRows
            .Where(i => i >= 0 && i < _candidates.Length)
            .Select(i => _candidates[i])
            .ToImmutableArray();
        if (selected.IsDefaultOrEmpty)
        {
            return new RemovalSummary(Removed: 0, Failed: 0, Confirmed: false);
        }

        var response = confirmationGranted ? 1 : _confirmBatchRemoval(selected);
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
        var skippedBeforeValidation = selection.Duplicates.Length + selection.Unique.Count(candidate => candidate.ReviewOnly);
        attemptState?.SetPreValidationSkipped(skippedBeforeValidation);
        foreach (var candidate in selection.Duplicates)
        {
            _logger.Debug(
                "cleanup",
                $"skipped duplicate selected path {candidate.Path}");
        }

        var report = await _remove.RemoveManyAsync(
            ValidateImmediatelyBeforeRemoval(
                selection.Unique.Where(candidate => !candidate.ReviewOnly).ToImmutableArray(),
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
        LastReport = report;
        var failed = CountFailedSelections(selected.Length, report);
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
            var validation = candidate.ReviewOnly ? RefuseUnsupportedCandidate(candidate) : candidate.Kind switch
            {
                CleanupClassifier.CandidateKind.BrokenSymlink =>
                    RemoveValidator.ValidateBrokenSymlink(candidate.Path, _scanRoots, cancellationToken, _ownershipRunner),
                CleanupClassifier.CandidateKind.EmptyDirectory =>
                    RemoveValidator.ValidateEmptyDirectory(candidate.Path, _scanRoots, cancellationToken, _ownershipRunner),
                _ when candidate.Skill is not null =>
                    RemoveValidator.Validate(candidate.Skill, _scanRoots, _allSkills, cancellationToken),
                _ => RefuseUnsupportedCandidate(candidate),
            };
            validation = validation with { Impact = validation.Impact ?? candidate.Impact };
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

    internal const int MaxConfirmationItems = 128;
    internal const int MaxConfirmationConsumers = 256;
    internal sealed record ConfirmationReview(string Text, bool Complete);

    internal static string BuildRemoveConfirmationText(
        IReadOnlyList<CleanupClassifier.Candidate> selected, bool showDetails = false) =>
        BuildConfirmationReview(selected, showDetails).Text;

    internal static ConfirmationReview BuildConfirmationReview(
        IReadOnlyList<CleanupClassifier.Candidate> selected, bool showDetails = false)
    {
        const string incomplete = "Review is incomplete. Select fewer items; removal is unavailable.";
        var budget = MaintenanceQueueFormatter.MaxDetailCharacters;
        var complete = selected.Count <= MaxConfirmationItems && CleanupReviewFormatter.FitsConfirmationBudget(
            selected, budget, MaxConfirmationConsumers);
        if (!showDetails && !complete)
            return new("## Selection too large to review\n\n" + incomplete, false);

        var text = new StringBuilder($"Review {selected.Count} cleanup item(s)\n\nCancel keeps all files. Deletion has no automatic undo.\n\n");
        foreach (var group in selected.Take(MaxConfirmationItems).GroupBy(CleanupReviewFormatter.Issue).OrderBy(group => group.Key, StringComparer.Ordinal))
            text.AppendLine($"- {group.Key}: {group.Count()}");
        text.AppendLine();
        // Exact summary fields are omitted whole; any omission closes approval.
        // Technical fields are also bounded, with their omissions made explicit.
        var fields = selected.Take(MaxConfirmationItems).SelectMany(candidate => CleanupReviewFormatter.ConfirmationFields(candidate, showDetails));
        var capturedFields = fields.Take(1025).ToArray();
        var rendered = MaintenanceQueueFormatter.RenderFields(capturedFields.Take(1024), CancellationToken.None, budget - text.Length - 512);
        complete &= capturedFields.Length <= 1024 && rendered.OmittedFields == 0;
        text.Append(rendered.Text);
        if (showDetails && (selected.Count > MaxConfirmationItems || capturedFields.Length > 1024))
            text.AppendLine("Additional items or fields were omitted from this technical preview.");
        if (!complete || showDetails)
            text.AppendLine(showDetails
                ? $"Technical preview is bounded: {rendered.OmittedFields} oversized fields omitted; at most {MaxConfirmationItems} items and 1024 fields are shown. Summary must be complete before removal. Additional distributor and reload evidence is not shown here."
                : incomplete);
        return new(TerminalEscapeSanitizer.Sanitize(text.ToString()) ?? "", complete);
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

    internal static string RenderDetail(CleanupClassifier.Candidate c, bool showDetails = false)
    {
        if (!showDetails) return TerminalEscapeSanitizer.Sanitize(CleanupReviewFormatter.Render(c)) ?? string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("## Technical details");
        sb.AppendLine();
        var fields = MaintenanceQueueFormatter.RenderFields(
            [("Kind", c.Kind.ToString()), ("Path", c.Path), ("Reason", c.Reason)],
            CancellationToken.None, MaintenanceQueueFormatter.MaxDetailCharacters - 256);
        sb.AppendLine(fields.Text);
        if (fields.OmittedFields > 0)
            sb.AppendLine($"Technical fields omitted from this bounded preview: {fields.OmittedFields}. Exact values are omitted whole, never shortened.");
        if (c.Impact is { } impact) OperationImpactFormatter.Append(sb, impact);
        if (c.Skill is { } s)
        {
            sb.AppendLine();
            sb.AppendLine("## Skill");
            sb.AppendLine();
            sb.AppendLine(InstalledScreen.RenderDetail(s, showDetails: true, showShortcuts: false));
        }
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    internal static Shortcut[] BuildShortcutsForTests() => BuildShortcuts();

    internal static string BuildHeaderTextForTests() => BuildHeaderText();

    private static Shortcut[] BuildShortcuts() =>
    [
        new Shortcut { Title = "Space", HelpText = "Select" },
        new Shortcut { Title = "Enter", HelpText = "Review" },
        new Shortcut { Title = "F2", HelpText = "Details" },
        new Shortcut { Title = "i", HelpText = "Ignore" },
        new Shortcut { Title = "x", HelpText = "Export" },
        new Shortcut { Title = "Esc", HelpText = "Back" },
    ];

    private static string BuildHeaderText() => "Space Select · Enter Review · F2 Details · Tab Scroll";

    private static RemoveValidator.RemoveValidation RefuseUnsupportedCandidate(CleanupClassifier.Candidate candidate) =>
        new(
            ImmutableArray.Create(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.NotASkillDirectory,
                $"cleanup candidate kind '{candidate.Kind}' requires comparison or ownership review, not bulk removal")),
            ImmutableArray<RemoveValidator.Warning>.Empty,
            candidate.Path,
            ImmutableArray<string>.Empty);

    private sealed class ColumnWidths
    {
        public int Name;
        public int Path;
    }

    private int ConfirmBatchRemoval(IReadOnlyList<CleanupClassifier.Candidate> selected)
    {
        var response = 0;
        var showDetails = false;
        var summary = BuildConfirmationReview(selected);
        ConfirmationReview? technical = null;
        using var dialog = new Dialog { Title = "Review cleanup removal", Width = Dim.Percent(90), Height = Dim.Percent(90) };
        var review = new Markdown
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(4),
            Text = summary.Text,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar
        };
        TuiHelpers.ConfigureMarkdownPane(review, SkillViewStyling.BaseSchemeName);
        var canRemove = summary.Complete && selected.Any(candidate => !candidate.ReviewOnly);
        var explanation = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(),
            Height = 1,
            Text = !summary.Complete ? "Review incomplete: select fewer items; removal is unavailable."
                : canRemove ? "Deletion has no automatic undo." : "Removal unavailable: inspect or repair these items first."
        };
        var hints = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, Text = "Tab Focus · PgDn Scroll · F2 Details" };
        var cancel = new Button { Text = "Cancel", X = Pos.Center() - 12, Y = Pos.AnchorEnd(1), IsDefault = true };
        var remove = new Button { Text = "Remove", X = Pos.Center() + 2, Y = Pos.AnchorEnd(1), Enabled = canRemove };
        cancel.Accepting += (_, args) => { args.Handled = true; _app.RequestStop(); };
        remove.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (!canRemove) return;
            response = 1; _app.RequestStop();
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key.Handled || key.KeyCode != KeyCode.F2) return;
            key.Handled = true;
            showDetails = !showDetails;
            technical ??= showDetails ? BuildConfirmationReview(selected, showDetails: true) : null;
            review.Text = showDetails ? technical!.Text : summary.Text;
            hints.Text = showDetails ? "Tab Focus · PgDn Scroll · F2 Summary" : "Tab Focus · PgDn Scroll · F2 Details";
        };
        dialog.Add(review, explanation, hints, cancel, remove);
        cancel.SetFocus();
        _app.Run(dialog);
        return response;
    }
}
