using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    // Retirement has no authority over the live payload or registry. Historical
    // replacement admission remains exclusively in ordinary Observe/Admit.
    private (ReplacementObservation Observation, ReplacementEvidence? Live) InspectPrivateRetirement(
        Session session, InventorySnapshot inventory, CancellationToken token)
    {
        RequireRetirementInventory(session, inventory, token);
        var plan = session.Plan;
        var entries = RetirementEntries(session.Operation, token);
        ReplacementSlot? Slot(string name, ReplacementEvidence expected)
        {
            if (!entries.Any(entry => entry.Name == name)) return null;
            var snapshot = CaptureRetirementDirectory(session.Operation, name, token);
            VerifyPayload(expected, snapshot);
            return RetirementSlot(snapshot);
        }
        var candidate = Slot("candidate", plan.Candidate);
        var original = Slot("original", plan.Original);
        var displaced = Slot("displaced", plan.Candidate);
        if (plan.Revision?.RecordedBase is { } baseline && Slot("recorded-base", baseline) is null)
            throw new IOException("Retirement requires the immutable recorded-base slot.");
        var topology = (candidate is not null, original is not null, displaced is not null) switch
        {
            (true, false, false) => "ready",
            (false, true, false) => "installed",
            (false, false, true) => "restored",
            (true, true, false) => "original-retained",
            (false, true, true) => "replacement-retained",
            _ => throw new IOException("Private retirement slots are incomplete or ambiguous; preserve for specialist inspection.")
        };
        var live = CaptureRetirementLive(plan, token);
        if (topology is "installed" or "restored" && live.Evidence is null)
            throw new IOException("Discard Undo requires a current live copy; the retained original may be the only copy. Preserve this operation.");
        foreach (var entry in entries.Where(entry => entry.Directory))
            RequireNoRetirementConsumers(session, entry.Name, inventory, token);
        return (new(topology, live.Slot, original, candidate, displaced, live.Evidence?.Identity ?? default, live.Parent), live.Evidence);
    }

    private void RequireRetirementInventory(Session session, InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (inventory.IsPartial || !inventory.ConfiguredRootsComplete || inventory.ConfiguredRoots.Length > 512
            || !inventory.OwnershipReadDiagnostics.IsEmpty || inventory.OwnershipRecords.Any(claim => claim.Diagnostic is not null)
            || inventory.Diagnostics.Locations.Any(location => location.State == "partial"))
            throw new IOException("Retirement requires fresh complete inventory and readable ownership observations; resolve coverage diagnostics first.");
        CheckPlacement(inventory, token);
        NativeStateDirectory.CheckExcluded(session.Operation.CanonicalPath, Exclusions(inventory));
        foreach (var claim in inventory.OwnershipRecords)
            foreach (var path in claim.CandidatePaths(session.Plan.Subject.Name))
            {
                token.ThrowIfCancellationRequested();
                var address = QuarantineStore.ResolveExposureAddress(path, token);
                if (PathIdentity.IsInside(address, session.Operation.CanonicalPath)
                    || PathIdentity.IsInside(session.Operation.CanonicalPath, address))
                    throw new IOException("Current ownership evidence overlaps private retained state; preserve it for specialist inspection.");
            }
    }

    private static void RequireNoRetirementConsumers(Session session, string name, InventorySnapshot inventory, CancellationToken token)
    {
        // Historical memberships are read-only locators too: inspect their
        // current destinations without requiring their old context/fingerprint.
        // A removed scan location must not hide a known link into private data.
        var consumers = inventory with { Skills = inventory.Skills.Add(Subject(session.Plan)) };
        QuarantineStore.EnsureNoConsumers(Path.Combine(session.Operation.CanonicalPath, name), consumers, token);
    }

    private static ReplacementSlot RetirementSlot(PayloadSnapshot snapshot) =>
        new(snapshot.Path, snapshot.Fingerprint, snapshot.ObjectStateFingerprint!, snapshot.RootUnixMode, snapshot.RootIdentity!.Value);

    private static (ReplacementSlot? Slot, ReplacementEvidence? Evidence, SecureFileIdentity Parent) CaptureRetirementLive(
        ReplacementPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parentPath = Path.GetDirectoryName(plan.OriginalPath)!;
        if (!RetirementPathPresent(parentPath)) return (null, null, default);
        using var parent = OwnedDirectoryLease.Open(parentPath);
        var before = parent.Capture(token);
        if (before.Identity.IsReparsePoint || before.Identity.CanonicalPath != parentPath)
            throw new IOException("Current live parent is aliased or cannot be captured at its recorded address.");
        ReplacementEvidence? evidence = null;
        ReplacementSlot? slot = null;
        if (RetirementPathPresent(plan.OriginalPath))
        {
            using var held = OwnedDirectoryLease.Open(plan.OriginalPath);
            var liveBefore = held.Capture(token);
            if (liveBefore.Identity.IsReparsePoint || liveBefore.Identity.CanonicalPath != plan.OriginalPath)
                throw new IOException("Current live copy is aliased; preserve private evidence.");
            var snapshot = PayloadSnapshot.CaptureEvidence(plan.OriginalPath, token);
            if (snapshot.RootIdentity != liveBefore.Identity with { DirectoryFingerprint = null } || held.Capture(token) != liveBefore)
                throw new IOException("Current live copy changed during retirement capture; Inspect again.");
            evidence = ReplacementEvidence.From(snapshot);
            slot = RetirementSlot(snapshot);
        }
        if (parent.Capture(token) != before) throw new IOException("Current live parent changed during retirement capture; Inspect again.");
        return (slot, evidence, before.Identity with { DirectoryFingerprint = null });
    }

    private static bool RetirementPathPresent(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static RetirementLiveGuard HoldRetirementLive(ReplacementPlan plan, RetirementMarker marker, CancellationToken token)
    {
        var expected = MarkerLive(plan, marker);
        if (expected is null)
            return new(null, () =>
            {
                var live = CaptureRetirementLive(plan, token);
                if (live.Evidence is not null || live.Parent != marker.Before.Parent)
                    throw new IOException("The missing live observation changed during retirement; remaining evidence is protected.");
            });
        var held = OwnedDirectoryLease.Open(plan.OriginalPath);
        try
        {
            var before = held.Capture(token);
            if (before.Identity with { DirectoryFingerprint = null } != expected.Identity)
                throw new IOException("Current live identity changed before retirement removal; preserve remaining evidence.");
            return new(held, () =>
            {
                token.ThrowIfCancellationRequested();
                if (held.Capture(token) != before
                    || !SecureRemovalBackend.TryCaptureIdentity(plan.OriginalPath, out var current, out _)
                    || current != expected.Identity
                    || !SecureRemovalBackend.TryCaptureIdentity(Path.GetDirectoryName(plan.OriginalPath)!, out var parent, out _)
                    || parent != marker.Before.Parent)
                    throw new IOException("Current live copy disappeared or was replaced/changed during retirement; remaining evidence is protected.");
            });
        }
        catch { held.Dispose(); throw; }
    }

    private sealed class RetirementLiveGuard(OwnedDirectoryLease? held, Action revalidate) : IDisposable
    {
        internal void Revalidate() => revalidate();
        public void Dispose() => held?.Dispose();
    }
}
