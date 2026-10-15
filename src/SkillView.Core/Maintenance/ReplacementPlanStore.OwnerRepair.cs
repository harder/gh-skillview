using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Threading;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    internal ReplacementResult RepairOwner(string id, string expectedObservation, InventorySnapshot inventory, CancellationToken token)
    {
        using var session = Open(id, inventory, token);
        var plan = session.Plan;
        using var owner = OpenOwner(plan.Revision?.Owner, token, recoverySession: session, inventory: inventory)
            ?? throw new IOException("This plan has no shared registry owner.");
        var current = Observe(plan, session.Operation, inventory, token, owner);
        if (expectedObservation != ObservationHash(plan, current))
            throw new IOException("Repair bytes, native evidence or context changed; inspect again before proceeding.");
        if (current.Owner is not { State: "torn", Repair: { } repair } || owner.RepairBytes is not { } image)
            throw new IOException("Explicit owner repair requires recognized torn bytes and a reviewed full image.");
        var undo = repair.Direction == "undo";
        if (!undo && session.RecoveryJournal is not null)
            throw new IOException("A forward repair cannot use the undo recovery journal.");
        if (undo && (session.StepCount > MaxSteps - 2 || session.RecoveryJournal is not null))
            session.UseRecoveryJournal(Exclusions(inventory), token);
        if (session.StepCount > MaxSteps - 2) throw new IOException("No journal capacity remains for the entire repair.");
        using var journal = OpenOwnerJournal(session, inventory, token, create: false)
            ?? throw new IOException("Retained owner evidence is missing.");
        journal.Reserve(undo);
        var snapshot = owner.Held.Read(token);
        if (snapshot.ContentHash != current.Owner.Hash || snapshot.Identity != current.Owner.Identity
            || NativeRegistryFileLease.Hash(image) != repair.ImageHash || image.Length != repair.Bytes)
            throw new IOException("Owner repair image or current snapshot changed; inspect again.");
        var writeId = Guid.NewGuid().ToString("N");
        Append("owner-repair-intent", current, token);
        checkpoint?.Invoke("owner-repair-intent");
        var result = owner.Held.RepairImage(snapshot, image, journal.Directory,
            new(plan.ReviewFingerprint, "repair", writeId, repair.WriteId, repair.Direction), token);
        if (!result.Succeeded)
            return Result(plan, "needs-recovery", false, result.MutationAttempted, result.Canceled, "",
                "Owner repair was interrupted. Retained evidence requires fresh inspection. " + result.Error);
        try
        {
            checkpoint?.Invoke("owner-repaired");
            using var reconciliation = new CancellationSource(TimeSpan.FromSeconds(30), CancellationCallbackReporter.For(logger, "Owner repair reconciliation"));
            current = Observe(plan, session.Operation, inventory, reconciliation.Token, owner);
            Append("owner-repair-complete", current, reconciliation.Token);
            return Result(plan, "owner-repaired", true, result.MutationAttempted, token.IsCancellationRequested, ObservationHash(plan, current),
                "Only the reviewed registry image was repaired. Payload locations are unchanged. Inspect again before explicit recovery or undo; agent session state is unknown.", current.Owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return Result(plan, "needs-recovery", false, result.MutationAttempted, token.IsCancellationRequested, "",
                "Registry repair wrote its image; terminal verification needs fresh inspection. " + ex.Message);
        }

        void Append(string status, ReplacementObservation observation, CancellationToken ct)
        {
            var step = new ReplacementStep(session.StepCount, plan.ReviewFingerprint, status, observation, writeId);
            Write(session.Journal, $"step-{session.StepCount:D4}.json", step, ReplacementJsonContext.Default.ReplacementStep, ct);
            session.StepCount++;
            session.LatestOwnerStep = step;
            session.OwnerWriteIds.Add(writeId);
        }
    }
}
