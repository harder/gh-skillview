using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Inventory;

namespace SkillView.Maintenance;

public sealed record SkillComparisonResult(string LeftInstallationId, string RightInstallationId,
    string SourceRelationship, string RevisionRelationship, string ContextRelationship,
    string PayloadRelationship, string? LeftFingerprint, string? RightFingerprint,
    ImmutableArray<string> DifferingFiles, int DifferingFileCount, bool Complete,
    bool RequiresExposureMigration, string Recommendation, ImmutableArray<string> Diagnostics)
{
    public string? LeftObjectStateFingerprint { get; init; }
    public string? RightObjectStateFingerprint { get; init; }
    public int? LeftRootUnixMode { get; init; }
    public int? RightRootUnixMode { get; init; }
}

/// Explicit, bounded comparison. Routine inventory does not hash or query sources.
public sealed class SkillComparisonService
{
    private static readonly SemaphoreSlim InspectionSlots = new(2);

    public async Task<SkillComparisonResult> CompareAsync(InstalledSkill left, InstalledSkill right,
        CancellationToken cancellationToken = default, bool inventoryComplete = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = left.Scope != right.Scope || !ExposureContexts(left).SequenceEqual(ExposureContexts(right))
            ? "different-contexts" : "same-context";
        var results = await Task.WhenAll(CaptureAsync(left, cancellationToken), CaptureAsync(right, cancellationToken)).ConfigureAwait(false);
        var diagnostics = results.Where(result => result.Error is not null).Select(result => result.Error!).ToImmutableArray();
        var a = results[0].Snapshot;
        var b = results[1].Snapshot;
        var inspected = a is not null && b is not null;
        if (inspected && a!.RootUnixMode != b!.RootUnixMode)
            diagnostics = diagnostics.Add("Root Unix modes differ; equal file bytes do not establish matching root permissions.");
        var changedTracking = inspected && (!TrackingMatches(left, a!) || !TrackingMatches(right, b!));
        if (changedTracking) diagnostics = diagnostics.Add("Inventory tracking changed; refresh before planning.");
        await InspectionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        ImmutableArray<string> ownerErrors;
        try { ownerErrors = await Task.Run(() => ValidateOwners(left, right, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { InspectionSlots.Release(); }
        diagnostics = diagnostics.AddRange(ownerErrors);
        var complete = inspected && !changedTracking && ownerErrors.IsEmpty;
        if (!inventoryComplete) diagnostics = diagnostics.Add("Inventory coverage is partial; migration planning is unavailable.");
        if (a?.FrontMatter is { } leftMetadata) left = left with { FrontMatter = leftMetadata };
        if (b?.FrontMatter is { } rightMetadata) right = right with { FrontMatter = rightMetadata };
        var source = SourceRelationship(left, right);
        var revision = RevisionRelationship(left, right);
        var differences = inspected ? PayloadSnapshot.Differences(a!, b!) : [];
        var aliases = left.InstallationId == right.InstallationId;
        var payload = !complete ? "incomplete" : a!.Fingerprint == b!.Fingerprint ? "byte-identical"
            : differences.IsEmpty ? "installer-metadata-only" : "different-payloads";
        var migration = complete && inventoryComplete && !aliases && differences.IsEmpty && source == "same-source"
            && revision == "same-recorded-revision" && left.Validity == ValidityState.Valid
            && right.Validity == ValidityState.Valid && a!.RootUnixMode == b!.RootUnixMode && !left.AllOwnershipClaims.Any() && !right.AllOwnershipClaims.Any();
        var recommendation = !complete ? "Resolve incomplete inspection before comparing or planning changes."
            : aliases ? "These observations identify one physical address; preserve all exposures."
            : source is "different-origins" or "different-source-subpaths" ? "Keep origins distinct; matching names or bytes do not transfer ownership."
            : revision != "same-recorded-revision" || context == "different-contexts"
                ? "Review pins and context overrides. No preferred revision is inferred from age or path length."
            : migration ? "Review an exposure-preserving migration plan before consolidating identical copies."
            : "Compare instructions, scripts, resources and permissions; preserve differences until their disposition is explicit.";
        return new(left.InstallationId, right.InstallationId, source, revision, context, payload,
            a?.Fingerprint, b?.Fingerprint, differences.Take(128).ToImmutableArray(), differences.Length,
            complete, migration, recommendation, diagnostics)
        {
            LeftObjectStateFingerprint = a?.ObjectStateFingerprint,
            RightObjectStateFingerprint = b?.ObjectStateFingerprint,
            LeftRootUnixMode = a?.RootUnixMode,
            RightRootUnixMode = b?.RootUnixMode
        };
    }

    internal static string SourceRelationship(InstalledSkill left, InstalledSkill right)
    {
        var a = left.SourceIdentity;
        var b = right.SourceIdentity;
        if (a.Conflicted || b.Conflicted) return "conflicting-provenance";
        if (a.Origin is null || b.Origin is null) return "unknown-origin";
        if (a.Provider != b.Provider || a.Origin != b.Origin) return "different-origins";
        if (a.Provider != "local" && (a.Subpath is null || b.Subpath is null)) return "unknown-source-subpath";
        return a.Subpath == b.Subpath ? "same-source" : "different-source-subpaths";
    }

    internal static string RevisionRelationship(InstalledSkill left, InstalledSkill right)
    {
        var a = RevisionEvidence(left);
        var b = RevisionEvidence(right);
        if (!a["pin"].SequenceEqual(b["pin"])) return "different-pins";
        var comparable = false;
        var incomplete = false;
        foreach (var key in a.Keys.Concat(b.Keys).Distinct(StringComparer.Ordinal).Where(key => key != "pin"))
        {
            var leftValues = a.GetValueOrDefault(key) ?? [];
            var rightValues = b.GetValueOrDefault(key) ?? [];
            if (leftValues.Length == 0 && rightValues.Length == 0) continue;
            if (leftValues.Length == 0 || rightValues.Length == 0) { incomplete = true; continue; }
            comparable = true;
            if (!leftValues.SequenceEqual(rightValues)) return "different-recorded-revisions";
        }
        return incomplete ? "incomparable-recorded-revisions"
            : comparable ? "same-recorded-revision" : "unknown-recorded-revision";
    }

    private static Dictionary<string, string[]> RevisionEvidence(InstalledSkill skill)
    {
        var revision = skill.Revision;
        var claims = skill.AllOwnershipClaims.ToArray();
        var evidence = new Dictionary<string, string[]>()
        {
            ["ref"] = Values(claims.Select(claim => claim.RepositoryRef).Append(revision.RepositoryRef)
                .Select(reference => reference?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? reference[11..]
                    : reference?.StartsWith("refs/tags/", StringComparison.Ordinal) == true ? reference[10..] : reference)),
            ["tree"] = Values(claims.Where(claim => claim.HashKind == "recorded-git-tree").Select(claim => claim.ContentHash).Append(revision.RecordedTree)),
            ["pin"] = Values(claims.Select(claim => claim.PinnedRef).Append(revision.PinnedRef)),
            ["distributor-version"] = Values(claims.Select(claim => claim.PackageVersion).Append(revision.DistributorVersion))
        };
        foreach (var hashes in claims.Where(claim => claim.ContentHash is not null && claim.HashKind is not null
            && claim.HashKind != "recorded-git-tree")
            .GroupBy(claim => claim.HashKind, StringComparer.Ordinal))
            evidence["owner-hash:" + hashes.Key] = Values(hashes.Select(claim => claim.ContentHash));
        return evidence;
        static string[] Values(IEnumerable<string?> values) => values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static ImmutableArray<string> ValidateOwners(InstalledSkill left, InstalledSkill right, CancellationToken token)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (!left.OwnershipDiagnostics.IsDefaultOrEmpty || !right.OwnershipDiagnostics.IsDefaultOrEmpty)
            errors.Add("Owner-record inventory is incomplete; resolve diagnostics before planning.");
        var claims = left.AllOwnershipClaims.Concat(right.AllOwnershipClaims).Distinct().Take(129).ToArray();
        if (claims.Length > 128) return ["Owner-record inspection limit exceeded; refresh before planning."];
        foreach (var group in claims.GroupBy(claim => claim.LockFilePath, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (group.Key is null || group.Any(claim => claim.RecordFingerprint is null || claim.ReadOnly))
            { errors.Add("Owner-record evidence is incomplete or unsupported; refresh before planning."); continue; }
            try
            {
                var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(SkillLockFileReader.ReadBytes(group.Key, token)));
                if (group.Any(claim => claim.RecordFingerprint != fingerprint))
                    errors.Add($"Owner record changed: {group.Key}; refresh before planning.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { errors.Add($"Owner record unavailable: {group.Key}: {ex.Message}"); }
        }
        return errors.ToImmutable();
    }

    private static IEnumerable<(string, Scope?, string?, string?, string?)> ExposureContexts(InstalledSkill skill) =>
        skill.Agents.OrderBy(exposure => exposure.AgentId, StringComparer.Ordinal).ThenBy(exposure => exposure.Scope)
            .ThenBy(exposure => exposure.Context, StringComparer.Ordinal).ThenBy(exposure => exposure.Profile, StringComparer.Ordinal)
            .ThenBy(exposure => exposure.DiscoveryRoot, StringComparer.Ordinal)
            .Select(exposure => (exposure.AgentId, exposure.Scope, exposure.Context, exposure.Profile, exposure.DiscoveryRoot));

    private static bool TrackingMatches(InstalledSkill skill, PayloadSnapshot payload) => payload.FrontMatter is { } metadata
        && metadata.Name == skill.FrontMatter.Name && metadata.Upstream == skill.FrontMatter.Upstream
        && metadata.GithubPath == skill.FrontMatter.GithubPath && metadata.GithubRef == skill.FrontMatter.GithubRef
        && metadata.GithubTreeSha == skill.FrontMatter.GithubTreeSha && metadata.PinnedRef == skill.FrontMatter.PinnedRef
        && metadata.LocalPath == skill.FrontMatter.LocalPath;

    private static async Task<(PayloadSnapshot? Snapshot, string? Error)> CaptureAsync(InstalledSkill skill, CancellationToken token)
    {
        await InspectionSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run<(PayloadSnapshot?, string?)>(() =>
            {
                try { return (PayloadSnapshot.Capture(skill.ResolvedPath, token), null); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return (null, $"{skill.ResolvedPath}: {ex.Message}"); }
            }, token).ConfigureAwait(false);
        }
        finally { InspectionSlots.Release(); }
    }
}
