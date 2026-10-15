using System.Collections.Immutable;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

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

internal sealed record ReplacementPlan(int SchemaVersion, string Id, string Purpose, DateTimeOffset CreatedAt,
    string OriginalPath, string RootPath, SecureFileIdentity RootIdentity, SecureFileIdentity ParentIdentity,
    ReplacementSubject Subject, string ContextFingerprint, ReplacementEvidence Original,
    ReplacementEvidence Candidate, ImmutableArray<string> ChangedPaths, string ReviewFingerprint);

internal sealed record ReplacementSlot(string Path, string Payload, string Objects, int? Mode, SecureFileIdentity Identity);

internal sealed record ReplacementObservation(string Topology, ReplacementSlot? Original, ReplacementSlot? Retained,
    ReplacementSlot? Candidate, ReplacementSlot? Displaced, SecureFileIdentity Root, SecureFileIdentity Parent);

internal sealed record ReplacementStep(int Sequence, string PlanFingerprint, string Status, ReplacementObservation Observation);

internal sealed record ReplacementResult(string Id, string Status, bool Succeeded, bool MutationObserved,
    bool Canceled, string OriginalPath, string RetainedPath, string CandidatePath, string DisplacedPath,
    string PlanFingerprint, string ReviewFingerprint, ImmutableArray<string> ChangedPaths, string Message, ReplacementSubject Subject, string PlanPath, int? OriginalRootMode, int? CandidateRootMode);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ReplacementResult))]
[JsonSerializable(typeof(ReplacementPlan))]
[JsonSerializable(typeof(ReplacementStep))]
[JsonSerializable(typeof(ReplacementObservation))]
[JsonSerializable(typeof(ReplacementSubject))]
internal partial class ReplacementJsonContext : JsonSerializerContext;
