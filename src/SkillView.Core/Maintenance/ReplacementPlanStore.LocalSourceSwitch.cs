using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    private static void RequireOptionalCapturedLocalPath(string? path)
    {
        if (path is not null && (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl)))
            throw new IOException("Captured source local-path must be absent or an absolute unambiguous local address.");
    }

    private void CheckSwitchPlacement(NativePlacementEvidence previous, InventorySnapshot inventory,
        string selectedPath, string selectedCanonical, CancellationToken token)
    {
        // Every address is proven before mkdir; NativeStateDirectory's historical
        // lexical fallback is not authority for this new workflow's old source.
        foreach (var statePath in new[] { Root, LocalPreviewRoot }.Concat(runner is null ? [] : new[] { MaintenanceBackupStore.ResolveRoot(runner), Path.Combine(Path.GetDirectoryName(Root)!, "preferences"), Path.Combine(Path.GetDirectoryName(Root)!, "quarantine") }).Distinct())
        {
            using var state = NativePlacementLease.Open(statePath, token);
            foreach (var source in new[] { previous.Path, previous.CanonicalPath, selectedPath, selectedCanonical })
            {
                if (Overlaps(statePath, source) || Overlaps(state.Evidence.CanonicalPath, source))
                    throw new IOException("Source-switch private state overlaps an old or new source address.");
            }
            state.Revalidate(token);
        }
        foreach (var installed in inventory.Skills.Select(skill => skill.ResolvedPath))
            if (Overlaps(previous.Path, installed) || Overlaps(previous.CanonicalPath, Canonical(installed)))
                throw new IOException("Old-source placement overlaps an installed physical payload.");
        static bool Overlaps(string left, string right) => PathIdentity.Equals(left, right)
            || PathIdentity.IsInside(left, right) || PathIdentity.IsInside(right, left);
    }

    private static void ValidateLocalSourceSwitch(ReplacementPlan plan)
    {
        if (plan.LocalSourceSwitch is not { } change) return;
        if (plan.SchemaVersion != 6 || plan.Purpose != LocalSourceSwitchPurpose || plan.LocalImport is not { } import
            || change.PreviousRecordedPath != plan.Subject.Metadata.LocalPath || change.PreviousPlacement is not { } placement
            || placement.Path != change.PreviousRecordedPath
            || !Path.IsPathFullyQualified(placement.CanonicalPath) || !Path.IsPathFullyQualified(placement.ExistingAncestor.CanonicalPath)
            || !placement.ExistingAncestor.IsDirectory || placement.ExistingAncestor.IsReparsePoint
            || placement.MissingSuffix.IsDefault || placement.MissingSuffix.Length > 128
            || placement.MissingSuffix.Any(part => string.IsNullOrEmpty(part) || part is "." or ".." || part.Any(c => c is '/' or '\\' || char.IsControl(c)))
            || placement.MissingSuffix.Aggregate(placement.ExistingAncestor.CanonicalPath, Path.Combine) != placement.CanonicalPath
            || import.SourcePath == change.PreviousRecordedPath
            || change.SourceRelationship != "selected-existing-local-source" || change.FutureUpdatePolicy != "explicit-local-reimport")
            throw new IOException("Saved local source-switch placement or source relationship is invalid.");
        RequireOptionalCapturedLocalPath(change.CapturedSourceRecordedPath);
    }

    private void RequireFreshPreviousPlacement(ReplacementPlan plan, InventorySnapshot inventory, CancellationToken token)
    {
        if (plan.LocalSourceSwitch is not { } change) return;
        using var placement = NativePlacementLease.Open(change.PreviousRecordedPath, token);
        NativePlacementLease.RequireSame(change.PreviousPlacement, placement.Evidence);
        CheckSwitchPlacement(placement.Evidence, inventory, plan.LocalImport!.SourcePath, plan.LocalImport.CanonicalSourcePath, token);
        placement.Revalidate(token);
    }
}
