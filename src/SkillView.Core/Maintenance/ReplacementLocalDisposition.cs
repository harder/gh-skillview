using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SkillView.Maintenance;

internal sealed record LocalReviewHeader(int SchemaVersion, string PlanFingerprint,
    string RecordedManifest, string InstalledManifest, string SourceManifest);
internal sealed record LocalReviewSelection(int SchemaVersion, string HeaderFingerprint,
    ImmutableArray<LocalPathChoice> Choices);
internal sealed record ReplacementLocalDisposition(string Kind, string SourcePlanFingerprint,
    string HeaderFingerprint, string SelectionFingerprint, ReplacementEvidence SourceCandidate,
    string MetadataPolicy, string MergedSlot, string? ResultAuthoredVersion);
internal sealed record LocalUpdateReview(string Id, string PlanFingerprint, string HeaderFingerprint,
    string ReviewFingerprint, string ReviewPath, ThreeWayPayloadReview Review,
    ImmutableArray<string> MetadataRefusals, bool CanMaterialize, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LocalReviewHeader))]
[JsonSerializable(typeof(LocalReviewSelection))]
[JsonSerializable(typeof(LocalUpdateReview))]
internal partial class LocalReviewJsonContext : JsonSerializerContext;
