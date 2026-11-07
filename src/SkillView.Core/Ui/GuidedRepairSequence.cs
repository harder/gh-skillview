using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal sealed record GuidedRepairItem(MaintenanceQueueEntry Entry, ExactCopyIntent? CopyIntent);
internal sealed record GuidedRepairOutcome(string Id, string Path, string Status, string? SavedOperationId = null);
internal sealed record UpdateReviewWork(OperationSubject Subject, string? SavedPlanId, long LoadGeneration, bool Applying);

// A serial review cursor, not a mutation queue. No candidates are prepared here.
// Settling a worker means its executor/reconciliation returned, even if delivery
// was canceled. Only the user's next Review next can admit the next item.
internal sealed class GuidedRepairSequence
{
    private readonly object _gate = new();
    private GuidedRepairItem[] _items = [];
    private readonly List<GuidedRepairOutcome> _outcomes = [];
    private int _index;
    private bool _stopped;
    private bool _working;
    private bool _closeRequested;
    private string _closeStatus = "Skipped: review closed; no further item started";
    private GuidedRepairItem? _active;
    private OperationSubject? _dispatchedSubject;
    private long? _updateLoadGeneration;
    private string? _savedPlanId;
    private UpdateReviewWork? _updateWork;
    internal string Context { get; private set; } = "";
    internal GuidedRepairItem? Active { get { lock (_gate) return _active; } }
    internal GuidedRepairOutcome[] Outcomes { get { lock (_gate) return _outcomes.ToArray(); } }
    internal bool HasSequence { get { lock (_gate) return _items.Length > 0; } }
    internal bool HasActiveSequence { get { lock (_gate) return !_stopped && (_active is not null || _working || _index < _items.Length); } }
    internal bool HasOpenItemOrWork { get { lock (_gate) return _active is not null || _working; } }
    internal string Summary
    {
        get
        {
            lock (_gate) return $"Guided reviews: {_outcomes.Count} outcomes; {_outcomes.Count(o => o.Status.StartsWith("Skipped", StringComparison.Ordinal))} skipped; {_items.Length - _index} remaining"
                + (_stopped ? "; stopped" : _working ? "; waiting for reconciliation" : _active is not null ? "; selected review open" : "; Review next / Skip / Stop");
        }
    }
    internal void Start(IEnumerable<MaintenanceQueueEntry> entries, InventorySnapshot snapshot, string context)
    {
        lock (_gate)
        {
            if (_active is not null || _working) return;
            _items = entries.DistinctBy(entry => (entry.Subject.InstallationId, entry.Subject.Path, entry.Issue)).Take(128)
                .Select(entry => new GuidedRepairItem(entry, InlineCopyRequest.ResolveDisplayed(snapshot, entry.Subject) is { } target
                    ? ExactCopyIntent.Capture(target) : null)).ToArray();
            _index = 0; _stopped = false; _closeRequested = false; _outcomes.Clear(); Context = context;
        }
    }
    internal GuidedRepairItem? ReviewNext()
    {
        lock (_gate)
        {
            if (_stopped || _active is not null || _working || _index >= _items.Length) return null;
            _closeRequested = false;
            _closeStatus = "Skipped: review closed; no further item started";
            return _active = _items[_index];
        }
    }
    internal void Skip()
    {
        lock (_gate)
        {
            if (_stopped || _active is not null || _working || _index >= _items.Length) return;
            Finish("Skipped by user");
        }
    }
    internal void Stop() { lock (_gate) { _stopped = true; _closeRequested = true; _closeStatus = "Skipped: stopped during review; saved evidence retained"; if (!_working && _active is not null) Finish(_closeStatus, _savedPlanId); } }
    internal void CloseReview(GuidedRepairItem? expected = null)
    {
        lock (_gate) { if (expected is not null && !ReferenceEquals(expected, _active)) return; _closeRequested = true; if (!_working && _active is not null) Finish(_closeStatus, _savedPlanId); }
    }
    internal bool DispatchUpdate(GuidedRepairItem item, OperationSubject subject)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, item) || _working || !SameSubject(item.Entry.Subject, subject)) return false;
            _dispatchedSubject = subject;
            _updateLoadGeneration = null;
            return true;
        }
    }
    internal void ReviewLoaded(OperationSubject subject, long generation)
    {
        lock (_gate)
        {
            if (_active is null || _dispatchedSubject is null) return;
            if (_updateLoadGeneration is { } current && generation < current) return;
            if (!SameSubject(_dispatchedSubject, subject))
            {
                _closeRequested = true;
                _closeStatus = "Skipped: another review replaced it";
                if (!_working) Finish(_closeStatus, _savedPlanId);
            }
            else if (!_working) _updateLoadGeneration = generation;
        }
    }
    internal void ReviewSkipped(OperationSubject subject, string? planId, long generation)
    {
        lock (_gate)
        {
            if (!MatchesUpdate(subject, planId, generation)) return;
            _closeRequested = true;
            if (!_working) Finish(_closeStatus, _savedPlanId);
        }
    }
    internal void CleanupWorkStarted() { lock (_gate) { if (_active is not null) _working = true; } }
    internal void WorkStarted(UpdateReviewWork work)
    {
        lock (_gate)
        {
            if (_working || !MatchesUpdate(work.Subject, work.SavedPlanId, work.LoadGeneration)
                || work.Applying && _savedPlanId is null) return;
            _updateWork = work;
            _working = true;
        }
    }
    internal void WorkSettled(UpdateReviewWork work, UpdateResult? result)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_updateWork, work) || !MatchesUpdate(work.Subject, work.SavedPlanId, work.LoadGeneration)) return;
            if (result?.SavedPlan is { } plan)
            {
                if (plan.Subject.InstallationId != work.Subject.InstallationId || plan.OriginalPath != work.Subject.Path
                    || _savedPlanId is not null && plan.Id != _savedPlanId) return;
                _savedPlanId ??= plan.Id;
            }
            _working = false;
            _updateWork = null;
            if (work.Applying)
                Finish(result is null ? "Failure/uncertain; inspect saved history before recovery"
                    : (result.SavedPlan is { Status: "installed", MutationObserved: true } ? "Native replacement installed; inspect the saved operation or use admitted Undo in its result"
                        : result.SavedPlan?.Status == "unchanged" || result.Succeeded && !result.MutationAttempted ? "Unchanged; no native replacement reported"
                        : result.Cancelled ? "Canceled/partial; inspect saved evidence" : "Failed/refused; inspect saved evidence")
                        + (result.ReconciliationPartial ? "; inventory verification incomplete" : "; reconciliation returned"), _savedPlanId);
            else if (result?.SavedPlan is null) Finish(result?.Succeeded == true ? "No replacement required; unchanged" : "Skipped/refused: candidate unavailable");
            else if (_closeRequested) Finish(_closeStatus + "; saved candidate retained", _savedPlanId);
        }
    }
    private bool MatchesUpdate(OperationSubject subject, string? planId, long generation) => _active is not null
        && _dispatchedSubject is not null && SameSubject(_dispatchedSubject, subject)
        && _updateLoadGeneration == generation && _savedPlanId == planId;
    private static bool SameSubject(OperationSubject left, OperationSubject right) =>
        left.InstallationId == right.InstallationId && left.Path == right.Path;
    internal void Complete(GuidedRepairItem expected, string status) { lock (_gate) { if (ReferenceEquals(_active, expected) && !_working) Finish(status); } }
    internal void CompleteWork(GuidedRepairItem expected, string status)
    {
        lock (_gate) { if (!ReferenceEquals(_active, expected)) return; _working = false; Finish(status); }
    }

    private void Finish(string status, string? operationId = null)
    {
        if (_index >= _items.Length) return;
        var item = _items[_index++];
        _outcomes.Add(new(item.Entry.Id, item.Entry.Subject.Path, status, operationId));
        _active = null; _working = false;
        _dispatchedSubject = null; _updateLoadGeneration = null; _savedPlanId = null; _updateWork = null;
    }
}
