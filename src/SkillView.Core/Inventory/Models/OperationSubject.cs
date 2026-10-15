using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace SkillView.Inventory.Models;

/// An observed distributor label is not proof of an upstream repository or a
/// globally unique marketplace. Logical identity deliberately excludes version
/// and delivery address. Every claim remains available alongside this projection.
public sealed record SkillDistributorIdentity(string Id, string Provider, string Locator,
    string Evidence, string DeliveryId, string? Version, string? Boundary,
    string? RegistryPath, string? Context, SkillPackage Owner)
{
    internal static SkillDistributorIdentity From(SkillPackage owner) => From(owner, owner.ManagedRegion);

    internal static SkillDistributorIdentity From(SkillPackage owner, ManagedSkillRegion? region)
    {
        var locator = owner.PluginName ?? owner.SourceUrl ?? owner.Source;
        var evidence = owner.ReadOnly ? "observed-distributor-label" : "contextual-owner-record";
        // A local manifest name has meaning only in its own installation context.
        var authority = owner.Provider.StartsWith("local-", StringComparison.Ordinal)
            ? region?.ObservedCanonicalPath ?? region?.Path ?? owner.ContextRoot ?? owner.InstallationRoot : null;
        // A plugin label is only one claim: distinct recorded repositories must
        // never collapse into one destructive package group.
        var source = NormalizeSource(owner.Source, owner.SourceType);
        var sourceUrl = owner.SourceUrl is { } url ? NormalizeSource(url, owner.SourceType) : null;
        var id = Digest(owner.Provider, owner.SourceType, owner.PluginName, source, sourceUrl, authority);
        var boundary = region?.ObservedCanonicalPath ?? region?.Path ?? owner.InstallationRoot;
        var registry = region?.RegistrationPath ?? owner.LockFilePath;
        var context = region?.Context ?? owner.ContextRoot;
        var roots = owner.CoveredRoots.Order(StringComparer.Ordinal).ToArray();
        return new(id, owner.Provider, locator, evidence,
            Digest([id, registry, context, boundary,
                owner.PackageVersion, roots.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), .. roots]),
            owner.PackageVersion, boundary, registry, context, owner);
    }

    private static string NormalizeSource(string source, string type)
    {
        if (type == "github" && source.Split('/').Length == 2 && !source.Contains(':'))
            return SkillSourceIdentity.NormalizeRepository("https://github.com/" + source);
        return type is "github" or "git" ? SkillSourceIdentity.NormalizeRepository(source) : source;
    }

    internal static string Digest(params string?[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            foreach (var value in values)
            {
                writer.Write(value is not null);
                if (value is not null) writer.Write(value);
            }
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }
}

