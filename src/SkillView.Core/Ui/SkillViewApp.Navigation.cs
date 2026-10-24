using System.Text;
using SkillView.Inventory.Models;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{

    private void OnWindowKeyDown(object? sender, Key key)
    {
        if (key.Handled) return;
        NoteUserInteraction();
        if (OnWindowShortcut(key))
        {
            key.Handled = true;
        }
    }

    private void OnApplicationKeyDown(object? sender, Key key)
    {
        if (key.Handled) return;
        var app = _app;
        var mainWindow = _mainWindow;
        if (app is null || mainWindow is null) return;

        if (IsUnconditionalQuitKey(key))
        {
            var top = app.TopRunnable;
            if (top is not null && !ReferenceEquals(top, mainWindow))
            {
                app.RequestStop(top);
            }
            app.RequestStop(mainWindow);
            key.Handled = true;
            return;
        }

        if (_sizeGuard?.Visible == true)
        {
            // Do not let hidden controls mutate state while the resize guard
            // is covering them. Ctrl+Q above remains available.
            key.Handled = true;
            return;
        }

        if (ReferenceEquals(app.TopRunnableView, mainWindow)
            && !TextInputHasFocus()
            && key.AsRune.Value is 'q' or 'Q')
        {
            app.RequestStop(mainWindow);
            key.Handled = true;
        }
    }

    private void RememberStickyFocus(View view)
    {
        if (ReferenceEquals(view, _queryField)
            || ReferenceEquals(view, _ownerField)
            || ReferenceEquals(view, _agentField)
            || ReferenceEquals(view, _limitUpDown)
            || ReferenceEquals(view, _hiddenDirsBox)
            || ReferenceEquals(view, _resultsTable))
        {
            _lastDiscoverFocus = view;
        }
    }

    private void RestoreDiscoverFocus()
    {
        if (_lastDiscoverFocus is not null)
        {
            _lastDiscoverFocus.SetFocus();
            return;
        }

        _queryField?.SetFocus();
    }

    /// Centralised single-letter shortcut dispatcher for `window.KeyDown`.
    /// Returns true if the key was consumed.
    private bool OnWindowShortcut(Key key)
    {
        // Ctrl+Q is the unconditional escape hatch. Handle it before the
        // printable-input guard so quitting still works while typing.
        if (IsUnconditionalQuitKey(key))
        {
            _app?.RequestStop();
            return true;
        }

        // Let printable text keep going to the focused input, but keep global
        // navigation (tab arrows, help, slash, etc.) available everywhere.
        if (TextInputHasFocus() && IsPrintableTextInputKey(key))
        {
            return false;
        }

        // Esc at the root of a primary tab would otherwise fall through to
        // Terminal.Gui's default quit-on-Esc and exit the app with no
        // confirmation. Swallow it and hint how to quit instead. Modals,
        // Doctor, Updates, and the search field all handle Esc themselves
        // (setting Handled before this runs), so this only fires at the
        // top-level list where Esc previously meant "lose your session".
        if (key.KeyCode == KeyCode.Esc)
        {
            var leftInput = LeaveTextInput();
            SetStatus(leftInput
                ? "Left the field · q or Ctrl+Q quits"
                : "Press q or Ctrl+Q to quit");
            return true;
        }

        var rune = key.AsRune;
        if (rune.Value == '/')
        {
            if (_inDoctor)
            {
                LeaveDoctor();
            }
            if (_activeTab != SkillViewTab.Discover)
            {
                ActivateTab(SkillViewTab.Discover);
            }
            _queryField?.SetFocus();
            if (_queryField is not null) _queryField.SelectAll();
            return true;
        }
        if ((rune.Value == 'f' || rune.Value == 'F') && _activeTab == SkillViewTab.Discover)
        {
            OpenDiscoverFilters();
            return true;
        }
        if (rune.Value == 'q' || rune.Value == 'Q') { _app?.RequestStop(); return true; }
        if (rune.Value == 'l' || rune.Value == 'L') { ToggleRightPane(); return true; }
        if (rune.Value == 'r' || rune.Value == 'R') { RefreshActiveTab(); return true; }
        if (rune.Value == 'e' || rune.Value == 'E') { TogglePreviewMode(); return true; }
        if (rune.Value == 'd' || rune.Value == 'D') { EnterDoctor(); return true; }
        // winget-tui keybindings:
        //   i → compact install modal (one screen, sensible defaults)
        //   I → advanced install wizard (multi-step InstallScreen)
        // The Installed view is reached via `2` (jump-to-tab) or ←/→ cycling.
        if (rune.Value == 'I') { StageInstall(forceAdvanced: true); return true; }
        if (rune.Value == 'i') { StageInstall(forceAdvanced: false); return true; }
        // A → discover the skills in the selected result's repo and pick which
        // to install (gh ≥ 2.95 non-interactive listing, cli/cli#13548).
        if (rune.Value == 'A' && _activeTab == SkillViewTab.Discover) { StageInstallAll(); return true; }
        if (rune.Value == 'o' || rune.Value == 'O') { OpenSelected(); return true; }
        // Open the update workspace directly. Changes rows describe observed
        // work and do not fabricate an update-available row for navigation.
        if (rune.Value == 'u' || rune.Value == 'U') { OpenUpdatesWorkspace(); return true; }
        if (rune.Value == 'c' || rune.Value == 'C') { _workflows.ShowCleanupScreen(); return true; }
        if (key.KeyCode == KeyCode.F1 || rune.Value == '?') { ShowHelp(); return true; }

        // Search-tab sort cycle. Lower-case `s` is unused at this level so
        // accept both — matches winget-tui's `S` semantics while staying
        // permissive about case.
        if ((rune.Value == 'S' || rune.Value == 's') && _activeTab == SkillViewTab.Discover)
        {
            HandleSearchSortKey();
            return true;
        }

        // Tab navigation — direct (1/2/3) and cyclic (←/→).
        if (rune.Value == '1') { ActivateTab(SkillViewTab.Discover); return true; }
        if (rune.Value == '2') { ActivateTab(SkillViewTab.Installed); return true; }
        if (rune.Value == '3') { ActivateTab(SkillViewTab.Changes); return true; }
        if (key.KeyCode == KeyCode.CursorLeft) { CycleTab(-1); return true; }
        if (key.KeyCode == KeyCode.CursorRight) { CycleTab(+1); return true; }
        return false;
    }

    private bool TextInputHasFocus()
    {
        if (_inDoctor) return false;
        if (_updatesTab?.Visible == true) return _updatesTab.LocalFileInputHasFocus;
        if (_activeTab == SkillViewTab.Installed)
        {
            return _installedTab?.FilterHasFocus == true;
        }
        return _activeTab == SkillViewTab.Discover
            && _discoverTab?.Visible == true
            && (_queryField?.HasFocus == true
                || _ownerField?.HasFocus == true
                || _agentField?.HasFocus == true
                || _limitUpDown?.HasFocus == true);
    }

    private bool LeaveTextInput()
    {
        if (!TextInputHasFocus())
        {
            return false;
        }

        if (_activeTab == SkillViewTab.Installed)
        {
            _installedTab?.FocusList();
            return true;
        }

        _resultsTable?.SetFocus();
        return true;
    }

    private static bool IsPrintableTextInputKey(Key key)
    {
        var rune = key.AsRune;
        if (rune.Value == 0)
        {
            return false;
        }

        return !Rune.IsControl(rune)
            && key.KeyCode != KeyCode.CursorLeft
            && key.KeyCode != KeyCode.CursorRight;
    }

    internal static bool IsUnconditionalQuitKey(Key key) =>
        (key.KeyCode & ~KeyCode.AltMask) == (KeyCode.Q | KeyCode.CtrlMask);

    /// Switch active tab. All three (Discover / Installed / Changes) are
    /// embedded views — flipping the Visible flags swaps them in-place
    /// without re-running the app loop.
    private void ActivateTab(SkillViewTab tab)
    {
        var leftDoctor = false;
        if (_inDoctor)
        {
            _inDoctor = false;
            if (_doctorTab is not null) _doctorTab.Visible = false;
            DeactivateDoctorWorkspace(clearBusy: true);
            leftDoctor = true;
        }
        if (leftDoctor && tab == _activeTab)
        {
            _activeTab = tab == SkillViewTab.Discover
                ? SkillViewTab.Installed
                : SkillViewTab.Discover;
        }
        if (tab == _activeTab) return;

        if (_activeTab == SkillViewTab.Discover)
        {
            DeactivateDiscoverWorkspace(clearBusy: true);
        }
        CancelPendingTabWork();
        _activeTab = tab;
        CancelStatusAutoClear();
        _currentStatus = _defaultStatus;
        _tabBar?.SetActiveTab(tab);

        // Hide every non-Discover tab by default; the requested one is then
        // revealed below.
        if (_installedTab is not null) _installedTab.Visible = false;
        if (_updatesTab is not null) _updatesTab.Visible = false;
        if (_changesTab is not null) _changesTab.Visible = false;

        switch (tab)
        {
            case SkillViewTab.Discover:
                ActivateDiscoverWorkspace();
                ShowSearchPanes(true);
                RestoreDiscoverFocus();
                break;
            case SkillViewTab.Installed:
                ShowSearchPanes(false);
                if (_installedTab is not null)
                {
                    _installedTab.Visible = true;
                    RunOwnedTask(_installedTab.LoadAsync, "installed.load");
                    var installed = _installedTab;
                    Invoke(() => installed.FocusList());
                }
                break;
            case SkillViewTab.Changes:
                ShowSearchPanes(false);
                if (_changesTab is not null)
                {
                    _changesTab.Visible = true;
                    RunOwnedTask(_changesTab.LoadAsync, "changes.load");
                }
                break;
        }
        RefreshShellChrome();
    }

    private void ShowSearchPanes(bool visible)
    {
        if (_discoverTab is not null) _discoverTab.Visible = visible;
        // Also explicitly control left-frame visibility to replicate the old
        // per-frame assignment, which has the side-effect of resetting log-mode's
        // hidden left-frame whenever the Discover tab is re-activated.
        if (_leftFrame is not null) _leftFrame.Visible = visible;
    }

    /// Replace whatever tab is currently visible with the Doctor view. We
    /// remember the prior tab so LeaveDoctor can restore it; if Doctor is
    /// already on screen this is a no-op.
    private void EnterDoctor()
    {
        if (_inDoctor || _doctorTab is null) return;
        _tabBeforeDoctor = _activeTab;
        _inDoctor = true;
        if (_activeTab == SkillViewTab.Discover)
        {
            DeactivateDiscoverWorkspace(clearBusy: true);
        }
        ActivateDoctorWorkspace();
        CancelPendingTabWork();
        ShowSearchPanes(false);
        if (_installedTab is not null) _installedTab.Visible = false;
        if (_updatesTab is not null) _updatesTab.Visible = false;
        if (_changesTab is not null) _changesTab.Visible = false;
        RefreshShellChrome();

        // Make sure the report is fresh — probe lazily if we never have.
        if (Volatile.Read(ref _lastReport) is { } cachedReport)
        {
            _doctorTab.SetReport(cachedReport, _installedTab?.CurrentSnapshot);
            _doctorTab.Visible = true;
            _doctorTab.SetFocus();
            return;
        }

        // Probe in the background; reveal an empty pane in the meantime so
        // the user sees the screen flip even before the report lands.
        _doctorTab.Visible = true;
        SetBusy("probing environment for Doctor…");
        var doctorGeneration = Interlocked.Read(ref _doctorGeneration);
        var doctorToken = _doctorLifetime?.Token ?? new CancellationToken(canceled: true);
        RunOwnedTask(
            () => ProbeDoctorAsync(doctorGeneration, doctorToken),
            "doctor");
    }

    private void CancelPendingTabWork()
    {
        _installedTab?.CancelPendingWork();
        _changesTab?.CancelPendingLoad();
        _updatesTab?.CancelPendingWork();
    }

    private void LeaveDoctor()
    {
        if (!_inDoctor || _doctorTab is null) return;
        _inDoctor = false;
        _doctorTab.Visible = false;
        DeactivateDoctorWorkspace(clearBusy: true);
        // Re-enter the previously-active primary tab. We force-set _activeTab
        // to something different first so ActivateTab's no-op guard doesn't
        // suppress the re-show.
        var restore = _tabBeforeDoctor;
        RefreshShellChrome();
        _activeTab = restore == SkillViewTab.Discover ? SkillViewTab.Installed : SkillViewTab.Discover;
        ActivateTab(restore);
    }

    /// Open update review without requiring an update-available queue row.
    private void OpenUpdatesWorkspace() => OpenUpdatesWorkspace(null);

    private void OpenUpdatesWorkspace(OperationSubject? selected)
    {
        if (_updatesTab is null || _changesTab is null || _updatesTab.Visible) return;
        ActivateTab(SkillViewTab.Changes);
        _openedUpdatesFromChanges = true;
        _changesTab.CancelPendingLoad();
        _workflows.OpenUpdatesFromChanges(
            hideChanges: () => _changesTab.Visible = false,
            activateUpdates: hideChanges => _updatesTab.ActivateFromChanges(hideChanges, selected));
    }

    /// Esc returns from the update workspace to Changes.
    private void LeaveUpdates()
    {
        if (_updatesTab is not null) _updatesTab.Visible = false;
        if (_openedUpdatesFromChanges)
        {
            _openedUpdatesFromChanges = false;
            // _activeTab is still Changes since drill-in bypasses ActivateTab.
            // Force the no-op guard by temporarily setting to a different tab.
            _activeTab = SkillViewTab.Discover;
            ActivateTab(SkillViewTab.Changes);
        }
        else
        {
            ActivateTab(SkillViewTab.Discover);
        }
    }

    private void CycleTab(int delta)
    {
        var values = new[] { SkillViewTab.Discover, SkillViewTab.Installed, SkillViewTab.Changes };
        var idx = Array.IndexOf(values, _activeTab);
        if (idx < 0) idx = 0;
        idx = (idx + delta + values.Length) % values.Length;
        ActivateTab(values[idx]);
    }

    /// Reload the active tab's data. Invalidates the shared `gh skill list`
    /// cache first so the reload reflects on-disk changes (e.g. a removal made
    /// outside this process). Discover re-runs the current query if one is set.
    private void RefreshActiveTab()
    {
        _services.ListAdapter.Invalidate();
        if (_updatesTab?.Visible == true)
        {
            RunOwnedTask(_updatesTab.LoadAsync, "updates.refresh");
            return;
        }
        switch (_activeTab)
        {
            case SkillViewTab.Installed when _installedTab is not null:
                SetStatus("refreshing inventory…");
                RunOwnedTask(_installedTab.LoadAsync, "installed.refresh");
                break;
            case SkillViewTab.Changes when _changesTab is not null:
                SetStatus("refreshing disk inventory; update availability remains unknown…");
                RunOwnedTask(_changesTab.LoadAsync, "changes.refresh");
                break;
            case SkillViewTab.Discover:
                if (!string.IsNullOrEmpty(_queryField?.Text.Trim()))
                {
                    SetStatus("refreshing results…");
                    SubmitSearch();
                }
                break;
        }
    }

    private void ShowHelp()
    {
        if (_app is null) return;
        HelpOverlay.Show(_app);
    }

    private void FocusSearchFromInstalled()
    {
        _queryField?.SetFocus();
        _queryField?.SelectAll();
        RestoreDefaultStatus();
    }
}
