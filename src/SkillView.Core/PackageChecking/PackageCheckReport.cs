using System.Collections.Immutable;

namespace SkillView.PackageChecking;

internal sealed record PackageCheckFinding(string Category, string Code, string Path, int? Line,
    string? Target, string Message, string Repair);

internal sealed record PackageCheckCoverage(string Payload, string References, int ObservedEntries,
    long ObservedBytes, int AnalyzedMarkdownFiles, int AnalyzedReferences, ImmutableArray<string> Reasons);

internal sealed record PackageRuleSource(string Url, string Revision, string Evidence);
internal sealed record PackageAssessment(string State, string Scope, string? Fingerprint,
    ImmutableArray<string> Reasons, ImmutableArray<PackageRuleSource> Sources, string? ObservedCondition = null);
internal sealed record PackageObservation(string Category, string Kind, string Path, int? Line,
    string Value, string State, string Repair);
internal sealed record PackageAgentTarget(string Agent, string? Version);

/// A bounded local observation, not an authenticity, compatibility or safety verdict.
internal sealed record PackageCheckReport(string InstallationId, string Path, string FormatStatus,
    string ReferenceStatus, PackageCheckCoverage Coverage, string? PayloadFingerprint,
    ImmutableArray<PackageCheckFinding> Findings, int FindingCount, int OmittedFindingCount,
    bool HasErrors)
{
    internal const string ReferenceScope = "Relative inline/image Markdown links, used reference-style links, standalone inline code paths under scripts/, assets/ or references/, and supported manifest-local dependencies. Fragments use plain ASCII ATX heading IDs with GitHub-style duplicate suffixes. Unused definitions, unsupported heading syntax, HTML, commands and external targets remain unvalidated.";
    // Retain schema-1 fields' meaning. Schema 2 adds independent assessments;
    // observing a literal declaration or a URL never establishes execution/access.
    internal string AgentCompatibility => "not-assessed";
    internal string SourceReview => "not-assessed";
    internal string Execution => "none";
    internal string? RootMetadataSha256 { get; init; }
    internal PackageAssessment Format => new(FormatStatus, "Portable root SKILL.md frontmatter only; fingerprint is raw root metadata SHA256.", RootMetadataSha256,
        FormatStatus == "valid" ? [] : Findings.Where(f => f.Category == "metadata").Select(f => f.Message)
            .DefaultIfEmpty(FormatStatus is "invalid" or "missing" ? "Root metadata failed portable format requirements; see the bounded findings."
                : "Root metadata was not completely observed.").ToImmutableArray(), []);
    internal PackageAssessment PayloadIntact => new("unknown",
        "Source integrity requires an explicit expected baseline. Local capture/reference condition is a separate observation; provenance, dependency availability and behavior are not established.",
        PayloadFingerprint, Coverage.Reasons.Add("No trusted or explicitly declared expected payload baseline was supplied; readable bytes and matching local references cannot establish source integrity."), [],
        Coverage.Payload != "complete" ? "capture-unavailable" : FormatStatus == "missing" || ReferenceStatus == "findings" ? "local-defects-observed"
            : Coverage.References != "limited" ? "reference-coverage-partial" : "no-supported-reference-defects-observed");
    internal PackageAssessment AgentAssessment { get; init; } = new("unknown", "Selected agent/version package rules only; no client or effective policy observation.",
        null, ["No explicit agent/version was selected; installed consumers do not establish compatibility."], []);
    internal PackageAssessment SourceReviewed => new("not-reviewed", "Explicit source review is separate from static inspection.",
        PayloadFingerprint, ["No source-review acknowledgement is collected by this static check."], []);
    internal ImmutableArray<PackageObservation> Observations { get; init; } = [];
    internal int ObservationCount { get; init; }
    internal int OmittedObservationCount => ObservationCount - Observations.Length;
    internal ImmutableArray<string> DeclarationReasons { get; init; } = [];
    internal string DeclarationCoverage { get; init; } = "not-assessed";
    internal const string DeclarationScope = "Literal URLs in retained text; authored compatibility/allowed-tools; package.json dependency mappings; plain requirements.txt names/extras/version constraints and local -r includes; selected scripts' shebangs. Availability is unknown; arbitrary commands and other declaration grammars are not interpreted.";
}
