using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    // Read-only native I observation. Capture reopens and independently compares
    // this digest; preview grants neither desired intent nor source approval.
    internal ReplacementEvidence PreviewApprovedPayload(InstalledSkill target, InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ManagedSkillDiscovery.RefuseTarget(target, token, runner);
        var canonical = Canonical(target.ResolvedPath);
        var root = inventory.ScannedRoots.Select(scan => Canonical(scan.Path))
            .Where(path => !PathIdentity.Equals(path, canonical) && PathIdentity.IsInside(canonical, path))
            .OrderByDescending(path => path.Length).FirstOrDefault()
            ?? throw new IOException("Approval preview needs an existing installation under its actual scan root.");
        target = target with { ResolvedPath = canonical, ScanRoot = root };
        var transition = PrepareOwner(target, null, inventory, token, initializeState: false, approvedMetadata: target.FrontMatter);
        using var owner = OpenOwner(transition, token, requireBefore: true, exactEntryBytes: true);
        inventory = Admit(target, inventory, token, owner: owner, approvedCapture: true);
        var context = Context(target, inventory, token, owner: owner);
        using var held = NativeDirectoryMoveLease.Open(root, canonical, token);
        var snapshot = held.Capture(token);
        var authored = held.CaptureInto((_, _) => { }, null, token);
        VerifyPayload(ReplacementEvidence.From(snapshot), LegacyProjection(authored, token));
        if (authored.FrontMatter is not { } metadata || !metadata.Diagnostics.IsEmpty || !SameParsedMetadata(target.FrontMatter, metadata))
            throw new IOException("Installed metadata changed; refresh the exact installation.");
        held.Revalidate(token);
        VerifyPayload(ReplacementEvidence.From(snapshot), held.Capture(token));
        if (Context(target, inventory, token, owner: owner) != context) throw new IOException("Approval preview context changed.");
        return ReplacementEvidence.From(snapshot);
    }
}
