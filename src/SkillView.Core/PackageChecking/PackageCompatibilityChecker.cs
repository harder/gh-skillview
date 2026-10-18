using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Loading;

namespace SkillView.PackageChecking;

/// Uses the same cited rule set and framing contract as effective loading, but
/// never reads user settings, follows configuration links or starts a client.
internal static class PackageCompatibilityChecker
{
    internal static PackageAssessment Check(PackageAgentTarget? target, string? skill, string? fingerprint)
    {
        const string scope = "Selected agent/version package rules only; fingerprint is raw root metadata SHA256. Placement, effective policy and active sessions are not assessed.";
        if (target is null) return Unknown("No explicit agent/version was selected; installed consumers do not establish compatibility.");
        var agent = AgentIdentity.Normalize(target.Agent);
        var rules = AgentLoadingRules.Find(agent);
        var sources = rules?.Sources.Select(s => new PackageRuleSource(s.Url, s.Revision, s.Evidence)).ToImmutableArray() ?? [];
        var applicability = rules?.Applicability(target.Version) ?? "unsupported-agent";
        if (applicability != "source-correlated")
            return Unknown($"{agent} {target.Version ?? "(version unknown)"}: {applicability}; no default compatibility is assumed.", sources);
        if (skill is null) return Unknown("Complete regular root SKILL.md text was unavailable.", sources);
        if (agent == "gemini-cli")
        {
            using var reader = new StringReader(skill.TrimStart('\uFEFF'));
            if (reader.ReadLine() != "---") return Unknown("Root header framing is unsupported; portable format does not establish native acceptance.", sources);
            var closing = "missing"; var later = false; var chars = 4;
            while (reader.ReadLine() is { } line)
            {
                chars += line.Length + 1;
                if (chars > FrontMatterParser.MaxYamlChars)
                    return Unknown("Root header framing exceeded its bounded rule input.", sources);
                if (line is not ("---" or "...")) continue;
                closing = line;
                if (closing == "...")
                    while (reader.ReadLine() is { } after)
                        if (after == "---") { later = true; break; }
                break;
            }
            // The input is the complete hash-verified file, not a prefix. Keep
            // YAML framing separate, including a later delimiter after `...`.
            var framing = new LoadingHeaderFraming(skill.StartsWith('\uFEFF'), closing, true, later);
            if (framing.GeminiAcceptance == false)
                return new("incompatible", scope, fingerprint,
                    ["Gemini 0.46.0 rejects a leading BOM or a header without its closing --- delimiter. Review the authored header in its source; no bytes were edited."], sources);
            return Unknown(framing.GeminiAcceptance is null
                ? "Gemini closing --- acceptance is not established by the supported framing rule."
                : "Gemini 0.46.0 framing rule passed; other native metadata/configuration requirements were not assessed, so whole-agent compatibility remains unknown.", sources);
        }
        return Unknown("No complete package compatibility rule is modeled for this agent/version.", sources);

        PackageAssessment Unknown(string reason, ImmutableArray<PackageRuleSource> sources = default) =>
            new("unknown", scope, fingerprint, [reason], sources.IsDefault ? [] : sources);
    }
}
