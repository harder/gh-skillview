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
        if (disposition != "retain-original")
            return UpdateResult.Failure(false, 2, "Explicit retain-original approval is required.", []) with
            { SavedPlan = review, Outcomes = [FromPlan(review, "refused", "Explicit retain-original approval is required.")] };
        var result = Empty(false) with { SavedPlan = review };
        var attempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            attempted = true;
            var applied = plans.Apply(review.Id, review.PlanFingerprint, inventory, token);
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
            $"{message} Saved plan {plan.Id}; manifest: {plan.PlanPath}. Inspect with update-inspect --id {plan.Id} before recovery or undo.")
        { Impact = plan.Impact };
}
