using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

/// App-owned transient check results shared by Installed and Issues. All
/// publication happens on the UI thread, including request/generation checks.
internal sealed class UpdateAvailabilitySession
{
    private sealed record Entry(InstalledSkill Target, string Evidence, UpdateAvailability Observation);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _request;
    private long _catalogGeneration;
    private string? _context;
    private string? _failure;
    internal bool Running { get; private set; }
    internal bool Stopping { get; private set; }
    internal int Completed { get; private set; }
    internal int Total { get; private set; }
    internal string Summary => Stopping ? "Canceling update checks…"
        : Running ? Total == 0 ? "Checking updates · loading inventory · C cancel" : $"Checking updates {Completed}/{Total} · C cancel"
        : _failure ?? (_entries.Count == 0 ? "Updates not checked"
        : $"{_entries.Values.Count(e => e.Observation.Status == UpdateAvailabilityStatus.Available)} Updates · "
            + $"{_entries.Values.Count(e => e.Observation.Status is UpdateAvailabilityStatus.Unknown or UpdateAvailabilityStatus.Failed or UpdateAvailabilityStatus.Stale or UpdateAvailabilityStatus.Canceled or UpdateAvailabilityStatus.Unsupported)} incomplete checks");

    internal void Start() { Running = true; Stopping = false; Total = Completed = 0; _failure = null; }
    internal void SetStopping(bool stopping) => Stopping = stopping;
    internal void Fail(string reason) { Cancel(); _failure = "Update check failed: " + reason; }

    internal long Begin(InventorySnapshot snapshot, long generation, string context)
    {
        _request++;
        _catalogGeneration = generation;
        _context = context;
        _entries.Clear();
        _failure = null;
        foreach (var skill in snapshot.Skills)
            _entries[UpdateAvailability.Address(skill)] = new(skill, UpdateAvailability.Evidence(skill),
                new(UpdateAvailabilityStatus.Checking, "Waiting for this copy's bounded upstream check."));
        Total = _entries.Count;
        Completed = 0;
        Running = true;
        return _request;
    }

    internal UpdateAvailability Get(InstalledSkill skill)
    {
        var id = UpdateAvailability.Address(skill);
        if (!_entries.TryGetValue(id, out var entry))
            return new(UpdateAvailabilityStatus.NotChecked, "Use Check updates to check this copy explicitly.");
        if (!ReferenceEquals(entry.Target, skill))
        {
            var observation = entry.Evidence == UpdateAvailability.Evidence(skill) ? entry.Observation
                : Stale(entry.Observation, "Installation, source, ref, pin, owner or consumer evidence changed.");
            entry = entry with { Target = skill, Observation = observation };
            _entries[id] = entry;
        }
        return entry.Observation;
    }

    internal bool Publish(long request, long generation, string context, InstalledSkill skill, UpdateAvailability observation)
    {
        var id = UpdateAvailability.Address(skill);
        if (!Running || request != _request || generation != _catalogGeneration || context != _context
            || !_entries.TryGetValue(id, out var entry)
            || entry.Observation.Status != UpdateAvailabilityStatus.Checking
            || !ReferenceEquals(entry.Target, skill) && entry.Evidence != UpdateAvailability.Evidence(skill)) return false;
        _entries[id] = entry with { Observation = observation };
        Completed++;
        return true;
    }

    internal bool IsCurrent(long request) => Running && _request == request;
    internal void Complete(long request) { if (_request == request) Running = false; }

    internal void Cancel()
    {
        _request++;
        Running = false;
        if (_entries.Count == 0) _failure = "Update check canceled";
        foreach (var (id, entry) in _entries)
            if (entry.Observation.Status == UpdateAvailabilityStatus.Checking)
                _entries[id] = entry with
                {
                    Observation = new(UpdateAvailabilityStatus.Canceled,
                    "Canceled before a complete answer for this copy.", DateTimeOffset.Now)
                };
    }

    internal void Invalidate()
    {
        _request++;
        Running = false;
        foreach (var (id, entry) in _entries)
            _entries[id] = entry with
            {
                Observation = Stale(entry.Observation,
                "Disk refresh, context change or operation invalidated this observation. Check again.")
            };
    }

    private static UpdateAvailability Stale(UpdateAvailability previous, string reason) =>
        previous.Status == UpdateAvailabilityStatus.Stale ? previous : previous with
        { Status = UpdateAvailabilityStatus.Stale, Reason = reason + $" Previous result: {previous.Label}. {previous.Reason}" };
}
