using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Threading;

namespace SkillView.Maintenance;

/// Product update admission. Availability is disposable; preparation retains one
/// exact candidate; applying never resolves the remote source again.
internal sealed class SavedUpdateCoordinator(ReplacementPlanStore plans,
    SkillUpdateCoordinator availability, GhSkillListAdapter catalog, Logger logger)
{
    internal Task<UpdateResult> PrepareLocalReimportAsync(string ghPath, InstalledSkill target,
        InventorySnapshot inventory, CancellationToken token) => PrepareLocalImportAsync(ghPath, target, null, inventory, token);

    internal Task<UpdateResult> PrepareLocalSourceSwitchAsync(string ghPath, InstalledSkill target, string newSourcePath,
        InventorySnapshot inventory, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newSourcePath);
        return PrepareLocalImportAsync(ghPath, target, newSourcePath, inventory, token);
    }

    private async Task<UpdateResult> PrepareLocalImportAsync(string ghPath, InstalledSkill target, string? newSourcePath,
        InventorySnapshot inventory, CancellationToken token)
    {
        try
        {
            var preparation = newSourcePath is null
                ? await plans.PlanLocalReimportAsync(ghPath, target, inventory, token).ConfigureAwait(false)
                : await plans.PlanLocalSourceSwitchAsync(ghPath, target, newSourcePath, inventory, token).ConfigureAwait(false);
            return Empty(false) with
            {
                SavedPlan = preparation.Plan,
                Succeeded = preparation.Unchanged?.CleanupWarning is null,
                ExitCode = preparation.Unchanged?.CleanupWarning is null ? 0 : 2,
                ErrorMessage = preparation.Unchanged?.CleanupWarning,
                Outcomes = [preparation.Plan is { } plan ? FromPlan(plan, "review-required", plan.Message)
                    : Outcome(target, "unchanged", preparation.Unchanged!.Message
                        + (preparation.Unchanged.LocalSource is { } local ? $" Local source {local.Status}: {local.Message}" : "")
                        + (preparation.Unchanged.CleanupWarning is { } warning ? " " + warning : ""))]
            };
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            logger.Warn("reimport", ex.ToString());
            return Empty(false) with
            {
                Succeeded = false,
                Cancelled = true,
                ExitCode = 130,
                ErrorMessage = ex.Message,
                Outcomes = [Outcome(target, "canceled", ex.Message)]
            };
        }
        catch (GhSourceRequestException ex)
        {
            logger.Warn("reimport", ex.ToString());
            return Empty(false) with
            {
                Succeeded = false,
                ExitCode = 2,
                ErrorMessage = ex.Message,
                StdOut = ex.StdOut,
                StdErr = ex.StdErr,
                Outcomes = [Outcome(target, "refused", ex.Message)]
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException or EntryPointNotFoundException)
        {
            logger.Warn("reimport", ex.ToString());
            return Empty(false) with
            {
                Succeeded = false,
                ExitCode = 2,
                ErrorMessage = ex.Message,
                Outcomes = [Outcome(target, "refused", ex.Message)]
            };
        }
    }

    internal UpdateResult PrepareUnpin(InstalledSkill target, InventorySnapshot inventory, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var preparation = plans.PlanUnpin(target, inventory, token);
            return Empty(false) with
            {
                SavedPlan = preparation.Plan,
                Outcomes = [preparation.Plan is { } plan
                    ? FromPlan(plan, "review-required", "Pin-only candidate saved. Review all affected consumers and approve retention before applying.")
                    : Outcome(target, "unchanged", preparation.Unchanged!.Message)]
            };
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            // The owning view may disappear before queued completion. Keep the
            // exact retained evidence address in the application log as well.
            logger.Warn("unpin", ex.ToString());
            return Empty(false) with
            {
                Succeeded = false,
                Cancelled = true,
                ExitCode = 130,
                ErrorMessage = ex.Message,
                Outcomes = [Outcome(target, "canceled", ex.Message)]
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or EntryPointNotFoundException)
        {
            return Empty(false) with
            {
                Succeeded = false,
                ExitCode = 2,
                ErrorMessage = ex.Message,
                Outcomes = [Outcome(target, "refused", ex.Message)]
            };
        }
    }

    internal async Task<UpdateResult> PrepareAsync(string ghPath, GhSkillUpdateService.Options options,
        InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (options.Targets is not { Count: > 0 and <= SkillUpdateCoordinator.MaxTargets })
            return UpdateResult.Failure(options.DryRun, 2, $"Select 1–{SkillUpdateCoordinator.MaxTargets} exact installations.", []);
        if (inventory.IsPartial || !inventory.ConfiguredRootsComplete)
            return UpdateResult.Failure(options.DryRun, 2,
                "Update review requires complete inventory. Resolve provider/root diagnostics; include hidden directories when needed.", []) with
            {
                Outcomes = options.Targets.DistinctBy(target => target.InstallationId).Select(target => Outcome(target,
                    "refused", "Inventory coverage is incomplete; no candidate was staged.")).ToImmutableArray()
            };
        if (options.DryRun)
            return await availability.UpdateAsync(ghPath, options, token).ConfigureAwait(false);

        var targets = options.Targets.DistinctBy(target => target.InstallationId).ToArray();
        var outcomes = ImmutableArray.CreateBuilder<InstallationUpdateOutcome>();
        var diagnostics = new UpdateResultAccumulator();
        ReplacementResult? prepared = null;
        var canceled = false;
        var failed = false;
        foreach (var target in targets)
        {
            string status; string message;
            if (token.IsCancellationRequested)
            { canceled = true; status = "not-started"; message = "Canceled before preparation; installed payload unchanged."; }
            else if (prepared is not null)
            {
                status = "not-started";
                message = "Prepare this exact installation after applying or skipping the current review; sibling directory generations may change.";
            }
            else if (target.Pinned && !options.Unpin)
            { status = "pinned"; message = "Exact pin retained. Select unpin explicitly to review its removal."; }
            else if (target.FrontMatter.LocalPath is not null || string.IsNullOrWhiteSpace(target.FrontMatter.Upstream)
                || string.IsNullOrWhiteSpace(target.TreeSha) || string.IsNullOrWhiteSpace(target.FrontMatter.GithubRef))
            { status = "untracked"; message = "Verified remote provenance is required; use source adoption or local reimport."; }
            else
            {
                try
                {
                    var preparation = await plans.PlanLatestAsync(ghPath, target, inventory, options.Unpin, token).ConfigureAwait(false);
                    prepared = preparation.Plan;
                    if (preparation.Unchanged is { } current)
                    {
                        status = "unchanged";
                        message = current.Message;
                        if (current.CleanupWarning is { } warning) { failed = true; message += " " + warning; }
                    }
                    else
                    {
                        status = "review-required";
                        message = "Exact candidate saved; installed payload unchanged. Review the manifest and explicitly retain the original before apply.";
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { canceled = true; status = "canceled"; message = "Preparation canceled; installed payload unchanged. Private staging evidence may remain."; }
                catch (GhSourceRequestException ex)
                {
                    failed = true; status = "refused"; message = ex.Message;
                    diagnostics.Append(UpdateResult.Failure(false, 2, ex.StdErr, [], ex.StdOut));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or EntryPointNotFoundException)
                { failed = true; status = "refused"; message = ex.Message; }
            }
            outcomes.Add(Outcome(target, status, message));
        }
        return diagnostics.Append(Empty(false) with
        {
            Succeeded = !failed && !canceled,
            ExitCode = canceled ? 130 : failed ? 2 : 0,
            Cancelled = canceled,
            ErrorMessage = failed ? "Some installations could not be prepared; inspect independent outcomes." : canceled ? "Preparation canceled." : null,
            Outcomes = outcomes.ToImmutable(),
            SavedPlan = prepared,
            Notices = options.Force ? [new("review", "--force does not approve replacement or local-change disposition. Apply the saved plan with explicit retain-original approval.")] : []
        });
    }

    internal async Task<UpdateResult> ApplyAsync(ReplacementResult review, string disposition,
        InventorySnapshot inventory, Func<CancellationToken, Task<InventorySnapshot>> reconcile, CancellationToken token)
    {
        var requiredDisposition = review.Approval is not null ? ReplacementPlanStore.ApprovedRestoreDisposition : review.Selection is not null ? ReplacementPlanStore.SelectedRevisionDisposition : review.LocalSourceSwitch is not null ? ReplacementPlanStore.LocalSourceSwitchDisposition : review.LocalImport is not null ? ReplacementPlanStore.LocalReimportDisposition
            : review.PinPolicy is not null ? "unpin-only" : review.LocalDisposition is null ? "retain-original" : "merge-reviewed";
        if (disposition != requiredDisposition)
            return UpdateResult.Failure(false, 2, "Explicit " + requiredDisposition + " approval is required.", []) with
            { SavedPlan = review, Outcomes = [FromPlan(review, "refused", "The saved local-change disposition needs explicit approval.")] };
        var purpose = review.Approval is not null ? ReplacementPlanStore.ApprovedRestorePurpose
            : review.Selection is not null ? ReplacementPlanStore.SelectedRevisionPurpose
            : review.LocalSourceSwitch is not null ? ReplacementPlanStore.LocalSourceSwitchPurpose
            : review.LocalImport is not null ? ReplacementPlanStore.LocalReimportPurpose : review.PinPolicy is not null ? "unpin-only" : null;
        var result = Empty(false) with { SavedPlan = review };
        var attempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (review.SourceReview is { } source && review.AcknowledgeSourceReview)
                plans.AcknowledgeSourceReview(review.Id, review.PlanFingerprint, source.Fingerprint, inventory, token, purpose);
            attempted = true;
            var applied = plans.Apply(review.Id, review.PlanFingerprint, inventory, token, disposition, purpose);
            if (applied.Status == "unchanged" && !applied.MutationObserved) attempted = false;
            result = result with
            {
                SavedPlan = applied,
                Succeeded = applied.Succeeded && !applied.Canceled,
                ExitCode = applied.Canceled ? 130 : applied.Succeeded ? 0 : 2,
                Cancelled = applied.Canceled,
                ErrorMessage = applied.Succeeded ? null : applied.Message,
                Outcomes = [FromPlan(applied, applied.Succeeded ? applied.Status == "unchanged" ? "unchanged" : "changed" : "recovery-required", applied.Message)]
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = result with
            {
                Succeeded = false,
                ExitCode = 130,
                Cancelled = true,
                ErrorMessage = "Apply canceled. Inspect the saved plan before recovery or retry.",
                Outcomes = [FromPlan(review, attempted ? "recovery-required" : "canceled", "Inspect the saved plan; no automatic retry was attempted.")]
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or EntryPointNotFoundException)
        {
            result = result with
            {
                Succeeded = false,
                ExitCode = 2,
                ErrorMessage = ex.Message,
                Outcomes = [FromPlan(review, "refused", ex.Message + " Inspect the saved plan before retrying.")]
            };
        }
        finally
        {
            // Admission to Apply is conservative: validation may refuse without
            // moving. Invalidation/reconciliation must survive view cancellation.
            if (attempted)
            {
                catalog.Invalidate();
                using var deadline = new CancellationSource(TimeSpan.FromSeconds(10),
                    CancellationCallbackReporter.For(logger, "saved update reconciliation"));
                try
                {
                    var snapshot = await reconcile(deadline.Token).ConfigureAwait(false);
                    if (snapshot.IsPartial || !snapshot.ConfiguredRootsComplete)
                        throw new IOException("Post-attempt inventory is partial; saved per-target evidence is retained.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    result = result with
                    {
                        ReconciliationPartial = true,
                        Succeeded = false,
                        ExitCode = result.Cancelled ? 130 : 2,
                        ErrorMessage = $"{result.ErrorMessage} Post-attempt inventory reconciliation failed: {ex.Message}".Trim(),
                        Notices = result.Notices.Add(new("reconciliation", ex.Message))
                    };
                    logger.Warn("update.reconciliation", $"Plan {review.Id}: {ex.Message}");
                }
            }
        }
        if (token.IsCancellationRequested)
            result = result with { Cancelled = true, Succeeded = false, ExitCode = 130 };
        logger.Info("update.apply", $"Plan {review.Id}: {result.SavedPlan?.Status}; exit {result.ExitCode}; attempted={attempted}; reconciliationPartial={result.ReconciliationPartial}. Manifest: {review.PlanPath}");
        return result with { MutationAttempted = attempted };
    }

    private static UpdateResult Empty(bool dryRun) => new()
    {
        DryRun = dryRun,
        Succeeded = true,
        ExitCode = 0,
        StdOut = "",
        StdErr = "",
        ErrorMessage = null,
        CommandLine = [],
        Entries = []
    };

    private static InstallationUpdateOutcome Outcome(InstalledSkill target, string status, string message) =>
        new(target.InstallationId, target.ResolvedPath, status, null, null, [], 0,
            target.Agents.Select(agent => $"{agent.AgentId}: {agent.Path}").ToImmutableArray(), message)
        {
            Impact = OperationImpact.ObservedRetention(OperationSubject.From(target), "update-prepare")
        };

    private static InstallationUpdateOutcome FromPlan(ReplacementResult plan, string status, string message) =>
        new(plan.Subject.InstallationId, plan.OriginalPath, status, null, null,
            status == "unchanged" ? [] : plan.ChangedPaths.Take(128).ToImmutableArray(), status == "unchanged" ? 0 : plan.ChangedPaths.Length,
            plan.Subject.Agents.Select(agent => $"{agent.AgentId}: {agent.Path}").ToImmutableArray(),
            $"{message} Saved plan {plan.Id}; manifest: {plan.PlanPath}. Inspect with {(plan.Approval is not null ? "approved-inspect" : plan.Selection is not null ? "selected-inspect" : plan.LocalSourceSwitch is not null ? "source-switch-inspect" : plan.LocalImport is not null ? "reimport-inspect" : plan.PinPolicy is not null ? "unpin-inspect" : "update-inspect")} --id {plan.Id} before recovery or undo.")
        { Impact = plan.Impact };
}
