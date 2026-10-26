using SkillView.Diagnostics;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using Terminal.Gui.Views;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{

    internal void LoadSearchResultsForTests(IReadOnlyList<SearchResultSkill> results)
    {
        ReplaceSearchResults(results);
    }

    internal Task RunSearchForTestsAsync(string query, Func<CancellationToken, Task<SkillView.Gh.SearchResponse>> search)
    {
        _ghPath ??= "search-fixture";
        return RunSearchAsync(query, search: search);
    }

    internal SkillView.Ui.Tabs.DiscoverTabView? DiscoverTabForTests => _discoverTab;
    internal string DiscoverActionsForTests => _detailPane?.ItemActionsLabel.Text ?? string.Empty;
    internal void SetStatusForTests(string text, TuiHelpers.NotificationLevel level) => SetStatus(text, level);
    internal Func<bool> CaptureStatusAutoClearForTests() => CreateStatusAutoClearCallback();

    internal LatestRequestGate.Lease BeginPreviewRequestForTests()
    {
        var request = _previewRequests.Begin(DiscoverLifetimeForTests, PreviewTimeout);
        _ = BeginPreviewBusyOperation(request, "preview test…");
        return request;
    }

    internal long BeginBusyOperationForTests(string text) => BeginBusyOperation(text);

    internal void EndBusyOperationForTests(long operation) => EndBusyOperation(operation);

    internal void ShowLogPaneForTests() => ShowLogPane();

    internal void SetPreviewTextForTests(string text) => SetPreviewText(text);

    internal static string BuildDiscoverPreviewBodyForTests(
        string? description,
        string previewText,
        bool includeDescription) => BuildDiscoverPreviewBody(description, previewText, includeDescription);

    internal Window BuildUiForTests() => BuildUi();

    internal TextField? QueryFieldForTests => _queryField;

    internal TextField? OwnerFieldForTests => _ownerField;

    internal TextField? AgentFieldForTests => _agentField;

    internal NumericUpDown<int>? LimitUpDownForTests => _limitUpDown;

    internal CheckBox? HiddenDirsBoxForTests => _hiddenDirsBox;

    internal TableView? ResultsTableForTests => _resultsTable;

    internal bool UserInteractedSinceLaunchForTests => _userInteractedSinceLaunch;

    internal string StatusTextForTests => _currentStatus;

    internal string DefaultStatusForTests => _defaultStatus;

    internal void SetDefaultStatusForTests(string text) => SetDefaultStatus(text);

    internal void FocusSearchFromInstalledForTests() => FocusSearchFromInstalled();

    internal bool ShouldAutoOpenInstalledOnStartupForTests(InventorySnapshot snapshot) =>
        ShouldAutoOpenInstalledOnStartup(snapshot);

    internal SkillViewTab ActiveTabForTests => _activeTab;

    internal CancellationToken DiscoverLifetimeForTests =>
        TryCaptureActiveLifetimeToken(_discoverLifetime, out var token)
            ? token
            : new CancellationToken(canceled: true);

    internal CancellationToken DoctorLifetimeForTests =>
        _doctorLifetime?.Token ?? new CancellationToken(canceled: true);

    internal SkillView.Ui.Tabs.ChangesTabView? ChangesTabForTests => _changesTab;
    internal bool UpdatesWorkspaceVisibleForTests => _updatesTab?.Visible == true;
    internal SkillView.Ui.Tabs.UpdatesTabView? UpdatesTabForTests => _updatesTab;
    internal void StopBackgroundAdmissionForTests() => _backgroundTasks.StopAccepting();

    internal ContextBarView? ContextBarForTests => _contextBar;

    internal StatusStripView? StatusStripForTests => _statusStrip;

    internal IReadOnlyList<StatusHint> CurrentHintsForTests => GetCurrentHints();

    internal string PreviewTextForTests => _previewPane?.Text.ToString() ?? string.Empty;

    internal int VisibleLogCharacterCountForTests
    {
        get
        {
            lock (_visibleLogGate)
            {
                return _visibleLogCharacters;
            }
        }
    }

    internal TabBarView? TabBarForTests => _tabBar;

    internal SkillView.Ui.Tabs.InstalledTabView? InstalledTabForTests => _installedTab;

    internal void ForceActiveTabForTests(SkillViewTab tab)
    {
        _activeTab = tab;
        _tabBar?.SetActiveTab(tab);
        ShowSearchPanes(tab == SkillViewTab.Discover);
        if (tab == SkillViewTab.Discover)
        {
            RestoreDiscoverFocus();
        }
        RefreshShellChrome();
    }

    internal void ActivateTabForTests(SkillViewTab tab) => ActivateTab(tab);

    internal void RevealInstalledInstallForTests(InstalledInstallHandoff handoff) => RevealInstalledInstall(handoff);

    internal void SetInstalledTabForTests(SkillView.Ui.Tabs.InstalledTabView tab) => _installedTab = tab;

    internal void EnterDoctorForTests(EnvironmentReport report)
    {
        Volatile.Write(ref _lastReport, report);
        EnterDoctor();
    }

    internal void LeaveDoctorForTests() => LeaveDoctor();

    internal void RunBackgroundForTests(Func<CancellationToken, Task> work, string operation) =>
        RunBackground(work, operation);
}
