using SkillView.Gh.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal static class UpdateUndoPresentation
{
    internal static string Review(ReplacementResult plan) => Render("Undo this update?", Fields(plan),
        "Restore the exact retained original, including its local edits and extras. " +
        "Every consumer of this physical installation is affected. Changes since the update refuse undo. " +
        "Cancel leaves installed files unchanged. Agent reload state remains unknown.");

    internal static bool RestoredWithIncompleteInventory(UpdateResult result) => result.ReconciliationPartial
        && result.SavedPlan is { Succeeded: true, Canceled: false, Status: "restored" };

    internal static string Result(UpdateResult result) => Render(result.Cancelled ? "Undo canceled"
        : RestoredWithIncompleteInventory(result) ? "Original restored; verification incomplete"
        : result.ReconciliationPartial ? "Undo needs review; verification incomplete"
        : result.Succeeded ? "Original restored" : "Undo failed or refused", ResultFields(result),
        "Inspect the outcome and saved evidence in F2 Details. Refresh inventory before another review. Agent reload state remains unknown.");

    internal static string Refusal(ReplacementResult plan, string reason, bool executionAttempted = false) => Render(executionAttempted ? "Undo needs review" : "Undo unavailable",
        new (string, string?)[] { ("Location", plan.OriginalPath), ("Reason", reason) },
        executionAttempted ? "A write may have started. This approval is closed. Refresh inventory and inspect F2 Details before recovery."
        : "No new undo approval is granted. Inspect saved evidence in F2 Details before recovery or a fresh undo inspection.");

    internal static string FailureDetails(ReplacementResult plan, string reason) => Render("Undo failure details",
        new (string, string?)[] { ("Reason", reason), ("Saved ID", plan.Id), ("Manifest", plan.PlanPath) },
        "Inspect the current saved locations before recovery or a fresh undo inspection.")
        + "\n\n## Captured saved evidence\n\n" + UpdateScreen.RenderPlan(plan);

    private static IEnumerable<(string, string?)> Fields(ReplacementResult plan, bool showState = true)
    {
        yield return ("Skill", plan.Subject.Name);
        yield return ("Location", plan.OriginalPath);
        var agents = plan.Subject.Agents.Select(agent => agent.AgentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        yield return ("Recorded consumers", agents.Length == 0 ? "none recorded; coverage may be incomplete" : string.Join(", ", agents));
        yield return ("Restore ref", plan.Subject.Metadata.GithubRef);
        yield return ("Restore authored version", plan.Subject.Metadata.Version);
        if (showState) yield return ("Current disk state", plan.Status);
    }

    private static IEnumerable<(string, string?)> ResultFields(UpdateResult result)
    {
        yield return ("Outcome", result.Outcomes.LastOrDefault()?.Status
            ?? (result.Cancelled ? "canceled" : !result.Succeeded ? "failed or refused" : result.SavedPlan?.Status ?? "unavailable; inspect details"));
        if (result.ErrorMessage is not null) yield return ("Reason", result.ErrorMessage);
        if (result.ReconciliationPartial) yield return ("Inventory", "Verification incomplete; inspect details and refresh inventory.");
        if (result.SavedPlan is { } plan)
            foreach (var field in Fields(plan, result.Succeeded && !result.Cancelled)) yield return field;
        foreach (var outcome in result.Outcomes.Take(8)) yield return ("Notice", outcome.Message);
    }

    private static string Render(string heading, IEnumerable<(string, string?)> fields, string guidance) =>
        UpdateReviewPresentation.Render(heading, fields, guidance, 6144);
}
