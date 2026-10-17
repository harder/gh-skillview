using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Preferences;

namespace SkillView.Maintenance;

internal static class DesiredRevisionPolicyGate
{
    internal static void RequireKnown(DesiredPolicyObservation observation)
    {
        if (!observation.Known) throw new IOException(observation.Reason);
    }

    internal static void RequireAllowed(InstalledSkill target, DesiredPolicyObservation observation,
        string operation, bool explicitUnpin = false, SelectedRevisionRequest? selection = null)
    {
        RequireKnown(observation);
        if (target.AllOwnershipClaims.Any(owner => owner.ReadOnly || owner.IsExclusiveOwner || owner.PluginName is not null))
            throw new IOException("Native-manager policy retains this installation's package owner boundary; use its verified owner workflow.");
        if (operation == "unpin-only") return; // Pin-only evidence/retention remains mandatory below this gate.
        if (operation == ReplacementPlanStore.SelectedRevisionPurpose)
        {
            (selection ?? throw new IOException("Selected policy admission requires the exact saved request.")).Validate();
            if (target.Pinned && !selection.Pin) throw new IOException("Selected revision cannot implicitly clear the installed pin.");
        }
        var mode = observation.Policy?.Mode ?? "follow-gh-default";
        if (mode == "follow-chosen-track")
            throw new IOException("The chosen track has no verified provider update contract. Intent is retained; no branch-update flag or fallback was invented.");
        if (mode == "manual-fork" && operation == "local-reimport") return; // Explicit same-source review, never routine update.
        if (mode == "exact-pin" && operation == ReplacementPlanStore.SelectedRevisionPurpose && selection is { Pin: true }
            && observation.Policy!.RequestedRef == selection.Selector) return;
        if (mode != "follow-gh-default")
            throw new IOException("Desired-state policy " + mode + " retains this installation. Select an explicit policy/approved-revision workflow; routine maintenance and force do not override it.");
        if (target.Pinned && operation == "update-revision" && !explicitUnpin)
            throw new IOException("Exact pin retained. Unpin must be explicitly reviewed for this installation.");
    }

    internal static DesiredPolicyWitness Witness(DesiredRevisionPolicyStore.WriterLease lease, InstalledSkill target,
        string operation, CancellationToken token, bool explicitUnpin = false, SelectedRevisionRequest? selection = null)
    {
        var observation = lease.Observe(target, token);
        RequireAllowed(target, observation, operation, explicitUnpin, selection);
        return new(1, lease.Address, DesiredRevisionPolicyStore.BindingHash(DesiredRevisionPolicyStore.Bind(target)),
            observation.Fingerprint!, observation.Policy?.Mode ?? "follow-gh-default", operation);
    }
}
