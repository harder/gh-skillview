using SkillView.Logging;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{

    private void OnLogEntry(LogEntry entry)
    {
        var line = TerminalEscapeSanitizer.Sanitize(Logger.Format(entry)) ?? string.Empty;
        lock (_visibleLogGate)
        {
            _visibleLogLines.Enqueue(line);
            _visibleLogCharacters += line.Length + 1;
            while (_visibleLogLines.Count > MaxVisibleLogLines
                   || _visibleLogCharacters > MaxVisibleLogCharacters)
            {
                _visibleLogCharacters -= _visibleLogLines.Dequeue().Length + 1;
            }
        }

        // Retain the bounded entry stream even while hidden. Visibility only
        // controls drawing, so opening the pane never needs a racy snapshot
        // replacement.
        if (!_showingLogs) return;

        // Coalesce bursts into one UI refresh. This avoids rebuilding and
        // redrawing the whole log pane once per individual entry.
        if (Interlocked.Exchange(ref _logRefreshQueued, 1) == 0)
        {
            Invoke(FlushVisibleLogLines);
        }
    }

    private void FlushVisibleLogLines()
    {
        string text;
        lock (_visibleLogGate)
        {
            text = _visibleLogLines.Count == 0
                ? "(no log entries yet)"
                : string.Join('\n', _visibleLogLines);

            // Reset while the queue snapshot is protected. An entry enqueued
            // after this point must observe zero and schedule another flush.
            Interlocked.Exchange(ref _logRefreshQueued, 0);
        }
        if (_showingLogs && _logPane is not null)
        {
            _logPane.Text = text;
        }
    }

    private void SetStatus(string text) => SetStatus(text, TuiHelpers.NotificationLevel.Info);

    private void SetStatus(string text, TuiHelpers.NotificationLevel level) => Invoke(() =>
    {
        UpdateStatusStrip(text, level);
        ScheduleStatusAutoClear();
    });

    private void SetDiscoverStatus(string text, TuiHelpers.NotificationLevel level = TuiHelpers.NotificationLevel.Info)
    {
        var generation = Interlocked.Read(ref _discoverGeneration);
        var searchGeneration = Interlocked.Read(ref _searchGeneration);
        Invoke(() =>
        {
            if (!IsDiscoverWorkspaceActive(generation)
                || Interlocked.Read(ref _searchGeneration) != searchGeneration) return;
            UpdateStatusStrip(text, level, discoverOwned: true);
            ScheduleStatusAutoClear();
        });
    }

    /// Persistent contextual status (gh version, "gh not found", etc.).
    /// Replaces the auto-clear default and is what transient `SetStatus`
    /// messages fall back to after `StatusAutoClear`.
    private void SetDefaultStatus(string text) => Invoke(() =>
    {
        _defaultStatus = text;
        UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
        CancelStatusAutoClear();
    });

    private void RestoreDefaultStatus() => Invoke(() =>
    {
        UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
        CancelStatusAutoClear();
    });

    private void UpdateStatusStrip(string text, TuiHelpers.NotificationLevel level, bool discoverOwned = false)
    {
        _currentStatus = text;
        _currentStatusDiscoverOwned = discoverOwned;
        if (_statusStrip is null) return;

        var hints = GetCurrentHints();
        if (_activeTab == SkillViewTab.Installed && !_inDoctor && _updatesTab?.Visible != true && text == _defaultStatus)
            text = "";
        _statusStrip.Update(text, hints);
    }

    private List<StatusHint> GetCurrentHints()
    {
        if (_showingLogs)
        {
            return [
                new StatusHint("l", "Preview"),
                new StatusHint("?", "Help"),
                new StatusHint("Ctrl+Q", "Quit"),
            ];
        }

        if (_inDoctor)
        {
            return [
                new StatusHint("Esc", "Back"),
                new StatusHint("?", "Help"),
                new StatusHint("Ctrl+Q", "Quit"),
            ];
        }

        return _activeTab switch
        {
            SkillViewTab.Discover => [
                new StatusHint("f", "Filters"),
                new StatusHint("1/2/3", "Tabs"),
                new StatusHint("?", "Help"),
                new StatusHint("Ctrl+Q", "Quit"),
            ],
            SkillViewTab.Installed => [
                new StatusHint("Enter", "Details/Expand"),
                new StatusHint("/", "Search"),
                new StatusHint("c", "Cleanup"),
                new StatusHint("r", "Refresh"),
                new StatusHint("?", "Help"),
                new StatusHint("Ctrl+Q", "Quit"),
            ],
            SkillViewTab.Changes => [
                new StatusHint("Enter", "Review"),
                new StatusHint("F2", "Details"),
                new StatusHint("c", "Cleanup"),
                new StatusHint("d", "Doctor"),
                new StatusHint("?", "Help"),
                new StatusHint("C", "Check updates"),
                new StatusHint("Ctrl+Q", "Quit"),
            ],
            _ => [
                new StatusHint("?", "Help"),
                new StatusHint("Ctrl+Q", "Quit"),
            ],
        };
    }

    private void ScheduleStatusAutoClear()
    {
        CancelStatusAutoClear();
        if (_app is null) return;
        _statusToken = _app.AddTimeout(StatusAutoClear, CreateStatusAutoClearCallback());
    }

    private Func<bool> CreateStatusAutoClearCallback()
    {
        var version = _statusVersion;
        return () =>
        {
            if (version != _statusVersion) return false;
            _statusToken = null;
            if (!ApplyBusyUi())
                UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
            return false;
        };
    }

    private void CancelStatusAutoClear()
    {
        _statusVersion++;
        if (_app is null || _statusToken is null) return;
        _app.RemoveTimeout(_statusToken);
        _statusToken = null;
    }

    private long BeginBusyOperation(string text, bool discoverOwned = false)
    {
        long operation;
        lock (_busyGate)
        {
            operation = ++_nextBusyOperationId;
            _busyOperations.Add(operation, (text, discoverOwned));
        }
        Invoke(RefreshBusyUi);
        return operation;
    }

    private long BeginPreviewBusyOperation(
        LatestRequestGate.Lease request,
        string text)
    {
        var operation = BeginBusyOperation(text, discoverOwned: true);
        var previous = Interlocked.Exchange(
            ref _activePreviewBusyOperationId,
            operation);
        if (previous != 0)
        {
            EndBusyOperation(previous);
        }

        // Cancellation can race the small interval between beginning the
        // request and publishing its busy owner. Remove the just-published
        // owner if the request already lost ownership.
        if (!request.IsCurrent
            && Interlocked.CompareExchange(
                ref _activePreviewBusyOperationId,
                value: 0,
                comparand: operation) == operation)
        {
            EndBusyOperation(operation);
        }
        return operation;
    }

    private void EndBusyOperation(long operation)
    {
        var changed = false;
        lock (_busyGate)
        {
            changed = _busyOperations.Remove(operation);
            if (_legacyBusyOperationId == operation)
            {
                _legacyBusyOperationId = null;
            }
        }
        if (changed)
        {
            Invoke(RefreshBusyUi);
        }
    }

    private void SetBusy(string text)
    {
        lock (_busyGate)
        {
            if (_legacyBusyOperationId is { } previous)
            {
                _busyOperations.Remove(previous);
            }

            var operation = ++_nextBusyOperationId;
            _busyOperations.Add(operation, (text, DiscoverOwned: false));
            _legacyBusyOperationId = operation;
        }
        Invoke(RefreshBusyUi);
    }

    private void ClearBusy()
    {
        long? operation;
        lock (_busyGate)
        {
            operation = _legacyBusyOperationId;
        }
        if (operation is { } active)
        {
            EndBusyOperation(active);
        }
    }

    private void ClearAllBusyOperations()
    {
        lock (_busyGate)
        {
            _busyOperations.Clear();
            _legacyBusyOperationId = null;
        }
        Interlocked.Exchange(ref _activePreviewBusyOperationId, value: 0);
        Invoke(RefreshBusyUi);
    }

    private void ClearDiscoverFeedback()
    {
        lock (_busyGate)
        {
            foreach (var operation in _busyOperations.Where(pair => pair.Value.DiscoverOwned).Select(pair => pair.Key).ToArray())
                _busyOperations.Remove(operation);
        }
        if (_currentStatusDiscoverOwned)
        {
            CancelStatusAutoClear();
            if (!ApplyBusyUi())
                UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
        }
        else
        {
            // Keep workflow notifications and progress when Discover deactivates.
            _statusStrip?.SetBusy(HasBusyOperations());
        }
    }

    private bool HasBusyOperations()
    {
        lock (_busyGate) return _busyOperations.Count > 0;
    }

    private void RefreshBusyUi() => _ = ApplyBusyUi();

    private bool ApplyBusyUi()
    {
        string? text = null;
        var discoverOwned = false;
        long newest = 0;
        lock (_busyGate)
        {
            foreach (var operation in _busyOperations)
            {
                if (operation.Key <= newest) continue;
                newest = operation.Key;
                text = operation.Value.Text;
                discoverOwned = operation.Value.DiscoverOwned;
            }
        }

        var busy = text is not null;
        _statusStrip?.SetBusy(busy);
        if (busy)
        {
            UpdateStatusStrip(text!, TuiHelpers.NotificationLevel.Info, discoverOwned);
        }
        return busy;
    }
}
