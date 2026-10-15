using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Loading;

/// Contextual loading evidence is separate from physical installation identity.
/// All addresses/keys are captured off the UI thread; rendering performs no I/O.
internal sealed record SkillLoadingContext(
    string AgentId, string? Profile, string ProjectDirectory, string WorkingDirectory,
    string HomeDirectory, string ConfigDirectory, string? ClientVersion, string? AgentName,
    string ProjectTrust, string ConfigurationFingerprint, string RuleRevision,
    string ContextId)
{
    internal static SkillLoadingContext Capture(string agentId, string? profile,
        string projectDirectory, string workingDirectory, string homeDirectory,
        string configDirectory, string? clientVersion, string? agentName,
        string projectTrust, string configurationFingerprint, string ruleRevision)
    {
        var agent = AgentIdentity.Normalize(agentId);
        var project = PathIdentity.Normalize(projectDirectory);
        var cwd = PathIdentity.Normalize(workingDirectory);
        var home = PathIdentity.Normalize(homeDirectory);
        var config = PathIdentity.Normalize(configDirectory);
        return new(agent, profile, project, cwd, home, config, clientVersion, agentName,
            projectTrust, configurationFingerprint, ruleRevision,
            SkillDistributorIdentity.Digest(agent, profile, PathIdentity.NormalizeKey(project),
                PathIdentity.NormalizeKey(cwd), PathIdentity.NormalizeKey(home), PathIdentity.NormalizeKey(config),
                clientVersion, agentName, projectTrust, configurationFingerprint, ruleRevision));
    }
}

internal sealed record LoadingRuleSource(string Url, string Revision, string Evidence);

/// Documentation snapshots, source correlation and actual native conformance
/// are independent claims. Test fixture version labels cannot establish support.
internal sealed record AgentLoadingRuleSet(string AgentId, string Revision,
    ImmutableArray<string> SourceCorrelatedVersions, string NativeConformance,
    ImmutableArray<LoadingRuleSource> Sources)
{
    internal string Applicability(string? clientVersion) => string.IsNullOrWhiteSpace(clientVersion)
        ? "client-version-unknown"
        : SourceCorrelatedVersions.Contains(clientVersion, StringComparer.Ordinal)
            ? "source-correlated" : "client-version-unverified";
}

internal sealed record LoadingFileEvidence(string Path, string State, string? Sha256,
    string Purpose, string? Diagnostic);

/// Each dimension describes filesystem/configuration expectation, never an
/// active session. Unknown/incomplete evidence cannot silently become Enabled.
internal sealed record ExpectedSkillLoading(string State, string Discoverability,
    string ManualInvocation, string AutomaticInvocation, string Approval,
    string Selection, string Compatibility, string Reload,
    ImmutableArray<string> Reasons)
{
    internal string SlashInvocation { get; init; } = "unknown";
    internal static ExpectedSkillLoading Unknown(string reason) => new("unknown", "unknown",
        "unknown", "unknown", "unknown", "unknown", "unknown", "unknown", [reason]);
}

internal sealed record NativeSkillEntry(string Path, string? Name, bool? Enabled,
    string? InvocationName, string? Diagnostic);

/// A native list may cover only part of a context. Missing entries never erase
/// filesystem candidates or prove absence; current sessions remain independent.
internal sealed record NativeLoadingObservation(string Provider, string State,
    string ContextId, string? ClientVersion, string Schema, DateTimeOffset? CapturedAt,
    string Coverage, ImmutableArray<NativeSkillEntry> Entries,
    ImmutableArray<string> Diagnostics)
{
    internal static NativeLoadingObservation Unavailable(SkillLoadingContext context, string reason) =>
        new(context.AgentId, "unavailable", context.ContextId, context.ClientVersion,
            "none", null, "not-observed", [], [reason]);
}

internal sealed record EffectiveSkillCandidate(string LoadingKey, OperationSubject Subject,
    ImmutableArray<ObservedSkillExposure> Exposures, string? InvocationName,
    string Placement, ExpectedSkillLoading Expected,
    ImmutableArray<NativeSkillEntry> NativeObservations,
    ImmutableArray<string> Disagreements, ImmutableArray<string> PortableDiagnostics,
    string SessionState = "unknown")
{
    internal string? EntryPath { get; init; }
    internal ImmutableArray<GeminiExtensionExpectation> GeminiExtensions { get; init; } = [];
    internal ImmutableArray<string> InvocationAliases { get; init; } = [];
    internal ImmutableArray<string> SelectionPeers { get; init; } = [];
    internal string SelectionPeerCoverage { get; init; } = "complete";
}

internal sealed record EffectiveSkillAnalysis(SkillLoadingContext Context,
    AgentLoadingRuleSet? Rules, string RuleApplicability, DateTimeOffset CapturedAt,
    bool Complete, ImmutableArray<EffectiveSkillCandidate> Candidates,
    ImmutableArray<LoadingFileEvidence> Configuration,
    NativeLoadingObservation Native, ImmutableArray<string> Diagnostics)
{
    internal ImmutableArray<LoadingDiscoveryRoot> DiscoveryRoots { get; init; } = [];
}
