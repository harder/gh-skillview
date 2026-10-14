using System.Text.Json.Serialization;
using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Inventory;

namespace SkillView.Maintenance;

internal sealed record QuarantineRecord(
    int SchemaVersion, string Id, string Status, DateTimeOffset CreatedAt,
    string InstallationId, string Name, string OriginalPath, string RootPath,
    string PayloadFingerprint, string ObjectFingerprint, string ContextFingerprint,
    string ReviewFingerprint, long PayloadBytes, int EntryCount, int? RootUnixMode, ImmutableArray<AgentMembership> Agents,
    SecureFileIdentity OriginalRoot, SecureFileIdentity OriginalParent, SecureFileIdentity OriginalTarget,
    SecureFileIdentity? RootAfter = null, SecureFileIdentity? ParentAfter = null,
    SecureFileIdentity? TargetAfter = null, string? ObjectFingerprintAfter = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true)]
[JsonSerializable(typeof(QuarantineRecord))]
internal partial class QuarantineJsonContext : JsonSerializerContext;

internal sealed record QuarantineResult(string Id, string Status, bool Succeeded, bool MutationObserved,
    bool Canceled, string OriginalPath, string RetainedPath, string? ReviewFingerprint,
    string Message);
