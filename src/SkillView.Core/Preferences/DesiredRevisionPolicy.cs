using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SkillView.Preferences;

public sealed record DesiredRevisionPolicy(string Mode, string? RequestedRef = null,
    string? ApprovalId = null, string? ApprovalFingerprint = null);

public sealed record DesiredPolicyObservation(string Status, string Reason,
    DesiredRevisionPolicy? Policy, string? Fingerprint, long? Sequence = null)
{
    public bool Known => Status is "absent" or "active";
}

internal sealed record DesiredPolicyBinding(string InstallationId, string Path, string Source, string Owners);
internal sealed record DesiredPolicyRecord(int SchemaVersion, long Sequence, string Action,
    DesiredPolicyBinding Binding, DesiredRevisionPolicy? Policy);
internal sealed record DesiredPolicyEnvelope(byte[] Body, string Sha256);
internal sealed record DesiredPolicyRead(ImmutableArray<DesiredPolicyRecord> Records, string? Diagnostic);
internal sealed record DesiredPolicyWitness(int SchemaVersion, string StoreAddress, string BindingFingerprint, string PolicyFingerprint,
    string Mode, string Operation);
internal sealed record DesiredPolicyReceipt(int SchemaVersion, string PlanFingerprint, DesiredPolicyWitness Witness);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(DesiredRevisionPolicy))]
[JsonSerializable(typeof(DesiredPolicyRecord))]
[JsonSerializable(typeof(DesiredPolicyEnvelope))]
[JsonSerializable(typeof(DesiredPolicyBinding))]
[JsonSerializable(typeof(DesiredPolicyReceipt))]
[JsonSerializable(typeof(DesiredPolicyWitness))]
internal partial class DesiredPolicyJsonContext : JsonSerializerContext;
