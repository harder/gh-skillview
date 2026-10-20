using SkillView.Gh;
using SkillView.Inventory.Models;

namespace SkillView.Ui.Tabs;

internal sealed partial class UpdatesTabView
{
    private void ChooseVersion()
    {
        if (_choosingVersion || _lifetimeToken.IsCancellationRequested || _operationCancellations.HasActive
            || _loadCancellations.HasActive || _review is not null || _queue.Count > 0) return;
        var row = _table.GetSelectedRow();
        if (row < 0 || row >= _skills.Count) { _status.Text = " select one physical installation first"; return; }
        var selected = _skills[row];
        if (selected.Pinned)
        { _status.Text = " pin retained; plan and apply a separate unpin review before choosing a tag"; return; }
        string? tag;
        _choosingVersion = true;
        try { tag = _chooseTag(selected); }
        finally { _choosingVersion = false; }
        if (_lifetimeToken.IsCancellationRequested) return;
        if (tag is null) { _status.Text = " version selection canceled; no candidate prepared"; return; }
        if (string.IsNullOrWhiteSpace(tag)) { _status.Text = " enter one Git tag; no candidate prepared"; return; }
        var hidden = _hiddenBox.Value == Terminal.Gui.Views.CheckState.Checked;
        _runTask(() => RunChosenTagAsync(selected, tag, hidden), "updates.choose-version");
    }

    private async Task RunChosenTagAsync(InstalledSkill selected, string tag, bool hidden)
    {
        if (_lifetimeToken.IsCancellationRequested || _operationCancellations.HasActive || _loadCancellations.HasActive
            || _review is not null || _queue.Count > 0) return;
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
