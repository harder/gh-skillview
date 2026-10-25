using System.Collections.Immutable;

namespace SkillView.Maintenance;

internal sealed record MaintenancePathComparison(string Path, PayloadEntry? Installed, PayloadEntry? Candidate);
internal sealed record MaintenancePlanComparison(string InstalledPayload, string CandidatePayload,
    ImmutableArray<MaintenancePathComparison> Paths)
{
    internal static MaintenancePlanComparison From(ReplacementPlan plan)
    {
        var installed = plan.Original.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var candidate = plan.Candidate.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var paths = installed.Keys.Concat(candidate.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path => new MaintenancePathComparison(path, installed.GetValueOrDefault(path), candidate.GetValueOrDefault(path)))
            .Where(path => path.Installed != path.Candidate).ToImmutableArray();
        return new(plan.Original.Payload, plan.Candidate.Payload, paths);
    }
}
