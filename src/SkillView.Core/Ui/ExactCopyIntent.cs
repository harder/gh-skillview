using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

// Re-resolution guard, never execution authority. Native/source/consumer intent
// survives a fresh capture; an unrelated registry sibling may legitimately change
// the whole-file fingerprint. Existing admission rechecks that fresh registry.
internal sealed record ExactCopyIntent(OperationSubject Subject, string Evidence)
{
    internal static ExactCopyIntent Capture(InstalledSkill target) => new(OperationSubject.From(target), Key(target));
    internal InstalledSkill? Resolve(InventorySnapshot snapshot)
    {
        var target = MaintenanceQueueBuilder.ResolveInstallation(snapshot, Subject);
        return Subject.NativeObject.Status == "observed" && target is not null && Key(target) == Evidence ? target : null;
    }
    private static string Key(InstalledSkill target) => UpdateAvailability.Evidence(target with
    {
        OwnershipClaims = target.AllOwnershipClaims.Select(owner => owner with { RecordFingerprint = null }).ToImmutableArray(),
        Package = target.Package is { } owner ? owner with { RecordFingerprint = null } : null
    });
}
