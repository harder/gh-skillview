using System.Collections.Immutable;
using SkillView.Inventory.Models;

namespace SkillView.Inventory;

/// Read-only, bounded per-install observation. Call only from background work;
/// renderers consume the frozen records without opening paths or hashing payloads.
internal static class SkillIdentityObserver
{
    internal static OperationImpact CaptureEntry(string path, string operation, DateTimeOffset at, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var native = SecureRemovalBackend.TryObserveIdentity(path, out var identity, out var error)
            ? NativeObjectObservation.From(identity, at, "inventory-entry-observation")
            : NativeObjectObservation.Unknown(error ?? "Native entry identity unavailable.");
        var subject = OperationSubject.FromRecorded(InstalledSkill.AddressId(path), path, null, null,
            null, [], [], at, native) with
        { AddressRole = "filesystem-entry-address" };
        return OperationImpact.Payload(subject, operation);
    }

    internal static OperationSubject Capture(InstalledSkill skill, DateTimeOffset at,
        bool coverageComplete, CancellationToken token, ImmutableArray<ManagedSkillRegion> regions = default)
    {
        token.ThrowIfCancellationRequested();
        NativeObjectObservation native;
        if (SecureRemovalBackend.TryObserveIdentity(skill.ResolvedPath, out var identity, out var error))
            native = NativeObjectObservation.From(identity, at, "inventory-observation");
        else native = NativeObjectObservation.Unknown(error ?? "Native directory identity unavailable.");
        var exposures = ImmutableArray.CreateBuilder<ObservedSkillExposure>();
        foreach (var agent in skill.Agents)
        {
            token.ThrowIfCancellationRequested();
            string? entry = null, entryKey = null;
            try
            {
                entry = OwnershipPath.EntryAddress(agent.Path);
                if (entry is not null) entryKey = PathIdentity.NormalizeKey(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            if (entry is null) coverageComplete = false;
            exposures.Add(new(agent, entry, entryKey));
        }
        var distributors = ImmutableArray.CreateBuilder<SkillDistributorIdentity>();
        foreach (var owner in skill.AllOwnershipClaims)
        {
            token.ThrowIfCancellationRequested();
            var matching = owner.ManagedRegion is { } captured ? [captured] : regions.IsDefaultOrEmpty ? [] : regions.Where(region =>
            {
                token.ThrowIfCancellationRequested();
                return region.Provider == owner.Provider && region.PluginId == owner.PluginName
                    && region.PackageVersion == owner.PackageVersion
                    && (owner.ContextRoot is null || region.Context == owner.ContextRoot)
                    && (region.ObserveCoverage(skill.ResolvedPath) == true || skill.Agents.Any(agent => region.ObserveCoverage(agent.Path) == true));
            }).ToArray();
            if (matching.Length == 0) distributors.Add(SkillDistributorIdentity.From(owner));
            else foreach (var region in matching) distributors.Add(SkillDistributorIdentity.From(owner, region));
        }
        return OperationSubject.FromRecorded(skill.InstallationId, skill.ResolvedPath, skill.Name, skill.Scope,
            skill.FrontMatter, skill.Agents, skill.AllOwnershipClaims.ToImmutableArray(), at, native, skill.PayloadFingerprint) with
        {
            Materialization = skill.PhysicalInstallation.Materialization,
            Source = skill.SourceIdentity,
            Revision = skill.Revision,
            Exposures = exposures.ToImmutable(),
            Distributors = distributors.DistinctBy(distributor => distributor.DeliveryId).ToImmutableArray(),
            ExposureCoverageComplete = coverageComplete,
            ExposureCoverage = coverageComplete
                ? "complete within captured configured roots; other unscanned contexts and active sessions are unknown"
                : "partial configured-root coverage; other consumers may exist"
        };
    }
}
