using System.Text;
using SkillView.Inventory.Models;

namespace SkillView.Ui;

/// Pure rendering over frozen evidence. Never inspect an operation's old paths.
internal static class OperationImpactFormatter
{
    internal static string Exposure(ObservedSkillExposure exposure)
    {
        var agent = exposure.Membership;
        return $"{agent.AgentId}: {agent.Path}; scope={agent.Scope?.ToString() ?? "unknown"}; "
            + $"profile={agent.Profile ?? "unknown"}; context={agent.Context ?? "unknown"}; "
            + $"discovery={agent.DiscoveryRoot ?? "unknown"}; agent version={agent.AgentVersion ?? "unknown"}";
    }

    internal static void Append(StringBuilder text, OperationImpact impact)
    {
        void Field(string label, string? value) => text.AppendLine($"**{label}:** {MarkdownTableFormatter.FormatCodeSpan(value ?? "unknown")}  ");
        Field("Installation address ID", impact.Subject.InstallationId);
        Field("Materialization", impact.Subject.Materialization);
        Field("Captured", impact.Subject.CapturedAt?.ToString("O"));
        Field("Native observation", impact.Subject.NativeObject.Status == "observed"
            ? impact.Subject.NativeObject.ObjectId + " (" + impact.Subject.NativeObject.Role + ")" : "unknown");
        Field("Exposure coverage", impact.ExposureCoverage);
        foreach (var distributor in impact.Subject.Distributors)
        {
            Field("Distributor", $"{distributor.Provider}: {distributor.Locator}; {distributor.Evidence}; version={distributor.Version ?? "unknown"}");
            Field("Delivery", distributor.DeliveryId);
        }
        foreach (var exposure in impact.AffectedExposures) Field("Affected exposure", Exposure(exposure));
        foreach (var exposure in impact.RetainedExposures) Field("Retained exposure", Exposure(exposure));
        if (impact.PayloadRetained)
        {
            Field("Retained payload", impact.RetainedPayloadPath ?? "location unknown; inspect operation");
            Field("Retention evidence", impact.RetentionEvidence);
        }
    }
}
