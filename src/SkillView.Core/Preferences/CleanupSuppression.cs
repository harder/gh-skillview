using System.Collections.Immutable;
using System.Text.Json.Serialization;
using SkillView.Inventory;

namespace SkillView.Preferences;

/// Frozen output only; never added to persisted maintenance subjects/hash inputs.
public sealed record CleanupSuppression(bool LegacyMarker, bool External, string State,
    string? BindingMode = null, long? Sequence = null, string? Diagnostic = null)
{
    public bool Active => LegacyMarker || External;
}

internal sealed record SuppressionBinding(string Path, string Mode, string Source,
    string Owners, string? ObjectFingerprint, string? PayloadFingerprint);
internal sealed record SuppressionRecord(int SchemaVersion, long Sequence, string Action,
    string Key, SuppressionBinding Binding);
internal sealed record SuppressionEnvelope(byte[] Body, string Sha256);
internal sealed record SuppressionRead(ImmutableArray<SuppressionRecord> Records, string? Diagnostic);
internal sealed record SuppressionResult(bool Changed, string Path, CleanupSuppression Observation);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SuppressionEnvelope))]
[JsonSerializable(typeof(SuppressionRecord))]
[JsonSerializable(typeof(SuppressionBinding))]
[JsonSerializable(typeof(string[]))]
internal partial class SuppressionJsonContext : JsonSerializerContext;
