namespace SkillView.Inventory.Models;

internal enum PinDisplayState { Unpinned, Pinned, Conflicting, Unknown }

/// Transient read-only presentation, never a saved field or mutation authority.
internal sealed record PinDisplayPolicy(PinDisplayState State, string? PinnedRef, bool HasPin)
{
    internal static PinDisplayPolicy Unknown { get; } = new(PinDisplayState.Unknown, null, false);

    internal static PinDisplayPolicy From(InstalledSkill skill)
    {
        var explicitUnpinned = skill.FrontMatter.Extra.TryGetValue("pinned", out var raw)
            && string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
        var unreadable = skill.Validity is ValidityState.MissingSkillMd or ValidityState.UnparsableFrontMatter or ValidityState.BrokenSymlink
            || !skill.OwnershipDiagnostics.IsEmpty;
        return Project(skill.Pinned, skill.FrontMatter.PinnedRef, skill.AllOwnershipClaims, explicitUnpinned, unreadable);
    }

    internal static PinDisplayPolicy FromRecorded(OperationSubject subject, bool pinned) =>
        Project(pinned, subject.Revision.PinnedRef, subject.Owners, false, false);

    private static PinDisplayPolicy Project(bool pinned, string? pinnedRef, IEnumerable<SkillPackage> claims,
        bool explicitUnpinned, bool unreadable)
    {
        var owners = claims.ToArray();
        static string? ValidRef(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
        var refs = owners.Select(owner => ValidRef(owner.PinnedRef)).Prepend(ValidRef(pinnedRef))
            .Where(value => value is not null).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        var hasPin = pinned || refs.Length > 0;
        // Absent frontmatter pin metadata is not a contrary claim: lock-only
        // pins are supported. Distinct owner policies do retain disagreement.
        var ownerDisagreement = owners.Where(owner => owner.PinnedRef is null || ValidRef(owner.PinnedRef) is not null)
            .Select(owner => owner.PinnedRef).Distinct(StringComparer.Ordinal).Take(2).Count() > 1;
        if (refs.Length > 1 || ownerDisagreement || explicitUnpinned && hasPin)
            return new(PinDisplayState.Conflicting, null, hasPin);
        if (pinnedRef is not null && ValidRef(pinnedRef) is null
            || owners.Any(owner => owner.PinnedRef is not null && ValidRef(owner.PinnedRef) is null))
            return new(PinDisplayState.Unknown, null, hasPin);
        if (hasPin) return new(PinDisplayState.Pinned, refs.FirstOrDefault(), true);
        if (unreadable) return Unknown;
        return new(PinDisplayState.Unpinned, null, false);
    }

    internal string Label => State switch
    {
        PinDisplayState.Pinned => "Yes",
        PinDisplayState.Unpinned => "No",
        PinDisplayState.Conflicting => "Conflicting",
        _ => "Unknown"
    };

    internal string Flag => State switch
    {
        PinDisplayState.Pinned => "p",
        PinDisplayState.Unpinned => "-",
        PinDisplayState.Conflicting => "!",
        _ => "?"
    };

    internal string Description => State switch
    {
        PinDisplayState.Pinned => PinnedRef ?? "pinned; ref unknown",
        PinDisplayState.Unpinned => "no pin recorded",
        PinDisplayState.Conflicting => "conflicting pin records; review ownership claims before changing this copy",
        _ => "pin state unknown; metadata or ownership is unreadable or invalid"
    };

    internal string Guidance => State switch
    {
        PinDisplayState.Pinned => "Routine updates leave this version pinned; explicit unpin review is required.",
        PinDisplayState.Conflicting => "Pin records disagree; inspect the ownership claims before requesting a change.",
        PinDisplayState.Unknown => "Pin state could not be verified; inspect metadata and ownership before requesting a change.",
        _ => "No pin recorded."
    };
}
