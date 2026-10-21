using SkillView.Gh;
using SkillView.Inventory.Models;
using SkillView.Inventory;
using SkillView.Maintenance;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

internal sealed partial class UpdatesTabView
{
    private bool _versionReloadPending;
    private OperationSubject? _versionReloadSubject;
    private sealed record VersionChoiceViewState(string[] MarkedIds, string SelectedId, string? ContextId,
        CheckState All, CheckState Unpin, View? Focused);

    private void ChooseVersion()
    {
        if (_choosingVersion || _undoReview is not null || _inventoryNeedsRefresh || _lifetimeToken.IsCancellationRequested || _operationCancellations.HasActive
            || _loadCancellations.HasActive || _review is not null || _queue.Count > 0) return;
        var row = _table.GetSelectedRow();
        if (row < 0 || row >= _skills.Count) { _status.Text = " select one physical installation first"; return; }
        var selected = _skills[row];
        if (selected.Pinned)
        { _status.Text = " pin retained; plan and apply a separate unpin review before choosing a tag"; return; }
        var viewState = new VersionChoiceViewState(_wrapper?.CheckedRows.Where(index => index >= 0 && index < _skills.Count)
            .Select(index => _skills[index].InstallationId).ToArray() ?? [], selected.InstallationId,
            _drillInSubject?.InstallationId, _allBox.Value, _unpinBox.Value, SubViews.FirstOrDefault(view => view.HasFocus));
        string? tag = null;
        string? chooserError = null;
        var loadGeneration = Interlocked.Read(ref _loadGeneration);
        var reload = false;
        OperationSubject? reloadSubject = null;
        _versionReloadPending = false;
        _versionReloadSubject = null;
        _choosingVersion = true;
        try { tag = _chooseTag(selected); }
        catch (OperationCanceledException) { /* Cancellation remains a canceled choice. */ }
        catch (Exception ex)
        {
            _logger.Error("updates.choose-version", ex.Message);
            chooserError = TuiHelpers.ErrorSnippet(ex.Message);
        }
        finally
        {
            _choosingVersion = false;
            reload = _versionReloadPending;
            reloadSubject = _versionReloadSubject;
            _versionReloadPending = false;
            _versionReloadSubject = null;
        }
        if (_lifetimeToken.IsCancellationRequested || loadGeneration != Interlocked.Read(ref _loadGeneration)) return;
        var hidden = _hiddenBox.Value == Terminal.Gui.Views.CheckState.Checked;
        if (reload)
        {
            _runTask(() => ReloadChosenTagAsync(selected, tag, hidden, reloadSubject, loadGeneration, viewState, chooserError), "updates.reload-after-version-choice");
            return;
        }
        if (chooserError is not null) { _status.Text = $" version chooser failed: {chooserError}; no candidate prepared"; return; }
        if (tag is null) { _status.Text = " version selection canceled; no candidate prepared"; return; }
        _runTask(() => RunChosenTagAsync(selected, tag, hidden), "updates.choose-version");
    }

