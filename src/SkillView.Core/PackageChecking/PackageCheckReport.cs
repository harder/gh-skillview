using System.Collections.Immutable;

namespace SkillView.PackageChecking;

internal sealed record PackageCheckFinding(string Category, string Code, string Path, int? Line,
    string? Target, string Message, string Repair);

internal sealed record PackageCheckCoverage(string Payload, string References, int ObservedEntries,
    long ObservedBytes, int AnalyzedMarkdownFiles, int AnalyzedReferences, ImmutableArray<string> Reasons);

/// A bounded local observation, not an authenticity, compatibility or safety verdict.
internal sealed record PackageCheckReport(string InstallationId, string Path, string FormatStatus,
    string ReferenceStatus, PackageCheckCoverage Coverage, string? PayloadFingerprint,
    ImmutableArray<PackageCheckFinding> Findings, int FindingCount, int OmittedFindingCount,
    bool HasErrors)
{
    internal const string ReferenceScope = "Relative inline/image Markdown links, used reference-style links, and standalone inline code paths under scripts/, assets/ or references/. Unused definitions, anchors, external URLs, HTML and commands are not validated.";
    internal string AgentCompatibility => "not-assessed";
    internal string SourceReview => "not-assessed";
    internal string Execution => "none";
}
