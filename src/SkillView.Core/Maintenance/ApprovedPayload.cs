using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.Preferences;

namespace SkillView.Maintenance;

internal sealed record ApprovedPayloadReference(string StoreAddress, string Id, string Fingerprint);

// This is an observed selected result, not a claim inferred from tracking.
internal sealed record ApprovedSelectedResult(string PlanStoreAddress, string PlanId, string PlanFingerprint,
    GhPinnedRevisionWitness Provider, string InstalledPayload);

internal sealed record ApprovedOwnerCapture(string RegistryPath, string Selector, SecureFileIdentity Identity,
    SecureFileIdentity ParentIdentity, byte[] Entry, string EntryFingerprint);

internal sealed record ApprovedPayloadHeader(int SchemaVersion, string Id, DateTimeOffset CreatedAt,
    string Kind, string ContextFingerprint, DesiredPolicyBinding Binding, ReplacementSubject Subject,
    ReplacementEvidence Captured, string ArchiveFingerprint, string MetadataPolicy,
    ImmutableArray<PayloadMetadataRecipe> Metadata, string SourceReviewContract, string Fingerprint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApprovedSelectedResult? Selected = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApprovedOwnerCapture? OwnerCapture = null);

internal sealed record ApprovedPayloadSummary(ApprovedPayloadReference Reference, string Kind,
    string Path, string Name, string PayloadFingerprint, string MetadataPolicy, string Provenance);

internal sealed record ApprovedPayloadInventory(ImmutableArray<ApprovedPayloadSummary> Captures, string? Diagnostic);

internal sealed record ReplacementApproval(ApprovedPayloadReference Reference, ApprovedPayloadHeader Header,
    string ContextFingerprint, ReplacementOwnerTransition? Owner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ApprovedPayloadHeader))]
[JsonSerializable(typeof(ApprovedPayloadSummary))]
[JsonSerializable(typeof(ApprovedPayloadReference))]
[JsonSerializable(typeof(ReplacementApproval))]
[JsonSerializable(typeof(ImmutableArray<PayloadMetadataRecipe>))]
internal partial class ApprovedPayloadJsonContext : JsonSerializerContext
{
    internal static byte[] Canonical<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, type));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) ReplacementPlanStore.WriteCanonical(document.RootElement, writer);
        return bytes.ToArray();
    }
}
