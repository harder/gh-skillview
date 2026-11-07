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

/// Focused review and existing removal executor. Browsing belongs to Issues.
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
    private RemoveValidator.RemoveValidation? _reviewedRemoval;
    private readonly CancellationToken _lifetimeToken;

    public int RemovedCount { get; private set; }
    public int RemovedFileCount { get; private set; }
    public int RemovedDirectoryCount { get; private set; }
    public int IgnoredCount { get; private set; }
    public bool PreferenceMutationObserved { get; private set; }
    public bool CancellationObserved { get; private set; }
    public RemoveService.BatchRemoveReport? LastReport { get; private set; }
    internal string OutcomeSummary { get; private set; } = "Skipped: review closed without an action.";

    public CleanupScreen(
        IApplication app,
        RemoveService remove,
        Logger logger,
        ImmutableArray<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<ScanRoot> scanRoots,
        IReadOnlyList<InstalledSkill> allSkills,
        Func<string, int>? confirmBatchRemoval = null,
        SkillView.Subprocess.ProcessRunner? ownershipRunner = null,
        Func<CancellationToken, Task<InventorySnapshot>>? captureInventory = null, CancellationToken lifetimeToken = default)
    {
        _lifetimeToken = lifetimeToken;
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

    internal void Show(Action<Dialog>? ready)
    {
        if (_lifetimeToken.IsCancellationRequested) return;
        if (_candidates.Length != 1) throw new InvalidOperationException("Select one exact cleanup finding in Issues before review.");
        using var window = new Dialog { Title = "Selected cleanup review", Width = Dim.Percent(96), Height = Dim.Percent(96) };
        using var operation = new ModalOperationTracker(_app, _logger, "cleanup.selected");
        var detail = new Markdown { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(4), Text = RenderDetail(_candidates[0]) };
        TuiHelpers.ConfigureMarkdownPane(detail, SkillViewStyling.BaseSchemeName);
        var status = new Label { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1, Text = "Review this exact object; deletion has no automatic Undo." };
        var remove = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(2), Text = "Review removal", Enabled = false };
        var ignore = new SingleRowButton { X = Pos.Right(remove) + 1, Y = Pos.AnchorEnd(2), Text = "Suppress suggestion", Enabled = false };
        var close = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "Back (Esc)", IsDefault = true };
        var closeRequested = false;
        var readyToClose = false;
        var showDetails = false;
        bool Busy() => operation.CurrentOwnership != ModalOperationTracker.Ownership.None;
        void Finish(string text)
        {
            OutcomeSummary = text;
            operation.Release(); status.Text = text;
            remove.Enabled = false; // A fresh exact finding is required for another attempt.
            ignore.Enabled = false;
            if (closeRequested) { readyToClose = true; _app.RequestStop(); }
        }
        void Close()
        {
            if (Busy()) { closeRequested = true; operation.Cancel(); status.Text = "Canceling; waiting for result accounting…"; return; }
            readyToClose = true; _app.RequestStop(window);
        }
        remove.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (Busy() || !remove.Enabled || _lifetimeToken.IsCancellationRequested) return;
            // Confirmation stays on the event-loop thread, before worker admission.
            if (_confirmBatchRemoval(_candidates) != 1 || _lifetimeToken.IsCancellationRequested) { OutcomeSummary = "Skipped: removal review canceled; nothing removed."; status.Text = OutcomeSummary; return; }
            remove.Enabled = ignore.Enabled = false;
            status.Text = "Removing selected object… Esc cancels and waits.";
            operation.TryStart(token => Task.Run(async () =>
            {
                string text;
                try
                {
                    var summary = await RemoveSelectedAsync([0], token, confirmationGranted: true).ConfigureAwait(false);
                    text = (summary.Failed > 0 ? "Refused/failed: " : "") + $"Removed {summary.Removed}; skipped {summary.Skipped}; failed/refused {summary.Failed}. No automatic Undo.";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { text = $"Canceled/partial: removed {RemovedCount} items, {RemovedFileCount} files, {RemovedDirectoryCount} folders. Inspect partial results."; }
                catch (Exception ex) { _logger.Error("cleanup.selected", ex.ToString()); text = "Removal failed or partial: " + TuiHelpers.ErrorSnippet(ex.Message); }
                OutcomeSummary = text; // Retain the worker outcome even if UI delivery is canceled.
                operation.InvokeTerminalIfActive(() => Finish(text));
            }));
        };
        ignore.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (Busy() || !ignore.Enabled || _lifetimeToken.IsCancellationRequested) return;
            if (_captureInventory is null || _ownershipRunner is null) { status.Text = "Fresh inventory unavailable; no preferences changed."; return; }
            remove.Enabled = ignore.Enabled = false;
            status.Text = "Suppressing this cleanup suggestion… Esc cancels and waits.";
            operation.TryStart(token => Task.Run(async () =>
            {
                string text;
                try
                {
                    var result = await CleanupSuppressionWorkflow.SetAsync(_candidates, CleanupSuppressionStore.For(_ownershipRunner),
                        _captureInventory, () => PreferenceMutationObserved = true, () => IgnoredCount++, token).ConfigureAwait(false);
                    CancellationObserved |= result.Canceled;
                    foreach (var error in result.Errors) _logger.Error("cleanup.suppress", error);
                    text = (result.Canceled ? "Canceled/partial: " : result.Failed > 0 ? "Failed/partial: " : result.Changed == 0 ? "Skipped: " : "") + $"Suppressed {result.Changed}; skipped {result.Skipped}; failed {result.Failed}. Preference only; health, ownership and enablement unchanged.";
                }
                catch (Exception ex) { _logger.Error("cleanup.suppress", ex.ToString()); text = "Suppression unavailable: " + TuiHelpers.ErrorSnippet(ex.Message); }
                OutcomeSummary = text; // Retain the worker outcome even if UI delivery is canceled.
                operation.InvokeTerminalIfActive(() => Finish(text));
            }));
        };
        close.Accepting += (_, e) => { e.Handled = true; Close(); };
        window.KeyDown += (_, key) =>
        {
            if (key.Handled) return;
            if (key.KeyCode == KeyCode.Esc) { key.Handled = true; Close(); }
            else if (key.KeyCode == KeyCode.F2)
            { key.Handled = true; showDetails = !showDetails; detail.Text = RenderDetail(_candidates[0], showDetails); }
        };
        window.IsRunningChanging += (_, args) =>
        {
            if (args.NewValue || readyToClose || !Busy()) return;
            args.Cancel = true; Close();
        };
        window.Add(detail, status, remove, ignore, close);
        close.SetFocus();
        operation.TryStart(token => Task.Run(() =>
        {
            RemoveValidator.RemoveValidation? validation = null;
            string? error = null;
            try { validation = ValidateCandidate(_candidates[0], token); }
            catch (Exception ex) { error = ex.Message; }
            operation.InvokeTerminalIfActive(() =>
            {
                _reviewedRemoval = validation;
                operation.Release();
                remove.Enabled = !_candidates[0].ReviewOnly && validation is not null;
                ignore.Enabled = true;
                if (validation is not { Allowed: true, RequiresSecondConfirm: false } || _candidates[0].ReviewOnly)
                    OutcomeSummary = "Refused: selected cleanup requires inspection or changed native admission.";
                status.Text = _candidates[0].ReviewOnly ? "Removal unavailable: inspect or repair this finding first."
                    : validation is { Allowed: true, RequiresSecondConfirm: false } ? "Exact native target captured; Review removal opens confirmation."
                    : "Removal unavailable: " + TuiHelpers.ErrorSnippet(error ?? string.Join("; ", validation?.Errors.Select(item => item.Detail) ?? []));
                if (closeRequested) { readyToClose = true; _app.RequestStop(); }
            });
        }));
        using var lifetimeRegistration = _lifetimeToken.Register(() =>
        {
            operation.Cancel();
            _app.Invoke(() => { if (window.IsRunning) Close(); });
        });
        ready?.Invoke(window);
        if (!_lifetimeToken.IsCancellationRequested) _app.Run(window);
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
            var validation = ValidateCandidate(candidate, cancellationToken);
            if (_reviewedRemoval is { } reviewed && (validation.ExecutionIdentity != reviewed.ExecutionIdentity
                || validation.ExecutionLinkIdentity != reviewed.ExecutionLinkIdentity
                || validation.RequiresEmptyDirectory != reviewed.RequiresEmptyDirectory || validation.RemovesLinkOnly != reviewed.RemovesLinkOnly))
                validation = validation with
                {
                    Errors = validation.Errors.Add(new(RemoveValidator.ErrorKind.FilesystemIdentityUnavailable,
                    "Selected cleanup target changed after review; refresh and review again."))
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

    private RemoveValidator.RemoveValidation ValidateCandidate(CleanupClassifier.Candidate candidate, CancellationToken token) =>
        candidate.ReviewOnly ? RefuseUnsupportedCandidate(candidate) : candidate.Kind switch
        {
            CleanupClassifier.CandidateKind.BrokenSymlink => RemoveValidator.ValidateBrokenSymlink(candidate.Path, _scanRoots, token, _ownershipRunner),
            CleanupClassifier.CandidateKind.EmptyDirectory => RemoveValidator.ValidateEmptyDirectory(candidate.Path, _scanRoots, token, _ownershipRunner),
            _ when candidate.Skill is not null => RemoveValidator.Validate(candidate.Skill, _scanRoots, _allSkills, token),
            _ => RefuseUnsupportedCandidate(candidate)
        };

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

    private static RemoveValidator.RemoveValidation RefuseUnsupportedCandidate(CleanupClassifier.Candidate candidate) =>
        new(
            ImmutableArray.Create(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.NotASkillDirectory,
                $"cleanup candidate kind '{candidate.Kind}' requires comparison or ownership review, not bulk removal")),
            ImmutableArray<RemoveValidator.Warning>.Empty,
            candidate.Path,
            ImmutableArray<string>.Empty);

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
        var canRemove = summary.Complete && selected.Any(candidate => !candidate.ReviewOnly)
            && (_reviewedRemoval is null || _reviewedRemoval is { Allowed: true, RequiresSecondConfirm: false });
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
