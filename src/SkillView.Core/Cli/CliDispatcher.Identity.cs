using System.Text.Json;
using SkillView.Inventory.Models;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    private static void WriteImpactText(OperationImpact? impact)
    {
        if (impact is null) return;
        var text = new System.Text.StringBuilder();
        SkillView.Ui.OperationImpactFormatter.Append(text, impact);
        Console.Out.WriteLine(SkillView.Ui.TerminalEscapeSanitizer.Sanitize(text.ToString()));
    }

    private static void WriteImpact(Utf8JsonWriter w, OperationImpact? impact)
    {
        w.WritePropertyName("impact");
        if (impact is null) { w.WriteNullValue(); return; }
        w.WriteStartObject();
        w.WriteString("operation", impact.Operation);
        w.WriteString("targetPath", impact.TargetPath);
        w.WriteBoolean("payloadRetained", impact.PayloadRetained);
        w.WriteString("retainedPayloadPath", impact.RetainedPayloadPath);
        w.WriteString("retentionEvidence", impact.RetentionEvidence);
        w.WriteBoolean("exposureCoverageComplete", impact.ExposureCoverageComplete);
        w.WriteString("exposureCoverage", impact.ExposureCoverage);
        WriteSubject(w, impact.Subject);
        WriteExposures(w, "affectedExposures", impact.AffectedExposures);
        WriteExposures(w, "retainedExposures", impact.RetainedExposures);
        WriteNativeObservation(w, "executionEvidence", impact.ExecutionEvidence);
        w.WriteEndObject();
    }

    private static void WriteSubject(Utf8JsonWriter w, OperationSubject subject)
    {
        w.WriteStartObject("subject");
        w.WriteString("installationId", subject.InstallationId);
        w.WriteString("addressRole", subject.AddressRole);
        w.WriteString("materialization", subject.Materialization);
        w.WriteString("path", subject.Path);
        w.WriteString("name", subject.Name);
        w.WriteString("scope", subject.Scope?.ToString());
        if (subject.CapturedAt is { } at) w.WriteString("capturedAt", at); else w.WriteNull("capturedAt");
        w.WriteBoolean("exposureCoverageComplete", subject.ExposureCoverageComplete);
        w.WriteString("exposureCoverage", subject.ExposureCoverage);
        w.WriteStartObject("source");
        w.WriteString("id", subject.Source.Key);
        w.WriteString("provider", subject.Source.Provider);
        w.WriteString("origin", subject.Source.Origin);
        w.WriteString("subpath", subject.Source.Subpath);
        w.WriteString("evidence", subject.Source.Evidence);
        w.WriteBoolean("conflicted", subject.Source.Conflicted);
        w.WriteEndObject();
        w.WriteStartObject("revision");
        w.WriteString("authoredVersion", subject.Revision.AuthoredVersion);
        w.WriteString("repositoryRef", subject.Revision.RepositoryRef);
        w.WriteString("recordedTree", subject.Revision.RecordedTree);
        w.WriteNull("resolvedCommit"); // Only a source resolver can establish this.
        w.WriteString("pinnedRef", subject.Revision.PinnedRef);
        w.WriteString("payloadFingerprint", subject.Revision.PayloadFingerprint);
        w.WriteString("ownerRecordedHash", subject.Revision.OwnerRecordedHash);
        w.WriteString("ownerHashKind", subject.Revision.OwnerHashKind);
        w.WriteString("distributorVersion", subject.Revision.DistributorVersion);
        w.WriteEndObject();
        WriteDistributors(w, subject.Distributors);
        w.WriteStartArray("ownerRecords");
        foreach (var owner in subject.Owners) WriteOwnerRecord(w, owner);
        w.WriteEndArray();
        WriteNativeObservation(w, "nativeObject", subject.NativeObject);
        WriteExposures(w, "exposures", subject.Exposures);
        w.WriteEndObject();
    }

    private static void WriteDistributors(Utf8JsonWriter w, IEnumerable<SkillDistributorIdentity> distributors)
    {
        w.WriteStartArray("distributors");
        foreach (var distributor in distributors)
        {
            w.WriteStartObject();
            w.WriteString("id", distributor.Id);
            w.WriteString("provider", distributor.Provider);
            w.WriteString("locator", distributor.Locator);
            w.WriteString("evidence", distributor.Evidence);
            w.WriteBoolean("verifiedOrigin", false);
            w.WriteString("deliveryId", distributor.DeliveryId);
            w.WriteString("version", distributor.Version);
            w.WriteString("boundary", distributor.Boundary);
            w.WriteString("registryPath", distributor.RegistryPath);
            w.WriteString("context", distributor.Context);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteNativeObservation(Utf8JsonWriter w, string property, NativeObjectObservation? native)
    {
        w.WritePropertyName(property);
        if (native is null) { w.WriteNullValue(); return; }
        w.WriteStartObject();
        w.WriteString("status", native.Status); w.WriteString("role", native.Role);
        if (native.CapturedAt is { } at) w.WriteString("capturedAt", at); else w.WriteNull("capturedAt");
        w.WriteString("platform", native.Platform); w.WriteString("volume", native.Volume);
        w.WriteString("objectId", native.ObjectId); w.WriteString("canonicalPath", native.CanonicalPath);
        w.WriteString("changeTimeSeconds", native.ChangeTimeSeconds); w.WriteString("changeTimeNanoseconds", native.ChangeTimeNanoseconds);
        w.WriteString("windowsCreationTime", native.WindowsCreationTime); w.WriteString("windowsChangeTime", native.WindowsChangeTime);
        w.WriteString("diagnostic", native.Diagnostic);
        w.WriteEndObject();
    }

    private static void WriteExposures(Utf8JsonWriter w, string property, IEnumerable<ObservedSkillExposure> exposures)
    {
        w.WriteStartArray(property);
        foreach (var exposure in exposures)
        {
            var agent = exposure.Membership;
            w.WriteStartObject();
            w.WriteString("agent", agent.AgentId); w.WriteString("path", agent.Path);
            w.WriteString("entryAddress", exposure.EntryAddress); w.WriteBoolean("isSymlink", agent.IsSymlink);
            w.WriteString("scope", agent.Scope?.ToString()); w.WriteString("context", agent.Context);
            w.WriteString("profile", agent.Profile); w.WriteString("discoveryRoot", agent.DiscoveryRoot);
            w.WriteString("agentVersion", agent.AgentVersion);
            if (agent.Enabled is { } enabled) w.WriteBoolean("enabled", enabled); else w.WriteNull("enabled");
            w.WriteString("loadingRule", agent.LoadingRule); w.WriteString("activeSessionState", "unknown");
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