/// Native values are strings so JSON consumers do not lose 64/128-bit IDs.
/// This evidence never grants execution authority and may already be stale.
public sealed record NativeObjectObservation(string Status, string Role, DateTimeOffset? CapturedAt,
    string? Platform, string? Volume, string? ObjectId, string? CanonicalPath,
    string? ChangeTimeSeconds, string? ChangeTimeNanoseconds,
    string? WindowsCreationTime, string? WindowsChangeTime, string? Diagnostic)
{
    internal static NativeObjectObservation Unknown(string reason) => new("unknown", "inventory-observation",
        null, null, null, null, null, null, null, null, null, reason);

    internal static NativeObjectObservation From(SecureFileIdentity identity, DateTimeOffset at, string role) =>
        new("observed", role, at, OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            identity.Volume.ToString("x16"), identity.FileIdHigh.ToString("x16") + identity.FileIdLow.ToString("x16"),
            identity.CanonicalPath, identity.ChangeTimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            identity.ChangeTimeNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            identity.WindowsCreationTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
            identity.WindowsChangeTime.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
}

public sealed record ObservedSkillExposure(AgentMembership Membership, string? EntryAddress, string? EntryKey = null);

/// Immutable pre-attempt facts. This is an output model, never a journal/hash
/// input. Historical evidence must not be filled by inspecting today's address.
public sealed record OperationSubject(string InstallationId, string AddressRole, string Path,
    string? Name, Scope? Scope, SkillSourceIdentity Source, SkillRevision Revision,
    ImmutableArray<SkillDistributorIdentity> Distributors, ImmutableArray<SkillPackage> Owners,
    NativeObjectObservation NativeObject, ImmutableArray<ObservedSkillExposure> Exposures,
    DateTimeOffset? CapturedAt, bool ExposureCoverageComplete, string ExposureCoverage)
{
    public string Materialization { get; init; } = "unknown";

    internal static OperationSubject FromRecorded(string id, string path, string? name, Scope? scope,
        SkillFrontMatter? metadata, ImmutableArray<AgentMembership> agents, ImmutableArray<SkillPackage> owners,
        DateTimeOffset? capturedAt = null, NativeObjectObservation? native = null, string? fingerprint = null)
    {
        var model = new InstalledSkill
        {
            Name = name ?? "unknown",
            ResolvedPath = path,
            ScanRoot = path,
            Scope = scope ?? Models.Scope.Custom,
            FrontMatter = metadata ?? new(),
            Agents = agents,
            OwnershipClaims = owners,
            Package = owners.FirstOrDefault(),
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
            PayloadFingerprint = fingerprint
        };
        return new(id, "installation-address", path, name, scope, model.SourceIdentity, model.Revision,
            owners.Select(SkillDistributorIdentity.From).ToImmutableArray(), owners,
            native ?? NativeObjectObservation.Unknown("Native object was not recorded."),
            agents.Select(agent => new ObservedSkillExposure(agent, null)).ToImmutableArray(), capturedAt,
            false, "historical or uncaptured discovery coverage; other consumers may exist");
    }

    internal static OperationSubject From(InstalledSkill skill) => skill.ObservedSubject ?? FromRecorded(
        skill.InstallationId, skill.ResolvedPath, skill.Name, skill.Scope, skill.FrontMatter, skill.Agents,
        skill.AllOwnershipClaims.ToImmutableArray(), fingerprint: skill.PayloadFingerprint) with
    { Source = skill.SourceIdentity, Revision = skill.Revision, Materialization = skill.PhysicalInstallation.Materialization };
}

public sealed record OperationImpact(OperationSubject Subject, string Operation, string TargetPath,
    bool PayloadRetained, ImmutableArray<ObservedSkillExposure> AffectedExposures,
    ImmutableArray<ObservedSkillExposure> RetainedExposures, bool ExposureCoverageComplete,
    string ExposureCoverage, NativeObjectObservation? ExecutionEvidence = null)
{
    public string? RetainedPayloadPath { get; init; }
    public string? RetentionEvidence { get; init; }
    internal static OperationImpact Payload(OperationSubject subject, string operation) => new(subject, operation,
        subject.Path, false, subject.Exposures, [], subject.ExposureCoverageComplete, subject.ExposureCoverage);

    internal static OperationImpact Link(OperationSubject subject, AgentMembership link)
    {
        var entry = subject.Exposures.FirstOrDefault(exposure => exposure.Membership == link)?.EntryKey;
        var affected = subject.Exposures.Where(exposure => entry is not null
            ? string.Equals(exposure.EntryKey, entry, StringComparison.Ordinal)
            : string.Equals(exposure.Membership.Path, link.Path, StringComparison.Ordinal)).ToImmutableArray();
        var retainsPayload = subject.Materialization == "directory";
        return new(subject, "unlink", link.Path, retainsPayload, affected,
            subject.Exposures.Except(affected).ToImmutableArray(), subject.ExposureCoverageComplete && entry is not null,
            entry is null ? "link entry address unobserved; other contexts may exist" : subject.ExposureCoverage)
        {
            RetainedPayloadPath = retainsPayload ? subject.Path : null,
            RetentionEvidence = retainsPayload ? "inventory-observed-payload; link-only operation"
                : subject.Materialization == "broken-link" ? "inventory-observed-broken-link; no payload observed" : "payload materialization unrecorded"
        };
    }
}
