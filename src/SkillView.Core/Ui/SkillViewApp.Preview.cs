using SkillView.Gh.Models;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{

    private async Task PreviewSelectedAsync()
    {
        _services.Logger.Debug("preview", $"PreviewSelectedAsync entered: table={_resultsTable is not null} results={_results.Count} ghPath={_ghPath is not null}");
        if (_resultsTable is null || _results.Count == 0 || _ghPath is null)
        {
            _services.Logger.Warn("preview", $"guard failed: table={_resultsTable is not null} results={_results.Count} ghPath={_ghPath ?? "(null)"}");
            return;
        }

        var row = _resultsTable.GetSelectedRow();
        if (row < 0 || row >= _results.Count)
        {
            _services.Logger.Warn("preview", $"SelectedRow={row} out of range (count={_results.Count})");
            return;
        }

        var pick = _results[row];
        var repo = pick.Repo ?? string.Empty;
        _services.Logger.Debug("preview", $"picked: repo={repo} skill={pick.SkillName}");
        if (string.IsNullOrEmpty(repo))
        {
            SetStatus("no repo on selected row");
            return;
        }

        if (!TryCaptureActiveLifetimeToken(_discoverLifetime, out var discoverToken))
        {
            return;
        }
        var discoverGeneration = Interlocked.Read(ref _discoverGeneration);
        using var request = _previewRequests.Begin(discoverToken, PreviewTimeout);
        var busyOperation = BeginPreviewBusyOperation(
            request,
            $"preview {repo}/{pick.SkillName}…");
        try
        {
            _services.Logger.Info("preview", $"loading {repo}/{pick.SkillName}…");
            var preview = await _services.PreviewService
                .PreviewAsync(
                    _ghPath,
                    repo,
                    pick.SkillName,
                    allowHiddenDirs: ShouldAllowHiddenDirs(pick, HiddenDirsEnabled),
                    cancellationToken: request.Token)
                .ConfigureAwait(false);
            _services.Logger.Debug("preview", $"PreviewAsync returned: succeeded={preview.Succeeded} exit={preview.ExitCode} bodyLen={preview.Body?.Length ?? 0}");
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent || !IsDiscoverWorkspaceActive(discoverGeneration)) return;
                _loadedPreviewKey = preview.Succeeded ? BuildPreviewSelectionKey(pick) : null;
                SetPreviewText(preview.Succeeded
                    ? preview.MarkdownBody ?? preview.Body ?? "(empty preview)"
                    : $"(preview failed: exit {preview.ExitCode})\n\n{preview.ErrorMessage}");
                if (_previewFrame is not null)
                {
                    _previewFrame.Title = $"Preview — {repo}/{pick.SkillName}";
                }
                ShowPreviewPane();
                if (preview.Succeeded)
                {
                    SetStatus(preview.AssociatedFiles.Length == 0
                        ? "preview loaded"
                        : $"preview loaded · {preview.AssociatedFiles.Length} file(s)",
                        TuiHelpers.NotificationLevel.Success);
                }
                else
                {
                    SetStatus("preview failed — see logs (l)", TuiHelpers.NotificationLevel.Error);
                }
            }, discoverToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (discoverToken.IsCancellationRequested)
        {
            _services.Logger.Debug("preview", "preview canceled during workspace deactivation");
        }
        catch (OperationCanceledException)
        {
            if (!request.IsCurrent)
            {
                _services.Logger.Debug("preview", "preview superseded by a newer selection");
                return;
            }
            _services.Logger.Warn("preview", "preview timed out");
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent || !IsDiscoverWorkspaceActive(discoverGeneration)) return;
                _loadedPreviewKey = null;
                SetPreviewText("(preview timed out)\n\nThe gh subprocess did not respond within 30 seconds.");
                SetStatus("preview timed out", TuiHelpers.NotificationLevel.Error);
            }, discoverToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Logger.Error("preview", ex.Message);
            var snippet = TuiHelpers.ErrorSnippet(ex.Message);
            await InvokeAsync(() =>
            {
                if (!request.IsCurrent || !IsDiscoverWorkspaceActive(discoverGeneration)) return;
                _loadedPreviewKey = null;
                SetPreviewText(snippet.Length > 0
                    ? $"(preview failed)\n\n{snippet}"
                    : "(preview failed)\n\nSee logs for details.");

                SetStatus(snippet.Length > 0
                    ? $"preview failed: {snippet}"
                    : "preview failed — see logs (l)",
                    TuiHelpers.NotificationLevel.Error);
            }, discoverToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _activePreviewBusyOperationId,
                value: 0,
                comparand: busyOperation);
            EndBusyOperation(busyOperation);
        }
    }

    private void ToggleRightPane()
    {
        if (_previewPane is null || _rightFrame is null)
        {
            return;
        }

        if (_showingLogs)
        {
            ShowPreviewPane();
            UpdatePreviewPlaceholder();
            if (_previewFrame is not null) _previewFrame.Title = "Preview";
        }
        else
        {
            ShowLogPane();
            FlushVisibleLogLines();
        }
    }

    /// Mirror text into both the rendered Markdown pane and the raw
    /// editor pane so toggling between them via `e` keeps the same content.
    private void SetPreviewText(string text)
    {
        var sanitized = TerminalEscapeSanitizer.Sanitize(text) ?? string.Empty;
        var selected = GetSelectedResult();
        var includeDescription = selected is not null
            && !string.Equals(_loadedPreviewKey, BuildPreviewSelectionKey(selected), StringComparison.Ordinal);
        var body = BuildDiscoverPreviewBody(selected?.Description, sanitized, includeDescription);
        if (_previewPane is not null) _previewPane.Text = body;
        if (_previewRawPane is not null) _previewRawPane.Text = body;
    }

    private static string BuildPreviewSelectionKey(SearchResultSkill skill) =>
        $"{skill.Repo ?? string.Empty}/{skill.SkillName ?? string.Empty}";

    private void TogglePreviewMode()
    {
        if (_previewPane is null || _previewRawPane is null || _showingLogs) return;
        _showingRawPreview = !_showingRawPreview;
        _previewPane.CanFocus = !_showingRawPreview;
        _previewRawPane.CanFocus = _showingRawPreview;
        _previewPane.Visible = !_showingRawPreview;
        _previewRawPane.Visible = _showingRawPreview;
        SetStatus(_showingRawPreview ? "preview: source" : "preview: rendered");
    }

    private void ShowPreviewPane()
    {
        _showingLogs = false;
        if (_previewPane is not null) _previewPane.CanFocus = !_showingRawPreview;
        if (_previewRawPane is not null) _previewRawPane.CanFocus = _showingRawPreview;
        if (_previewPane is not null) _previewPane.Visible = !_showingRawPreview;
        if (_previewRawPane is not null) _previewRawPane.Visible = _showingRawPreview;
        if (_metadataFrame is not null) _metadataFrame.Visible = true;
        if (_previewFrame is not null) _previewFrame.Visible = true;
        if (_itemActionsLabel is not null) _itemActionsLabel.Visible = true;
        if (_logPane is not null) _logPane.CanFocus = false;
        if (_logPane is not null) _logPane.Visible = false;
        if (_leftFrame is not null) _leftFrame.Visible = true;
        if (_rightFrame is not null)
        {
            _rightFrame.X = _leftFrame is not null ? Pos.Right(_leftFrame) : 0;
            _rightFrame.Width = Dim.Fill();
        }
        UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
    }

    private void ShowLogPane()
    {
        // Logs are an explicit user choice. A preview already in flight must
        // not be allowed to close the log pane when it completes.
        CancelCurrentPreview();
        _showingLogs = true;
        if (_previewPane is not null) _previewPane.CanFocus = false;
        if (_previewPane is not null) _previewPane.Visible = false;
        if (_previewRawPane is not null) _previewRawPane.CanFocus = false;
        if (_previewRawPane is not null) _previewRawPane.Visible = false;
        if (_metadataFrame is not null) _metadataFrame.Visible = false;
        if (_previewFrame is not null) _previewFrame.Visible = false;
        if (_itemActionsLabel is not null) _itemActionsLabel.Visible = false;
        if (_logPane is not null) _logPane.CanFocus = true;
        if (_logPane is not null) _logPane.Visible = true;
        if (_leftFrame is not null) _leftFrame.Visible = false;
        if (_rightFrame is not null)
        {
            _rightFrame.X = 0;
            _rightFrame.Width = Dim.Fill();
        }
        if (!ApplyBusyUi())
        {
            UpdateStatusStrip(_defaultStatus, TuiHelpers.NotificationLevel.Info);
        }
    }

    private void CancelCurrentPreview()
    {
        _previewRequests.Cancel();
        var busyOperation = Interlocked.Exchange(
            ref _activePreviewBusyOperationId,
            value: 0);
        if (busyOperation != 0)
        {
            EndBusyOperation(busyOperation);
        }
    }
}
