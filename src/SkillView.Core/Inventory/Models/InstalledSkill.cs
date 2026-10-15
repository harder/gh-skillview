using System.Collections.Immutable;

namespace SkillView.Inventory.Models;

/// A physical skill install. One record per resolved path — shared installs
/// and symlinks collapse into the same `InstalledSkill` with multiple
/// `AgentMembership` entries.
public sealed record InstalledSkill
{
    /// Front-matter `name` when present, otherwise the directory name.
    public required string Name { get; init; }

    /// Canonical, realpath-resolved install location. Used as the merge key
    /// between `gh skill list` records and filesystem scan.
    public required string ResolvedPath { get; init; }

    /// The scan root that contained the canonical path, or the first root that
    /// discovered it. Drives scope classification and the remove-safety
    /// containment check.
    public required string ScanRoot { get; init; }

    public required Scope Scope { get; init; }

    /// Agents whose skill directory lands on this install (directly or via
    /// symlink).
    public required ImmutableArray<AgentMembership> Agents { get; init; }

    public required SkillFrontMatter FrontMatter { get; init; }

    public required ValidityState Validity { get; init; }

    public required Provenance Provenance { get; init; }

    /// True if this install carries a `.skillview-ignore` marker.
    public required bool Ignored { get; init; }

    /// True if the canonical path was reached via any symlink.
    public required bool IsSymlinked { get; init; }

    public required DateTimeOffset? InstalledAt { get; init; }

    /// Source-bundle metadata when this install was placed by a packaging
    /// tool (e.g. `npx skills`) that wrote a `.skill-lock.json`. Null for
    /// installs not recorded in any lockfile.
    public SkillPackage? Package { get; init; }
    public ImmutableArray<SkillPackage> OwnershipClaims { get; init; } = [];
    public IEnumerable<SkillPackage> AllOwnershipClaims => OwnershipClaims.IsDefaultOrEmpty
        ? Package is { } package ? [package] : [] : OwnershipClaims;
    public ImmutableArray<string> OwnershipDiagnostics { get; init; } = [];
    // Live policy uses the captured subprocess context, including config-home
    // overrides. This is never serialized into inventory or saved plans.
    internal SkillView.Subprocess.ProcessRunner? OwnershipRunner { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public OperationSubject? ObservedSubject { get; init; }
    public string? PayloadFingerprint { get; init; }
    public SkillSourceIdentity SourceIdentity => SkillSourceIdentity.From(this);
    public SkillRevision Revision => new(FrontMatter.Version, FrontMatter.GithubRef ?? AgreedOwnerValue(owner => owner.RepositoryRef),
        TreeSha ?? AgreedOwnerValue(owner => owner.HashKind == "recorded-git-tree" ? owner.ContentHash : null),
        FrontMatter.PinnedRef ?? AgreedOwnerValue(owner => owner.PinnedRef), PayloadFingerprint,
        AgreedOwnerValue(owner => owner.ContentHash), AgreedOwnerValue(owner => owner.HashKind), AgreedOwnerValue(owner => owner.PackageVersion));
    private string? AgreedOwnerValue(Func<SkillPackage, string?> value)
    {
        var values = AllOwnershipClaims.Select(value).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return values.Length == 1 ? values[0] : null;
    }
    public PhysicalSkillInstallation PhysicalInstallation => new(InstallationId, ResolvedPath,
        Validity == ValidityState.BrokenSymlink ? "broken-link" : "directory", AllOwnershipClaims.ToImmutableArray());

    /// Disagreements remain visible; they must not silently authorize maintenance.
    public ImmutableArray<string> MetadataConflicts { get; init; } = [];
    public string InstallationId => AddressId(ResolvedPath);
    internal static string AddressId(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(PathIdentity.NormalizeKey(path))));

    public string? TreeSha => FrontMatter.GithubTreeSha;
    public bool Pinned => FrontMatter.Pinned;
}
