using System.Collections.Immutable;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed record ReplacementUnchanged(string InstallationId, string Path,
    string PayloadFingerprint, string Message, string? CleanupWarning,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementLocalSourceObservation? LocalSource = null);

internal sealed record ReplacementPreparation
{
    internal ReplacementResult? Plan { get; }
    internal ReplacementUnchanged? Unchanged { get; }
    private ReplacementPreparation(ReplacementResult? plan, ReplacementUnchanged? unchanged)
    { Plan = plan; Unchanged = unchanged; }
    internal static ReplacementPreparation Saved(ReplacementResult plan) => new(plan, null);
    internal static ReplacementPreparation Current(ReplacementUnchanged unchanged) => new(null, unchanged);
}

internal sealed record ReplacementEvidence(string Payload, string Objects, int? Mode,
    SecureFileIdentity Identity, ImmutableArray<PayloadEntry> Entries,
    ImmutableArray<PayloadObjectEvidence> Children)
{
    internal static ReplacementEvidence From(PayloadSnapshot snapshot) => new(snapshot.Fingerprint,
        snapshot.ObjectStateFingerprint!, snapshot.RootUnixMode, snapshot.RootIdentity!.Value, snapshot.Entries, snapshot.Objects);
}

internal sealed record ReplacementSubject(string InstallationId, string Name, Scope Scope,
    ImmutableArray<AgentMembership> Agents, SkillFrontMatter Metadata,
    ImmutableArray<SkillPackage> Owners);

internal sealed record ReplacementRevision(ResolvedSkillRevision Source, SkillFrontMatter DesiredMetadata,
    ReplacementEvidence? RecordedBase, ImmutableArray<string> LocalChanges,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementOwnerTransition? Owner = null);

internal sealed record ReplacementOwnerTransition(string RegistryPath, string Selector, SecureFileIdentity Identity,
    byte[] BeforeEntry, byte[] DesiredEntry, string BeforeFingerprint, string DesiredFingerprint, DateTimeOffset UpdatedAt,
    SecureFileIdentity ParentIdentity);

internal sealed record ReplacementOwnerObservation(string State, string RegistryPath, string Selector,
    string Hash, int Bytes, SecureFileIdentity Identity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementOwnerRepair? Repair = null);

internal sealed record ReplacementOwnerRepair(string WriteId, string Direction, string ImageHash, int Bytes, string EntryState);

internal sealed record ReplacementPlan(int SchemaVersion, string Id, string Purpose, DateTimeOffset CreatedAt,
    string OriginalPath, string RootPath, SecureFileIdentity RootIdentity, SecureFileIdentity ParentIdentity,
    ReplacementSubject Subject, string ContextFingerprint, ReplacementEvidence Original,
    ReplacementEvidence Candidate, ImmutableArray<string> ChangedPaths, string ReviewFingerprint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementRevision? Revision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementLocalDisposition? LocalDisposition = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementPinPolicy? PinPolicy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementLocalImport? LocalImport = null);

internal sealed record ReplacementSlot(string Path, string Payload, string Objects, int? Mode, SecureFileIdentity Identity);

internal sealed record ReplacementObservation(string Topology, ReplacementSlot? Original, ReplacementSlot? Retained,
    ReplacementSlot? Candidate, ReplacementSlot? Displaced, SecureFileIdentity Root, SecureFileIdentity Parent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementOwnerObservation? Owner = null);

internal sealed record ReplacementStep(int Sequence, string PlanFingerprint, string Status, ReplacementObservation Observation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerWriteId = null);

internal sealed record ReplacementResult(string Id, string Status, bool Succeeded, bool MutationObserved,
    bool Canceled, string OriginalPath, string RetainedPath, string CandidatePath, string DisplacedPath,
    string PlanFingerprint, string ReviewFingerprint, ImmutableArray<string> ChangedPaths, string Message, ReplacementSubject Subject, string PlanPath, int? OriginalRootMode, int? CandidateRootMode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementRevision? Revision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementOwnerObservation? Owner = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementLocalDisposition? LocalDisposition = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementPinPolicy? PinPolicy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementLocalImport? LocalImport = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReplacementLocalSourceObservation? LocalSource { get; init; }
    [JsonIgnore]
    internal OperationImpact? Impact { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ReplacementResult))]
[JsonSerializable(typeof(ReplacementUnchanged))]
[JsonSerializable(typeof(ReplacementPlan))]
[JsonSerializable(typeof(ReplacementStep))]
[JsonSerializable(typeof(ReplacementObservation))]
[JsonSerializable(typeof(ReplacementSubject))]
internal partial class ReplacementJsonContext : JsonSerializerContext;
