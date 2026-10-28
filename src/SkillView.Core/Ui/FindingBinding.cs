using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Preferences;

namespace SkillView.Ui;

// Finding membership restricts browsing only; fresh specialist admission remains separate.
internal sealed record FindingBinding(string Id, DifferenceBinding? Difference,
    ImmutableHashSet<(string Id, string Path)> Allowed)
{
    internal static FindingBinding? Capture(CapturedFinding finding) => finding.Subject is FindingCopies copies
        && finding.Navigation.Contains(FindingNavigation.InspectCopies)
        ? new(finding.Id, finding.Difference, copies.Copies.Select(copy => (copy.InstallationId, copy.Path)).ToImmutableHashSet()) : null;

    // Call only in owned background work, never a draw/filter callback.
    internal bool IsCurrent(InventorySnapshot snapshot, CancellationToken token)
    {
        var matches = MaintenanceQueueBuilder.Build(snapshot, token).Findings
            .Where(current => current.Id == Id && current.Difference == Difference).Take(2).ToArray();
        if (matches.Length != 1 || matches[0].Subject is not FindingCopies copies) return false;
        return Allowed.SetEquals(copies.Copies.Select(copy => (copy.InstallationId, copy.Path)))
            && copies.Copies.All(copy => MaintenanceQueueBuilder.ResolveInstallation(snapshot, copy) is not null);
    }
}
