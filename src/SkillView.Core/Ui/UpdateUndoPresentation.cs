using SkillView.Gh.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal static class UpdateUndoPresentation
{
    internal static string Review(ReplacementResult plan) => Render("Undo this update?", Fields(plan),
        "Restore the exact retained original, including its local edits and extras. " +
        "Every consumer of this physical installation is affected. Changes since the update refuse undo. " +
        "Cancel leaves installed files unchanged. Agent reload state remains unknown.");

    internal static string Result(UpdateResult result) => Render(result.Cancelled ? "Undo canceled"
        : result.Succeeded ? "Original restored" : "Undo failed or refused", ResultFields(result),
        "Inspect the outcome and saved evidence in F2 Details. Refresh inventory before another review. Agent reload state remains unknown.");

    internal static string Refusal(ReplacementResult plan, string reason) => Render("Undo unavailable",
        new (string, string?)[] { ("Location", plan.OriginalPath), ("Reason", reason) },
        "No new undo approval is granted. Inspect saved evidence in F2 Details before recovery or a fresh undo inspection.");

    private static IEnumerable<(string, string?)> Fields(ReplacementResult plan, bool showState = true)
    {
        yield return ("Skill", plan.Subject.Name);
        yield return ("Location", plan.OriginalPath);
        var agents = plan.Subject.Agents.Select(agent => agent.AgentId).Distinct().Order().ToArray();
        yield return ("Affected consumers", agents.Length == 0 ? "none recorded; coverage may be incomplete" : string.Join(", ", agents));
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

    private static string Render(string heading, IEnumerable<(string, string?)> fields, string guidance)
    {
        var rendered = MaintenanceQueueFormatter.RenderFields(fields, CancellationToken.None, 6144);
        var compact = string.Join("\n", rendered.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(field => "- " + field));
        return "## " + heading + "\n\n" + compact + "\n\n" + guidance
            + (rendered.OmittedFields > 0 ? $"\n\n{rendered.OmittedFields} fields omitted; inspect F2 Details and saved evidence." : "");
    }
}
