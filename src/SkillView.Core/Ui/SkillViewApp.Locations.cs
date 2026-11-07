using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{
    private SingleRowButton? _locationsButton;
    private void OpenLocations()
    {
        if (_app is not { } app || GetRunLifetimeToken().IsCancellationRequested) return;
        NoteUserInteraction();
        LocationsDialog.Show(app, _services, _workflows, _installedTab?.CurrentSnapshot, GetRunLifetimeToken(), () =>
        {
            CancelPendingTabWork();
            if (_activeTab == SkillViewTab.Installed && !_inDoctor)
                RunOwnedTask(_installedTab!.LoadAsync, "locations.refresh");
            else ActivateTab(SkillViewTab.Installed);
            SetStatus("Locations saved — refreshing inventory; previous checks are stale.");
            RefreshLocationsButton();
        });
        RefreshLocationsButton();
    }
    private void RefreshLocationsButton()
    {
        if (_locationsButton is not null) _locationsButton.Text = _workflows.LocationsDiagnostic is null ? "Locations F5" : "Locations ! F5";
    }
}
