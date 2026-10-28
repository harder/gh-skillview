using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal sealed record InlineBrowseReturnState(string? Selection, int TopRow, string[] Expanded, object Filters, object? Focus);

// Frozen address seam shared by the two main views. A family is display only;
// callers must deliberately select one exact child before constructing this.
internal sealed record InlineCopyRequest(InventorySnapshot Snapshot, long Generation, string Context,
    long Epoch, InstalledSkill Target, OperationSubject Subject, ImmutableArray<InstalledSkill> Choices,
    object ReturnState)
{
    internal FindingBinding? Finding { get; init; }
    internal int Omitted { get; init; }
    internal int Ambiguous { get; init; }

    internal static InlineCopyRequest? Capture(InventorySnapshot snapshot, long generation, string context,
        long epoch, InstalledSkill target, object returnState, FindingBinding? binding = null)
    {
        var matches = snapshot.Skills.Where(copy => UpdateAvailability.Address(copy) == UpdateAvailability.Address(target)).Take(2).ToArray();
        if (matches.Length != 1 || !ReferenceEquals(matches[0], target)) return null;
        if (binding is not null && !binding.Allowed.Contains((UpdateAvailability.Address(target), target.ResolvedPath))) return null;
        var group = CopiesPresentation.Build(snapshot, target, CancellationToken.None, binding?.Allowed);
        return new(snapshot, generation, context, epoch, target, OperationSubject.From(target),
            group.Copies.ToImmutableArray(), returnState)
        { Finding = binding, Omitted = group.Omitted, Ambiguous = group.Ambiguous };
    }

    internal static InstalledSkill? ResolveDisplayed(InventorySnapshot snapshot, OperationSubject subject)
    {
        var matches = snapshot.Skills.Where(copy => UpdateAvailability.Address(copy) == subject.InstallationId
            && string.Equals(copy.ResolvedPath, subject.Path, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal bool HasExactTarget => Snapshot.Skills.Count(copy => UpdateAvailability.Address(copy) == Subject.InstallationId) == 1
        && Snapshot.Skills.Any(copy => ReferenceEquals(copy, Target) && copy.ResolvedPath == Subject.Path);
}

internal sealed record CapturedCopyComparison(InlineCopyRequest Request, InstalledSkill Reference,
    SkillComparisonResult Result, DateTimeOffset CapturedAt)
{
    internal bool Matches(InlineCopyRequest request, InstalledSkill? reference) => ReferenceEquals(Request, request)
        && ReferenceEquals(Reference, reference) && request.HasExactTarget
        && Result.LeftInstallationId == UpdateAvailability.Address(Reference) && Result.RightInstallationId == request.Subject.InstallationId;

    internal string Guidance
    {
        get
        {
            var result = Result;
            if (!result.Complete || result.LeftFingerprint is null || result.RightFingerprint is null)
                return "Local capture incomplete; comparison remains unknown.";
            if (result.LeftRootIdentity is not { } left || result.RightRootIdentity is not { } right)
                return "Physical root independence is unknown; different addresses or hashes do not prove separate copies.";
            // Generations establish capture freshness, not physical independence.
            if (left.Volume == right.Volume && left.FileIdLow == right.FileIdLow && left.FileIdHigh == right.FileIdHigh)
                return "One physical payload observed through aliases; preserve every exposure. No extra copy was established.";
            if (!MatchesWitness(Request.Subject.NativeObject, right) || !MatchesWitness(OperationSubject.From(Reference).NativeObject, left))
                return "Captured files; native roots changed since inventory or the inventory witness is unknown. Refresh before further review.";
            if (result.LeftFingerprint != result.RightFingerprint)
                return "Raw captured payloads differ. Normalized source differences may omit installation metadata; preserve both until reviewed.";
            if (result.SourceRelationship is "different-origins" or "different-source-subpaths")
                return "Matching captured files; recorded sources/subpaths differ. Keep origins distinct; bytes do not establish common origin.";
            if (result.SourceRelationship != "same-source" || result.RevisionRelationship is "unknown-recorded-revision" or "incomparable-recorded-revisions")
                return "Matching captured files; source/revision unknown. Matching names, authored versions or bytes establish no common origin.";
            if (result.LeftRootUnixMode is null || result.RightRootUnixMode is null
                || result.LeftRootUnixMode != result.RightRootUnixMode)
                return "Matching captured files; root permissions differ or are unknown. Metadata compatibility is unverified.";
            var snapshot = Request.Snapshot;
            if (snapshot.IsPartial || !snapshot.ConfiguredRootsComplete || Request.Omitted > 0 || Request.Ambiguous > 0 || !Request.Subject.ExposureCoverageComplete
                || !OperationSubject.From(Reference).ExposureCoverageComplete)
                return "Matching captured files; inventory/configured-root/consumer coverage is incomplete. This is not a redundant-copy finding.";
            if (result.RevisionRelationship != "same-recorded-revision" || !ComparableRefs(Request.Target, Reference) || !Compatible(Request.Target) || !Compatible(Reference))
                return "Matching captured files; recorded revision or observed metadata is incompatible or uncertain. Review the exact copies separately.";
            return "Review: independent roots have matching raw captured files, observed modes and recorded source/subpath/revision. Full ACL/xattr/ADS metadata equivalence is unverified; no redundant-copy or non-use claim is made. Choose a copy deliberately; no survivor is preferred.";
        }
    }

    private static bool MatchesWitness(NativeObjectObservation observation, SecureFileIdentity identity) => observation.Status == "observed"
        && observation == NativeObjectObservation.From(identity, observation.CapturedAt ?? DateTimeOffset.MinValue, observation.Role);

    private static bool ComparableRefs(InstalledSkill left, InstalledSkill right)
    {
        static string[] Refs(InstalledSkill copy) => copy.AllOwnershipClaims.Select(owner => owner.RepositoryRef)
            .Append(copy.Revision.RepositoryRef).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // A short ref cannot establish whether a same-spelled head/tag is meant.
        // Equal recorded trees remain a separate domain; authored version is absent.
        return Refs(left).SequenceEqual(Refs(right), StringComparer.Ordinal);
    }

    private static bool Compatible(InstalledSkill copy) => copy.Validity == ValidityState.Valid
        && !copy.SourceIdentity.Conflicted && copy.MetadataConflicts.IsEmpty && copy.OwnershipDiagnostics.IsEmpty
        && copy.FrontMatter.Diagnostics.IsEmpty;
}
