using System.Text.Json;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Threading;

namespace SkillView.Maintenance;

internal sealed partial class SavedUpdateCoordinator
{
    internal static bool IsRevisionUpdate(ReplacementResult plan) => plan.Revision is not null
        && plan.PinPolicy is null && plan.LocalImport is null && plan.LocalSourceSwitch is null
        && (plan.Reference is null || plan.Reference.Purpose == "update-revision");

    internal static void RequireSameUndoPlan(ReplacementResult applied, ReplacementResult inspected)
    {
        if (!IsRevisionUpdate(applied) || !IsRevisionUpdate(inspected)
            || applied.Id != inspected.Id || applied.PlanFingerprint != inspected.PlanFingerprint
            || applied.Subject.InstallationId != inspected.Subject.InstallationId
            || !PathIdentity.Equals(applied.OriginalPath, inspected.OriginalPath))
            throw new IOException("The inspected update differs from the selected saved update. No undo was authorized.");
    }

    internal ReplacementResult InspectUndo(ReplacementResult applied, InventorySnapshot inventory, CancellationToken token)
    {
        if (!IsRevisionUpdate(applied) || !applied.Succeeded || applied.Canceled || applied.Status != "installed" || !applied.MutationObserved)
            throw new IOException("Undo here requires a successfully installed saved revision update.");
        var inspected = plans.Inspect(applied.Id, inventory, token, "update-revision");
        RequireSameUndoPlan(applied, inspected);
        if (inspected.Status != "installed")
            throw new IOException("This saved update is no longer installed unchanged. Inspect its saved evidence before recovery.");
        return inspected;
    }

    internal async Task<UpdateResult> UndoAsync(ReplacementResult review, InventorySnapshot inventory,
        Func<CancellationToken, Task<InventorySnapshot>> reconcile, CancellationToken token)
    {
        var result = Empty(false) with { SavedPlan = review };
        var attempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsRevisionUpdate(review) || !review.Succeeded || review.Canceled || review.Status != "installed")
                throw new IOException("Inspect this exact installed revision update before approving undo.");
            attempted = true;
            // The observation fingerprint, not the original apply approval,
            // binds the native locations and owner state the user just reviewed.
            var restored = plans.Recover(review.Id, review.ReviewFingerprint, inventory, undo: true, token, "update-revision");
            result = result with
            {
                SavedPlan = restored,
                Succeeded = restored.Succeeded && !restored.Canceled,
                ExitCode = restored.Canceled ? 130 : restored.Succeeded ? 0 : 2,
                Cancelled = restored.Canceled,
                ErrorMessage = restored.Succeeded ? null : restored.Message,
                Outcomes = [FromPlan(restored, restored.Succeeded ? restored.Status : "recovery-required", restored.Message)]
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = result with
            {
                Succeeded = false,
                ExitCode = 130,
                Cancelled = true,
                ErrorMessage = "Undo canceled. Inspect saved evidence before recovery or retry.",
                Outcomes = [FromPlan(review, attempted ? "recovery-required" : "canceled", "Undo canceled; no automatic retry was attempted.")]
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or EntryPointNotFoundException)
        {
            result = result with
            {
                Succeeded = false,
                ExitCode = 2,
                ErrorMessage = ex.Message,
                Outcomes = [FromPlan(review, "refused", ex.Message)]
            };
        }
        finally
        {
            if (attempted)
            {
                catalog.Invalidate();
                using var deadline = new CancellationSource(TimeSpan.FromSeconds(10),
                    CancellationCallbackReporter.For(logger, "saved update undo reconciliation"));
                try
                {
                    var snapshot = await reconcile(deadline.Token).ConfigureAwait(false);
                    if (snapshot.IsPartial || !snapshot.ConfiguredRootsComplete)
                        throw new IOException("Post-undo inventory is partial; saved evidence remains authoritative.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    result = result with
                    {
                        ReconciliationPartial = true,
                        Succeeded = false,
                        ExitCode = result.Cancelled ? 130 : 2,
                        ErrorMessage = $"{result.ErrorMessage} Post-undo inventory reconciliation failed: {ex.Message}".Trim(),
                        Notices = result.Notices.Add(new("reconciliation", ex.Message))
                    };
                    logger.Warn("update.undo.reconciliation", $"Plan {review.Id}: {ex.Message}");
                }
            }
        }
        if (token.IsCancellationRequested) result = result with { Cancelled = true, Succeeded = false, ExitCode = 130 };
        logger.Info("update.undo", $"Plan {review.Id}: {result.SavedPlan?.Status}; attempted={attempted}; exit {result.ExitCode}. Manifest: {review.PlanPath}");
        return result with { MutationAttempted = attempted };
    }
}