    private async Task ReloadChosenTagAsync(InstalledSkill selected, string? tag, bool hidden,
        OperationSubject? subject, long generation, VersionChoiceViewState viewState, string? chooserError)
    {
        if (!IsCurrentLoad(generation)) return;
        if (_choosingVersion || _review is not null || _queue.Count > 0 || _operationCancellations.HasActive)
        {
            _status.Text = " version choice not prepared; finish the current review, then refresh inventory";
            return;
        }
        var capture = await LoadForSubjectCoreAsync(subject).ConfigureAwait(false);
        if (capture is not { } fresh) return; // The loader preserves failures/cancellation.
        await _runOnUi(() =>
        {
            if (!IsCurrentLoad(fresh.Generation)) return;
            if (subject?.InstallationId == viewState.ContextId)
            {
                // Keep selection/marks by physical ID when rows reorder. A new
                // Changes drill-in retains its deliberate broad-option reset.
                var rows = _skills.Select((skill, index) => (skill.InstallationId, Index: index))
                    .GroupBy(row => row.InstallationId).Where(group => group.Count() == 1)
                    .ToDictionary(group => group.Key, group => group.Single().Index, StringComparer.Ordinal);
                _wrapper?.CheckedRows.Clear();
                foreach (var id in viewState.MarkedIds)
                    if (rows.TryGetValue(id, out var marked)) _wrapper?.CheckedRows.Add(marked);
                if (rows.TryGetValue(viewState.SelectedId, out var current)) _table.SetSelectedRow(current);
                _allBox.Value = viewState.All;
                _unpinBox.Value = viewState.Unpin;
                viewState.Focused?.SetFocus();
            }
            if (chooserError is not null)
            {
                _status.Text = $" version chooser failed: {chooserError}; inventory refreshed; no candidate prepared";
                return;
            }
            if (tag is null)
            {
                _status.Text = " version selection canceled; inventory refreshed; no candidate prepared";
                return;
            }
            var target = MaintenanceQueueBuilder.ResolveInstallation(fresh.Snapshot, OperationSubject.From(selected));
            if (target is null || !SameVersionChoice(selected, target)
                || subject is not null && (subject.InstallationId != selected.InstallationId || !PathIdentity.Equals(subject.Path, selected.ResolvedPath)))
            {
                _status.Text = " installation changed, missing or ambiguous; choose its version again";
                return;
            }
            if (_choosingVersion || _loadCancellations.HasActive || _operationCancellations.HasActive || _review is not null || _queue.Count > 0)
            {
                _status.Text = " version choice not prepared; finish the current review, then refresh inventory";
                return;
            }
            _runTask(() => RunChosenTagAsync(target, tag, hidden), "updates.choose-refreshed-version");
        }).ConfigureAwait(false);
    }

    private static bool SameVersionChoice(InstalledSkill selected, InstalledSkill fresh)
    {
        // Retain input across the same observed version/native address, not execution approval.
        // The saved planner still captures fresh payload/owner evidence for review.
        var before = selected.ObservedSubject?.NativeObject;
        var after = fresh.ObservedSubject?.NativeObject;
        return selected.Name == fresh.Name && selected.Scope == fresh.Scope
            && selected.SourceIdentity == fresh.SourceIdentity && selected.Revision == fresh.Revision
            && selected.Pinned == fresh.Pinned && selected.Validity == fresh.Validity
            && selected.Agents.ToHashSet().SetEquals(fresh.Agents)
            && (before is null ? after is null : after is not null
                && (before with { CapturedAt = null }) == (after with { CapturedAt = null }));
    }

    private async Task RunChosenTagAsync(InstalledSkill selected, string tag, bool hidden)
    {
        if (_lifetimeToken.IsCancellationRequested) return;
        if (string.IsNullOrWhiteSpace(tag)) { _status.Text = " enter one Git tag; no candidate prepared"; return; }
        if (_choosingVersion || _operationCancellations.HasActive || _loadCancellations.HasActive || _review is not null || _queue.Count > 0)
        {
            _status.Text = " version choice not prepared; finish the current operation or review, then refresh inventory";
            return;
        }
        var ghPath = _ghPathProvider();
        if (ghPath is null) { _status.Text = " gh unavailable; no candidate prepared"; return; }
        if (selected.Pinned) { _status.Text = " pin retained; unpin separately before choosing a tag"; return; }
        var options = new GhSkillUpdateService.Options([selected.Name], false, false, false,
            Targets: [selected], AllowHiddenDirectories: hidden, Tag: tag);
        _queueOptions = options;
        _queueOutcomes.Clear();
        _queueResults = new();
        await ExecuteReviewOperationAsync(token => _updateRunner(ghPath, options, token), dryRun: false).ConfigureAwait(false);
    }
}
