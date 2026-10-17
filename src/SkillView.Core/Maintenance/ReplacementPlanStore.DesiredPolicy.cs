using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Preferences;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    private const string DesiredPolicyReceiptName = "desired-policy.json";
    private DesiredRevisionPolicyStore DesiredPolicies => runner is null
        ? new(Path.Combine(Path.GetDirectoryName(Root)!, "desired-state")) : DesiredRevisionPolicyStore.For(runner);

    private DesiredRevisionPolicyStore.WriterLease AcquireDesiredPolicy(InstalledSkill target, InventorySnapshot inventory,
        string operation, CancellationToken token, bool explicitUnpin = false)
    {
        // Lock order: desired policy -> replacement root -> operation -> registry
        // -> native payload authorities. No callback or cancellation is initiated
        // while an application ownership lock is held here.
        var prior = DesiredPolicies.Observe(target, Exclusions(inventory), token);
        DesiredRevisionPolicyGate.RequireAllowed(target, prior, operation, explicitUnpin);
        var lease = DesiredPolicies.Acquire(Exclusions(inventory), token);
        try { _ = DesiredRevisionPolicyGate.Witness(lease, target, operation, token, explicitUnpin); return lease; }
        catch { lease.Dispose(); throw; }
    }

    private void WriteDesiredPolicyReceipt(NativeStateDirectory operation, ReplacementPlan plan,
        DesiredRevisionPolicyStore.WriterLease policy, CancellationToken token)
    {
        var witness = DesiredRevisionPolicyGate.Witness(policy, Subject(plan), plan.Purpose, token,
            explicitUnpin: plan.Subject.Metadata.Pinned && plan.Revision?.DesiredMetadata.Pinned == false);
        using (var file = operation.OpenFile(DesiredPolicyReceiptName, false, token))
        {
            _ = BoundedReviewJson.Write(file, new DesiredPolicyReceipt(1, plan.ReviewFingerprint, witness),
                DesiredPolicyJsonContext.Default.DesiredPolicyReceipt, DesiredRevisionPolicyStore.MaxRecordBytes, token);
            file.Flush(true);
        }
        operation.FlushPublication(token);
    }

    private void RequireDesiredPolicyReceipt(Session session, DesiredRevisionPolicyStore.WriterLease policy,
        CancellationToken token)
    {
        var plan = session.Plan;
        var target = Subject(plan);
        var current = policy.Observe(target, token);
        DesiredRevisionPolicyGate.RequireAllowed(target, current, plan.Purpose,
            explicitUnpin: plan.Subject.Metadata.Pinned && plan.Revision?.DesiredMetadata.Pinned == false);
        var entries = session.Operation.EnumerateEntries(token).Where(entry => entry.Name == DesiredPolicyReceiptName).ToArray();
        if (entries.Length == 0)
        {
            if (current.Status != "absent" || current.Sequence is not null) throw new IOException("A desired-state policy now applies, or its history changed since this legacy ready plan. Create a fresh plan and review its policy witness.");
            return; // Existing historical no-policy plans keep their hash/read contract.
        }
        using var file = session.Operation.OpenExistingFile(DesiredPolicyReceiptName, false, token);
        var receipt = BoundedReviewJson.Read(file, DesiredPolicyJsonContext.Default.DesiredPolicyReceipt,
            DesiredRevisionPolicyStore.MaxRecordBytes, token).Value;
        var expectedPlan = plan.LocalDisposition?.SourcePlanFingerprint ?? plan.ReviewFingerprint;
        var fresh = DesiredRevisionPolicyGate.Witness(policy, target, plan.Purpose, token,
            explicitUnpin: plan.Subject.Metadata.Pinned && plan.Revision?.DesiredMetadata.Pinned == false);
        if (receipt.SchemaVersion != 1 || receipt.PlanFingerprint != expectedPlan || receipt.Witness != fresh)
            throw new IOException("Desired-state policy or context changed after plan review; prepare a fresh policy-bound candidate.");
    }
}
