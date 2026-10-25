using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SkillView.Maintenance;

internal sealed record LocalReviewHeader(int SchemaVersion, string PlanFingerprint,
    string RecordedManifest, string InstalledManifest, string SourceManifest);
internal sealed record LocalReviewSelection(int SchemaVersion, string HeaderFingerprint,
    ImmutableArray<LocalPathChoice> Choices,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LocalResolutionSet? Resolutions = null);
internal sealed record ReplacementLocalDisposition(string Kind, string SourcePlanFingerprint,
    string HeaderFingerprint, string SelectionFingerprint, ReplacementEvidence SourceCandidate,
    string MetadataPolicy, string MergedSlot, string? ResultAuthoredVersion);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LocalReviewHeader))]
[JsonSerializable(typeof(LocalReviewSelection))]
internal partial class LocalReviewJsonContext : JsonSerializerContext;
