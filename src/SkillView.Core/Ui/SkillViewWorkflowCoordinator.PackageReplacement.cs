using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal sealed partial class SkillViewWorkflowCoordinator
{
    internal async Task<UpdateResult> PrepareResourceReplacementAsync(PackageCheckFact fact, string target, string ghPath,
        GhSkillUpdateService.Options options, CancellationToken token)
    {
        // The activation froze A before navigation. Its own preparation may
        // invalidate G; fresh admission replaces old-A freshness from here on.
        var inventory = await CaptureUpdateInventoryAsync(ghPath, options.AllowHiddenDirectories, token).ConfigureAwait(false);
        var copies = inventory.Skills.Where(s => s.InstallationId == fact.Request.Id && s.ResolvedPath == fact.Request.Target.ResolvedPath).ToArray();
        if (fact.Request.Context != InstallNavigationEvidence.Context(_services.Runner) || PackageResourceReplacement.Target(fact) != target
            || options.Unpin || options.All || options.DryRun || options.Targets is not { Count: 1 }
            || options.Targets[0].InstallationId != fact.Request.Id || options.Targets[0].ResolvedPath != fact.Request.Target.ResolvedPath
            || copies.Length != 1 || !PackageResourceReplacement.FreshCopy(fact, inventory, copies[0]))
            return UpdateResult.Failure(false, 2, "The checked physical copy, source, context or inventory changed; Refresh and Check package again.", []);
        var result = await Task.Run(() => _services.SavedUpdates.PrepareAsync(ghPath, options with { Targets = [copies[0]] }, inventory, token), token).ConfigureAwait(false);
        if (fact.Request.Context != InstallNavigationEvidence.Context(_services.Runner))
            throw new IOException("The captured child context changed during preparation; reopen ordinary saved review or Refresh and Check package again.");
        return result;
    }

    internal async Task<string> CheckResourceReplacementAsync(PackageCheckFact fact, string target, ReplacementResult applied, CancellationToken token)
    {
        if (fact.Request.Context != InstallNavigationEvidence.Context(_services.Runner)) return PackageResourceReplacement.Incomplete;
        var note = await Task.Run(() => PackageResourceReplacement.CheckAppliedAsync(fact.Request.Target, target, applied, token), token).ConfigureAwait(false);
        return fact.Request.Context == InstallNavigationEvidence.Context(_services.Runner) ? note : PackageResourceReplacement.Incomplete;
    }
}
