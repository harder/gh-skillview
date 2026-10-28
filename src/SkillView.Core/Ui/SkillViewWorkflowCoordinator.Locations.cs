using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Preferences;
using SkillView.Subprocess;
using SkillView.Threading;

namespace SkillView.Ui;

internal sealed partial class SkillViewWorkflowCoordinator
{
    private readonly AppOptions _originalLocationOptions;
    internal AppOptions OriginalLocationOptions => _originalLocationOptions;
    private readonly SharedAsyncOperation<bool> _locationInitialization;
    private readonly object _locationsGate = new();
    private LocationRead _savedLocations = new([]);
    private bool _locationsInitialized;
    private readonly ProcessRunner _locationContext;
    private LocationPreferences? _locationStore;
    private AppOptions DiscoveryOptions => Volatile.Read(ref _options);
    internal string? LocationsDiagnostic { get { lock (_locationsGate) return _savedLocations.Diagnostic; } }
    internal string? LocationsPath => _locationStore?.Root;
    internal LocationRead SavedLocations { get { lock (_locationsGate) return _savedLocations; } }
    internal AppOptions CurrentDiscoveryOptions => DiscoveryOptions;

    internal async Task InitializeLocationsAsync(CancellationToken token)
    {
        lock (_locationsGate) if (_locationsInitialized) return;
        await _locationInitialization.GetAsync(async shared =>
        {
            await Task.Run(() =>
            {
                lock (_locationsGate) if (_locationsInitialized) return;
                LocationRead read;
                var context = _locationContext;
                LocationPreferences? store = null;
                try
                {
                    store = LocationPreferences.For(context);
                    read = store.Read(LocationExclusions(DiscoveryOptions, context, shared, requireComplete: false), shared);
                }
                catch (Exception ex) when (LocationPreferences.Expected(ex)) { read = new([], Diagnostic: "Locations unavailable: " + ex.Message); }
                shared.ThrowIfCancellationRequested();
                lock (_locationsGate)
                {
                    _locationStore = store;
                    _savedLocations = read;
                    Volatile.Write(ref _options, LocationPreferences.Apply(_originalLocationOptions, read));
                    _locationsInitialized = true;
                }
            }, shared).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    internal async Task<LocationRead> ReadLocationsForDialogAsync(CancellationToken token)
    {
        await InitializeLocationsAsync(token).ConfigureAwait(false);
        var read = await Task.Run(() =>
        {
            try
            {
                return _locationStore?.Read(LocationExclusions(DiscoveryOptions, _locationContext, token, requireComplete: false), token)
                    ?? new LocationRead([], Diagnostic: LocationsDiagnostic ?? "Location state path unavailable.");
            }
            catch (Exception ex) when (LocationPreferences.Expected(ex))
            { return new LocationRead([], Diagnostic: "Locations unavailable: " + ex.Message); }
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_locationsGate) _savedLocations = read;
        return read;
    }

    private AppOptions SelectedOptions(ImmutableArray<SelectedLocation> selected) =>
        LocationPreferences.Apply(_originalLocationOptions, new(selected));

    internal LocationRead SaveLocations(LocationRead expected, ImmutableArray<SelectedLocation> selected,
        InventorySnapshot? snapshot, CancellationToken token)
    {
        RevalidateLocationContext(token);
        var context = _locationContext;
        var store = _locationStore ?? throw new IOException(LocationsDiagnostic ?? "Location state path unavailable.");
        var proposed = SelectedOptions(selected);
        var project = SkillView.Gh.GhPlacementCatalog.ProjectRootAsync(context, new(), context.EffectiveWorkingDirectory, token).GetAwaiter().GetResult();
        var excluded = ScanRootResolver.ProjectSeeds.Select(seed => Path.Combine(project, seed.RelativePath))
            .Concat(LocationExclusions(proposed, context, token))
            .Concat(LocationExclusions(LocationPreferences.Apply(_originalLocationOptions with { ScanRoots = [], WorkspaceDirectories = [], Profiles = [] }, new(selected)), context, token, requireComplete: false))
            .Concat(LocationExclusions(DiscoveryOptions, context, token, requireComplete: false))
            .Concat(snapshot?.ProtectedRoots.Select(root => root.Path) ?? [])
            .Concat(snapshot?.Skills.Select(skill => skill.ResolvedPath) ?? []).Distinct(StringComparer.Ordinal).ToArray();
        ManagedSkillDiscovery.RefusePaths([store.Root], token, context);
        return store.Save(expected, selected, excluded, context.EffectiveWorkingDirectory, token);
    }

    // UI publication only; the worker has finished durable storage. Invalidation
    // retires display/Issues/package/availability evidence at the same boundary.
    internal void ApplyLocations(LocationRead saved)
    {
        lock (_locationsGate)
        {
            _savedLocations = saved;
            Volatile.Write(ref _options, SelectedOptions(saved.Locations));
        }
        _services.ListAdapter.Invalidate();
    }

    internal string LocationSessionDescription(LocationRead saved)
    {
        var text = new System.Text.StringBuilder("Edit the LATEST SAVED selections, not CLI overrides. Opening/Cancel never changes the active session.\n");
        foreach (var (flag, count) in new[] { ("--workspace", _originalLocationOptions.WorkspaceDirectories.Count),
            ("--scan-root", _originalLocationOptions.ScanRoots.Count), ("--profile", _originalLocationOptions.Profiles.Count) })
            if (count > 0) text.AppendLine(flag + " active for this session; saved values apply when launched without it.");
        var active = LocationPreferences.Choices(DiscoveryOptions, _locationContext.EffectiveWorkingDirectory);
        if (!active.SequenceEqual(saved.Locations)) text.AppendLine("Saved selection differs from the current active session. Save merges ORIGINAL CLI categories over these saved choices; CLI flags are never persisted.");
        text.AppendLine("\nLATEST SAVED:");
        foreach (var item in saved.Locations) text.AppendLine(item.Kind + " " + item.Agent + (item.Name is null ? "" : ":" + item.Name) + " = " + item.Path);
        text.AppendLine("\nCURRENT ACTIVE SESSION:");
        foreach (var item in active) text.AppendLine(item.Kind + " " + item.Agent + (item.Name is null ? "" : ":" + item.Name) + " = " + item.Path);
        text.AppendLine($"\nCapacity: {(saved.Diagnostic is not null && saved.RecordsName is null ? "unknown" : saved.UsedRecords.ToString())}/{LocationPreferences.MaxRecords} records, {(saved.Diagnostic is not null && saved.RecordsName is null ? "unknown" : saved.UsedBytes.ToString())}/{LocationPreferences.MaxBytes} bytes.");
        if (saved.UsedRecords >= LocationPreferences.MaxRecords * 3 / 4 || saved.UsedBytes >= LocationPreferences.MaxBytes * 3 / 4)
            text.AppendLine("Near capacity (75% or more). Use explicit Compact or Reset in Recovery.");
        text.AppendLine("\n" + saved.Diagnostic + "\n" + saved.RecoveryHint);
        return text.ToString();
    }

    private void RevalidateLocationContext(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (SkillView.Gh.InstallNavigationEvidence.Context(_services.Runner) != SkillView.Gh.InstallNavigationEvidence.Context(_locationContext)
            || _locationStore?.Root != LocationPreferences.For(_locationContext).Root)
            throw new IOException("Captured HOME/CWD/config/state context changed; restart SkillView before saving or resetting Locations.");
        ManagedSkillDiscovery.RefusePaths([_locationStore.Root], token, _locationContext);
    }
    private string[] RecoveryExclusions(InventorySnapshot? snapshot, CancellationToken token) =>
        LocationExclusions(DiscoveryOptions, _locationContext, token, requireComplete: false)
            .Concat(LocationExclusions(LocationPreferences.Apply(_originalLocationOptions with { ScanRoots = [], WorkspaceDirectories = [], Profiles = [] }, SavedLocations), _locationContext, token, requireComplete: false))
            .Concat(snapshot?.ProtectedRoots.Select(root => root.Path) ?? [])
            .Concat(snapshot?.Skills.Select(skill => skill.ResolvedPath) ?? []).Distinct(StringComparer.Ordinal).ToArray();

    internal LocationResetReview PrepareLocationReset(InventorySnapshot? snapshot, CancellationToken token)
    {
        RevalidateLocationContext(token);
        return (_locationStore ?? throw new IOException("Locations have not loaded.")).PrepareReset(RecoveryExclusions(snapshot, token), token);
    }
    internal LocationRead ResetLocations(LocationResetReview reviewed, bool compact, InventorySnapshot? snapshot, CancellationToken token)
    {
        RevalidateLocationContext(token);
        return (_locationStore ?? throw new IOException("Locations have not loaded.")).Reset(reviewed, compact,
            RecoveryExclusions(snapshot, token), token, RevalidateLocationContext);
    }

    private static string[] LocationExclusions(AppOptions options, ProcessRunner context, CancellationToken token, bool requireComplete = true)
    {
        var cwd = context.EffectiveWorkingDirectory;
        string? Env(string key) => context.GetEffectiveEnvironmentVariable(key) is { Length: > 0 } value ? Path.GetFullPath(value, cwd) : null;
        var home = Env(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") ?? throw new IOException("Cannot establish location preference home.");
        var configuration = new ScanRootConfiguration();
        _ = new ScanRootResolver().ResolveWithCancellation(new(cwd, home, options.ScanRoots, Env("CLAUDE_CONFIG_DIR"), Env("PI_CODING_AGENT_DIR"), options.WorkspaceDirectories, options.Profiles), token, configuration: configuration);
        // Existing-only reads and removal of unavailable old selections retain
        // every expected path without requiring those old contexts to resolve.
        // New preference publication still requires complete proposed inputs.
        if (requireComplete && !configuration.IsComplete) throw new IOException("Location configuration is incomplete; preferences cannot be verified safely.");
        return configuration.Roots.Select(root => root.Path).ToArray();
    }
}
