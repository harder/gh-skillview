using System.Globalization;
using SkillView.Gh;
using SkillView.Gh.Models;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{

    private void OnSearchFieldKey(object? sender, Key key)
    {
        NoteUserInteraction();
        // Accept Enter and Ctrl+J as search submit triggers.
        // Ctrl+J is a workaround for Warp terminal which intercepts Enter
        // for its own block processing after the TUI enables mouse tracking.
        var isSubmit = key.KeyCode == KeyCode.Enter
            || key.KeyCode == (KeyCode.J | KeyCode.CtrlMask);

        if (isSubmit)
        {
            key.Handled = true;
            SubmitSearch();
        }
        else if (key.KeyCode == KeyCode.Esc)
        {
            key.Handled = true;
            LeaveTextInput();
        }
    }

    private SearchResultSkill? GetSelectedResult()
    {
        var row = _resultsTable?.GetSelectedRow() ?? -1;
        return row >= 0 && row < _results.Count ? _results[row] : null;
    }

    private void SubmitSearch()
    {
        var query = _queryField?.Text.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query)) return;
        var owner = _ownerField?.Text.Trim();
        var agent = _agentField?.Text.Trim();
        var limit = _limitUpDown?.Value ?? GhSkillSearchService.DefaultLimit;
        var allowHiddenDirs = HiddenDirsEnabled;
        UpdateContextBar();
        RunOwnedTask(
            () => RunSearchAsync(
                query,
                string.IsNullOrEmpty(owner) ? null : owner,
                limit,
                string.IsNullOrEmpty(agent) ? null : agent,
                allowHiddenDirs),
            "search");
    }

    private async Task RunSearchAsync(
        string query,
        string? owner = null,
        int? limit = null,
        string? agent = null,
        bool allowHiddenDirs = false,
        Func<CancellationToken, Task<SearchResponse>>? search = null)
    {
        if (_ghPath is null)
        {
            SetDiscoverStatus("cannot search — gh not found", TuiHelpers.NotificationLevel.Error);
            return;
        }
        if (!TryCaptureActiveLifetimeToken(_discoverLifetime, out var discoverToken))
        {
            return;
        }

        CancelCurrentPreview();
        using var request = _searchRequests.Begin(discoverToken, TimeSpan.FromMinutes(2));
        var discoverGeneration = Interlocked.Read(ref _discoverGeneration);
        var generation = System.Threading.Interlocked.Increment(ref _searchGeneration);
        var busyOperation = BeginBusyOperation($"searching {query}…", discoverOwned: true);
        var cancellationToken = request.Token;
        try
        {
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent || !IsDiscoverWorkspaceActive(discoverGeneration)) return;
                // A submitted search replaces the previous answer immediately.
                // Failure or navigation must never reveal the old install targets.
                ReplaceSearchResults([]);
                SetSearchFeedback("Searching skills…");
                _discoverTab?.SetLoading(true);
            }, discoverToken).ConfigureAwait(false);
            var options = new GhSkillSearchService.Options(
                Owner: owner,
                Limit: limit ?? GhSkillSearchService.DefaultLimit);
            var response = await (search is null
                ? _services.SearchService.SearchAsync(_ghPath, query, options, cancellationToken)
                : search(cancellationToken)).ConfigureAwait(false);
            if (!response.Succeeded)
                throw new IOException(response.ErrorMessage ?? $"gh search exited {response.ExitCode}");
            var results = response.Results;
            var filteredResults = await FilterResultsByAgentAsync(
                results,
                agent,
                allowHiddenDirs,
                cancellationToken).ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent
                    || !IsDiscoverWorkspaceActive(discoverGeneration)
                    || System.Threading.Interlocked.Read(ref _searchGeneration) != generation)
                {
                    // A newer search has already taken effect — drop these
                    // results silently so we never paint stale data.
                    _services.Logger.Debug("search", $"dropping stale results for generation {generation}");
                    return;
                }
                _discoverTab?.SetLoading(false);
                ReplaceSearchResults(filteredResults);
                _resultsTable?.SetFocus();
                _services.Logger.Info("search", $"results loaded: count={_results.Count} rawCount={results.Count} tableFocus={_resultsTable?.HasFocus} queryFocus={_queryField?.HasFocus}");
                if (!_showingLogs)
                {
                    SetPreviewText(_results.Count == 0 ? TuiHelpers.NoResultsHint : TuiHelpers.PreviewHint);
                }
                if (_previewFrame is not null)
                {
                    _previewFrame.Title = "Preview";
                }
                SetDiscoverStatus(DescribeSearchResults(results.Count, _results.Count, agent));
            }, discoverToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _services.Logger.Debug("search", "search canceled or superseded");
            if (request.IsCurrent && IsDiscoverWorkspaceActive(discoverGeneration))
            {
                await InvokeAsync(() =>
                {
                    if (request.IsCurrent && IsDiscoverWorkspaceActive(discoverGeneration))
                    {
                        SetSearchFeedback("Search timed out.\n\nNo results to show. Try the search again or change the query.");
                        SetDiscoverStatus("search timed out", TuiHelpers.NotificationLevel.Error);
                    }
                }, discoverToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("search", ex.Message);
            var snippet = TuiHelpers.ErrorSnippet(ex.Message);
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent || !IsDiscoverWorkspaceActive(discoverGeneration)) return;
                SetSearchFeedback($"Search failed.\n\nNo results to show. Try the search again or change the query.\n\n{snippet}");
                SetDiscoverStatus(snippet.Length > 0
                    ? $"search failed: {snippet}"
                    : "search failed — see logs (l)",
                    TuiHelpers.NotificationLevel.Error);
            }, discoverToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (IsDiscoverWorkspaceActive(discoverGeneration))
                {
                    await InvokeAsync(() =>
                    {
                        if (request.IsCurrent && IsDiscoverWorkspaceActive(discoverGeneration))
                            _discoverTab?.SetLoading(false);
                    }, GetRunLifetimeToken()).ConfigureAwait(false);
                }
            }
            finally
            {
                EndBusyOperation(busyOperation);
            }
        }
    }

    private void SetSearchFeedback(string text)
    {
        SetPreviewText(text);
        if (_previewFrame is not null) _previewFrame.Title = "Preview";
    }

    private void ReplaceSearchResults(IReadOnlyList<SearchResultSkill> results)
    {
        // A preview may have started against the old table after this search
        // began. Invalidate again at the commit boundary so it cannot paint
        // after the table has been replaced with a different result set.
        CancelCurrentPreview();
        _resultsNaturalOrder = results.ToList();
        _results = ApplySearchSort(_resultsNaturalOrder, _searchSort);
        _loadedPreviewKey = null;
        RefreshResultsTable();
        RefreshDiscoverResultsTitle();
        UpdateMetadataPane();
        UpdatePreviewPlaceholder();
        UpdateDiscoverActions();
    }

    private async Task<IReadOnlyList<SearchResultSkill>> FilterResultsByAgentAsync(
        IReadOnlyList<SearchResultSkill> results,
        string? requestedAgent,
        bool allowHiddenDirs,
        CancellationToken cancellationToken)
    {
        if (SearchAgentMetadataCache.NormalizeAgent(requestedAgent) is null || _ghPath is null)
        {
            return results;
        }

        var ghPath = _ghPath;
        return await _searchAgentMetadataLoader.FilterAsync(
            results,
            requestedAgent,
            (result, token) => _services.PreviewService.PreviewAsync(
                ghPath,
                result.Repo!,
                result.SkillName,
                allowHiddenDirs: ShouldAllowHiddenDirs(result, allowHiddenDirs),
                cancellationToken: token),
            cancellationToken).ConfigureAwait(false);
    }

    private void HandleSearchSortKey()
    {
        _searchSort = CycleSearchSort(_searchSort);
        _results = ApplySearchSort(_resultsNaturalOrder, _searchSort);
        RefreshResultsTable();
        SetDiscoverStatus(DescribeSearchSort(_searchSort));
    }

    private void RefreshResultsTable()
    {
        if (_resultsTable is null)
        {
            return;
        }
        // Three columns: ★, Name, Repo. ★ leads so it can't be pushed off the
        // right edge by long names/repos, and ExpandLastColumn lets Repo soak
        // up any leftover budget. Description was dropped — the preview pane
        // and metadata strip surface it instead.
        var viewportWidth = _resultsTable.Viewport.Width;
        var available = viewportWidth > 0
            ? Math.Max(40, viewportWidth - 6 /* borders + 2 separators + slop */)
            : 80;
        var longestName = _results.Count == 0 ? 0 : _results.Max(s => (s.SkillName ?? string.Empty).Length);
        var longestRepo = _results.Count == 0 ? 0 : _results.Max(s => (s.Repo ?? string.Empty).Length);
        // Cap mins so a single very-long value can't starve the other columns.
        var nameMin = Math.Clamp(longestName, 12, 28);
        var repoMin = Math.Clamp(longestRepo, 14, 32);
        var widths = TuiHelpers.DistributeWidths(available, new (int, double)[]
        {
            (5,       0.0), // ★
            (nameMin, 1.0), // Name
            (repoMin, 1.0), // Repo
        });
        int starsW = widths[0], nameW = widths[1], repoW = widths[2];

        // Suffix the active sort column with a direction glyph so the user
        // can see at a glance what `S` last did. Inactive columns stay clean.
        var starsHeader = _searchSort == SearchSort.StarsDesc ? "★ ↓" : "★";
        var nameHeader = _searchSort switch
        {
            SearchSort.NameAsc => "Name ↑",
            SearchSort.NameDesc => "Name ↓",
            _ => "Name",
        };
        var repoHeader = _searchSort == SearchSort.RepoAsc ? "Repo ↑" : "Repo";
        var source = new EnumerableTableSource<SearchResultSkill>(
            _results,
            new Dictionary<string, Func<SearchResultSkill, object>>
            {
                [starsHeader] = s => s.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                [nameHeader] = s => TuiHelpers.Truncate(s.SkillName, nameW),
                [repoHeader] = s => TuiHelpers.Truncate(s.Repo, repoW),
            });
        _resultsTable.Table = source;
        TuiHelpers.ApplyColumnStyles(_resultsTable, nameW, repoW, starsW, 0);
        _resultsTable.Update();
    }

    private void UpdatePreviewPlaceholder()
    {
        if (_previewPane is null || _showingLogs || _results.Count == 0)
        {
            return;
        }

        var pick = GetSelectedResult();
        if (pick is null)
        {
            return;
        }
        _loadedPreviewKey = null;
        SetPreviewText($"Selected: {pick.Repo}/{pick.SkillName}\n\n{TuiHelpers.PreviewHint}");
    }

    /// Render the metadata sidebar for the currently-selected search result.
    /// Mirrors SkillsGate's metadata panel: name, description, source, URL,
    /// path, namespace, stars. The sidebar always reflects the selected row,
    /// independent of whether the SKILL.md preview has been loaded yet.
    private void UpdateMetadataPane()
    {
        if (_discoverTab is null) return;
        var selected = GetSelectedResult();
        var text = selected is not null
            ? RenderSearchMetadata(selected, _lastReport?.Auth)
            : null;
        _discoverTab.DetailPane.SetMetadataContent(text);
    }

    private bool HiddenDirsEnabled => _hiddenDirsBox?.Value == CheckState.Checked;

    private void RefreshHiddenDirUi()
    {
        RefreshDiscoverFilterSummary();
        UpdateDiscoverActions();
        UpdateContextBar();
    }

    private void RefreshDiscoverFilterSummary()
    {
        if (_discoverTab is null)
        {
            return;
        }

        var summary = DescribeDiscoverFilters(includePrefix: true);
        var showSummary = summary.Length > 0;
        _discoverTab.FilterSummaryLabel.Text = summary;
        _discoverTab.FilterSummaryLabel.Visible = showSummary;
        _discoverTab.ResultsTable.Y = showSummary ? 4 : 3;
    }

    private string DescribeDiscoverFilters(bool includePrefix)
    {
        var summary = SkillView.Ui.Tabs.DiscoverTabView.BuildFilterSummaryForTests(
            owner: _ownerField?.Text.Trim() ?? string.Empty,
            agent: _agentField?.Text.Trim() ?? string.Empty,
            limit: _limitUpDown?.Value ?? GhSkillSearchService.DefaultLimit,
            hiddenDirs: HiddenDirsEnabled);

        return includePrefix
            ? summary
            : summary["Filters: ".Length..];
    }

    private void RefreshDiscoverResultsTitle()
    {
        if (_discoverTab is null)
        {
            return;
        }

        _discoverTab.LeftFrame.Title = _results.Count == 0
            ? "Search Results"
            : $"Search Results ({_results.Count})";
    }

    private void UpdateDiscoverActions()
    {
        if (_detailPane is null)
        {
            return;
        }

        _detailPane.SetActionsText(_results.Count == 0
            ? "[f] Filters    [?] Help"
            : ItemActionsText);
    }

    private void OpenDiscoverFilters()
    {
        if (_app is null || _ownerField is null || _agentField is null || _limitUpDown is null || _hiddenDirsBox is null)
        {
            return;
        }

        var dialog = new DiscoverFiltersDialog(
            _app,
            owner: _ownerField.Text.Trim(),
            agent: _agentField.Text.Trim(),
            limit: _limitUpDown.Value,
            hiddenDirs: HiddenDirsEnabled,
            supportsHiddenDirs: _hiddenDirsBox.Enabled);
        var result = dialog.Show();
        if (!result.Accepted)
        {
            return;
        }

        NoteUserInteraction();
        _ownerField.Text = result.Owner;
        _agentField.Text = result.Agent;
        _limitUpDown.Value = result.Limit;
        _hiddenDirsBox.Value = result.HiddenDirs ? CheckState.Checked : CheckState.UnChecked;
        RefreshHiddenDirUi();

        if (!string.IsNullOrWhiteSpace(_queryField?.Text))
        {
            SubmitSearch();
        }
        else
        {
            SetDiscoverStatus("discover filters updated");
        }
    }

    /// Open the GitHub page for the selected search result in the system
    /// browser. Bound to `o` on the main view.
    private void OpenSelected()
    {
        if (_discoverTab?.IsLoading == true) return;
        if (_resultsTable is null || _results.Count == 0)
        {
            SetDiscoverStatus("no result to open");
            return;
        }
        var row = _resultsTable.GetSelectedRow();
        if (row < 0 || row >= _results.Count)
        {
            SetDiscoverStatus("no result selected");
            return;
        }
        var pick = _results[row];
        if (string.IsNullOrEmpty(pick.Repo))
        {
            SetDiscoverStatus("no repo on selected row");
            return;
        }
        var url = BuildRepoUrl(_lastReport?.Auth, pick.Repo);
        if (TuiHelpers.OpenInDefaultHandler(url))
        {
            SetDiscoverStatus($"opened {url}", TuiHelpers.NotificationLevel.Success);
        }
        else
        {
            SetDiscoverStatus("open failed — see logs (l)", TuiHelpers.NotificationLevel.Error);
            _services.Logger.Warn("open", $"failed to open {url}");
        }
    }

    /// Stage an install of the currently-selected search result. Bound to
    /// `i` on the main view; the actual `gh skill install` invocation runs
    /// in `OpenInstallDialog`.
    private void StageInstall(bool forceAdvanced = false)
    {
        if (_discoverTab?.IsLoading == true) return;
        if (_resultsTable is null || _results.Count == 0)
        {
            SetDiscoverStatus("no results to install");
            return;
        }
        var row = _resultsTable.GetSelectedRow();
        if (row < 0 || row >= _results.Count)
        {
            SetDiscoverStatus("no result selected");
            return;
        }
        var pick = _results[row];
        if (string.IsNullOrEmpty(pick.Repo))
        {
            SetDiscoverStatus("no repo on selected row");
            return;
        }
        var discoverGeneration = Interlocked.Read(ref _discoverGeneration);
        _workflows.OpenInstallDialog(
            new InstallRequest(
                Repo: pick.Repo,
                SkillName: pick.SkillName,
                AllowHiddenDirs: ShouldAllowHiddenDirs(pick, HiddenDirsEnabled),
                SourcePath: pick.Path),
            forceAdvanced: forceAdvanced,
            revealInstalled: RevealInstalledInstall,
            isOwnerActive: () => _mainWindow is { IsRunning: true, StopRequested: false } && IsDiscoverWorkspaceActive(discoverGeneration) && !GetRunLifetimeToken().IsCancellationRequested);
    }

    /// Discover the skills in the selected result's repo and open the picker
    /// so the user installs a chosen subset (gh ≥ 2.95 non-interactive
    /// listing, cli/cli#13548). Failed listing offers Retry or Cancel.
    private void StageInstallAll()
    {
        if (_discoverTab?.IsLoading == true) return;
        if (_resultsTable is null || _results.Count == 0)
        {
            SetDiscoverStatus("no results to install");
            return;
        }
        var row = _resultsTable.GetSelectedRow();
        if (row < 0 || row >= _results.Count)
        {
            SetDiscoverStatus("no result selected");
            return;
        }
        var pick = _results[row];
        if (string.IsNullOrEmpty(pick.Repo))
        {
            SetDiscoverStatus("no repo on selected row");
            return;
        }
        if (!TryCaptureActiveLifetimeToken(_discoverLifetime, out var lifetimeToken)) return;
        var generation = Interlocked.Read(ref _discoverGeneration);
        _workflows.OpenRepoDiscoveryDialog(
            new InstallRequest(
                Repo: pick.Repo,
                SkillName: null,
                AllowHiddenDirs: ShouldAllowHiddenDirs(pick, HiddenDirsEnabled)),
            () => IsDiscoverWorkspaceActive(generation) && !GetRunLifetimeToken().IsCancellationRequested,
            lifetimeToken);
    }
}
