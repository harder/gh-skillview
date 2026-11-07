using System.Collections.Immutable;
using System.Collections.ObjectModel;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Preferences;
using SkillView.Threading;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

internal static class LocationsDialog
{
    internal static void Show(IApplication app, TuiServices services, SkillViewWorkflowCoordinator workflows,
        InventorySnapshot? snapshot, CancellationToken lifetime, Action applied, Action<Dialog>? readyForTests = null)
    {
        using var dialog = new Dialog { Title = " Locations ", Width = Dim.Fill(2), Height = 22 };
        using var operation = new ModalOperationTracker(app, services.Logger, "locations");
        using var linked = new CancellationSource(lifetime, operation.Token, CancellationCallbackReporter.For(services.Logger, "locations"));
        var token = linked.Token;
        var status = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 3, Text = "Reading selected locations…" };
        var list = new ListView { X = 0, Y = 3, Width = Dim.Fill(), Height = 4 };
        var kind = new OptionSelector
        {
            X = 0,
            Y = 7,
            Orientation = Orientation.Horizontal,
            Labels = ["Workspace", "Scan root", "Profile"],
            Value = 0,
            FocusedItem = 0
        };
        var pathLabel = new Label { X = 0, Y = 8, Text = "Directory:" };
        var path = new TextField { X = 11, Y = 8, Width = Dim.Fill() };
        var profileLabel = new Label { X = 0, Y = 9, Text = "Profile:" };
        var profile = new TextField { X = 11, Y = 9, Width = Dim.Fill() };
        var hint = new Label { X = 0, Y = 10, Width = Dim.Fill(), Height = 2, Text = "Profile: agent[:name]. Workspace scans known skill folders; scan root scans the chosen folder. Defaults stay enabled." };
        var add = new SingleRowButton { X = 0, Y = 12, Text = "_Add" };
        var remove = new SingleRowButton { X = Pos.Right(add) + 1, Y = 12, Text = "_Remove selected" };
        var recovery = new SingleRowButton { X = Pos.Right(remove) + 1, Y = 12, Text = "_Details / recovery" };
        var fullDetails = "";
        var sessionDetails = "";
        var state = new Label { X = 0, Y = 13, Width = Dim.Fill(), Height = 2 };
        var selectedLabel = new Label { X = 0, Y = 15, Text = "Selected:" };
        var selectedPath = new TextField { X = 11, Y = 15, Width = Dim.Fill(), ReadOnly = true };
        var inspectHint = new Label { X = 0, Y = 16, Width = Dim.Fill(), Text = "Tab to Selected; Home/End scrolls the full selected path." };
        var usage = new Label { X = 0, Y = 17, Width = Dim.Fill(), Height = 1 };
        var save = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "_Save & refresh" };
        var cancel = new SingleRowButton { X = Pos.Right(save) + 1, Y = Pos.AnchorEnd(1), Text = "_Cancel", IsDefault = true };
        var selected = ImmutableArray<SelectedLocation>.Empty;
        LocationRead? original = null;
        var active = true;
        bool Owns() => active && !token.IsCancellationRequested;
        void Enable(bool enabled)
        {
            add.Enabled = remove.Enabled = save.Enabled = enabled && original?.Diagnostic is null;
            kind.Enabled = path.Enabled = profile.Enabled = enabled || original is not null;
        }
        void ShowSelected()
        {
            selectedPath.Text = list.SelectedItem is { } index && index >= 0 && index < selected.Length ? TerminalEscapeSanitizer.Sanitize(selected[index].Path) ?? "" : "";
        }
        list.ValueChanged += (_, _) => ShowSelected();
        void Render()
        {
            list.SetSource(new ObservableCollection<string>(selected.Select(item => InstalledOverview.Text(
                item.Kind + (item.Agent is null ? "" : " " + item.Agent + (item.Name is null ? "" : ":" + item.Name)) + " · " + item.Path, 4096))));
            list.SelectedItem = selected.IsEmpty ? null : Math.Clamp(list.SelectedItem ?? 0, 0, selected.Length - 1);
            ShowSelected();
        }
        void Close() { active = false; operation.Cancel(); app.RequestStop(dialog); }
        void Start(Func<CancellationToken, Task<Action>> work)
        {
            if (!Owns() || operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            Enable(false);
            recovery.Enabled = false;
            operation.TryStart(async _ =>
            {
                Action? completion = null;
                string? error = null;
                try { completion = await Task.Run(() => work(token), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { error = "Locations: " + ex.Message; services.Logger.Error("locations", ex.ToString()); }
                operation.InvokeTerminalIfActive(() =>
                {
                    try
                    {
                        if (!Owns()) return;
                        if (error is not null) { fullDetails = error + "\n\n" + sessionDetails; status.Text = "Locations action failed. Details / recovery shows the full error."; }
                        else completion?.Invoke();
                        Enable(true);
                        recovery.Enabled = true;
                    }
                    finally { operation.Release(); }
                });
            });
        }
        void Add()
        {
            var value = kind.Value ?? 0;
            var identity = profile.Text.Split(':', 2);
            var requested = new SelectedLocation(value == 0 ? "workspace" : value == 1 ? "scan-root" : "profile", path.Text,
                value == 2 ? AgentIdentity.Normalize(identity[0]) : null, value == 2 && identity.Length == 2 ? identity[1] : null);
            var captured = selected;
            status.Text = "Checking directory…";
            Start(ct =>
            {
                var valid = LocationPreferences.ValidateNew(requested, services.Runner.EffectiveWorkingDirectory, ct);
                if (captured.Any(item => item.Kind == valid.Kind && item.Agent == valid.Agent && item.Name == valid.Name && PathIdentity.Equals(item.Path, valid.Path)))
                    throw new IOException("This location is already selected.");
                var next = captured.Add(valid); LocationPreferences.ValidateShape(next);
                return Task.FromResult<Action>(() => { selected = next; Render(); status.Text = "Added to selection. Save to persist and refresh."; });
            });
        }
        add.Accepting += (_, args) => { args.Handled = true; Add(); };
        void AddOnEnter(object? sender, Key key)
        {
            if (key.KeyCode != KeyCode.Enter) return;
            key.Handled = true;
            if (add.Enabled) Add();
        }
        path.KeyDown += AddOnEnter;
        profile.KeyDown += AddOnEnter;
        void ConsumeEnter(object? sender, Key key) { if (key.KeyCode == KeyCode.Enter) key.Handled = true; }
        list.KeyDown += ConsumeEnter;
        list.Accepted += (_, args) => args.Handled = true;
        selectedPath.KeyDown += ConsumeEnter;
        remove.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None || list.SelectedItem is not { } index || index < 0 || index >= selected.Length) return;
            selected = selected.RemoveAt(index); Render(); status.Text = "Removed from selection. Save to persist and refresh.";
        };
        save.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (original is null) return;
            var captured = selected; var expected = original;
            status.Text = "Saving selected locations…";
            Start(ct =>
            {
                var saved = workflows.SaveLocations(expected, captured, snapshot, ct);
                return Task.FromResult<Action>(() => { workflows.ApplyLocations(saved); applied(); Close(); });
            });
        };
        recovery.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            var recovered = LocationsRecoveryDialog.Show(app, services, workflows, snapshot, token, fullDetails);
            if (recovered is null || !Owns()) return;
            workflows.ApplyLocations(recovered); applied(); Close();
        };
        cancel.Accepting += (_, args) => { args.Handled = true; Close(); };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; Close(); } };
        dialog.IsRunningChanging += (_, args) => { if (!args.NewValue) { active = false; operation.Cancel(); } };
        dialog.Add(status, list, kind, pathLabel, path, profileLabel, profile, hint, add, remove, recovery, state, selectedLabel, selectedPath, inspectHint, usage, save, cancel);
        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, dialog, status, list, kind, pathLabel, path, profileLabel, profile, hint, add, remove, recovery, state, selectedLabel, selectedPath, inspectHint, usage, save, cancel);
        Enable(false);
        Start(async ct =>
        {
            var read = await workflows.ReadLocationsForDialogAsync(ct).ConfigureAwait(false);
            var choices = read.Locations;
            return () =>
            {
                original = read; selected = choices; Render();
                sessionDetails = fullDetails = workflows.LocationSessionDescription(read);
                var near = read.UsedRecords >= LocationPreferences.MaxRecords * 3 / 4 || read.UsedBytes >= LocationPreferences.MaxBytes * 3 / 4;
                status.Text = $"SAVED extras: {read.Locations.Length} · {(read.Diagnostic is not null && read.RecordsName is null ? "unknown" : read.UsedRecords.ToString())}/{LocationPreferences.MaxRecords} records · {(read.Diagnostic is not null && read.RecordsName is null ? "unknown" : read.UsedBytes.ToString())}/{LocationPreferences.MaxBytes} bytes."
                    + (near ? " Near capacity (75%)." : "")
                    + "\n" + (read.Diagnostic is null ? "Editing saved values; CLI/session overrides are preserved." : "Saved Locations unavailable. Details / recovery has full errors and Reset.")
                    + "\n" + (LocationPreferences.Choices(workflows.CurrentDiscoveryOptions, services.Runner.EffectiveWorkingDirectory).SequenceEqual(read.Locations)
                        ? "Save refreshes; unselected projects remain outside coverage." : "Saved differs from active session. Review Details before Save.");
                usage.Text = $"Saved log: {(read.Diagnostic is not null && read.RecordsName is null ? "unknown" : read.UsedRecords.ToString())}/{LocationPreferences.MaxRecords} records · {(read.Diagnostic is not null && read.RecordsName is null ? "unknown" : read.UsedBytes.ToString())}/{LocationPreferences.MaxBytes} bytes" + (near ? " · 75% near capacity" : "");
                state.Text = InstalledOverview.Text(workflows.LocationsPath ?? "Preference path unavailable", 4096);
                cancel.SetFocus();
            };
        });
        readyForTests?.Invoke(dialog);
        try { app.Run(dialog); }
        finally { active = false; linked.TryCancel(); operation.Cancel(); }
    }
}
