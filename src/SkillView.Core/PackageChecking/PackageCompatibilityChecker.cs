using System.Collections.Immutable;
using SkillView.Inventory;

namespace SkillView.PackageChecking;

/// Checks only static package metadata against cited agent/version rules.
/// Never reads user settings, follows configuration links or starts a client.
internal static class PackageCompatibilityChecker
{
    internal static PackageAssessment Check(PackageAgentTarget? target, string? skill, string? fingerprint)
    {
        const string scope = "Selected agent/version package rules only; fingerprint is raw root metadata SHA256. Placement, effective policy and active sessions are not assessed.";
        if (target is null) return Unknown("No explicit agent/version was selected; installed consumers do not establish compatibility.");
        var agent = AgentIdentity.Normalize(target.Agent);
        var sources = Sources(agent);
        var applicability = sources.IsEmpty ? "unsupported-agent"
            : string.IsNullOrWhiteSpace(target.Version) ? "client-version-unknown"
            : agent == "gemini-cli" && target.Version == "0.46.0" ? "source-correlated"
            : "client-version-unverified";
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
            bool? acceptsFraming = skill.StartsWith('\uFEFF') ? false : closing == "---" ? true : !later ? false : null;
            if (acceptsFraming == false)
                return new("incompatible", scope, fingerprint,
                    ["Gemini 0.46.0 rejects a leading BOM or a header without its closing --- delimiter. Review the authored header in its source; no bytes were edited."], sources);
            return Unknown(acceptsFraming is null
                ? "Gemini closing --- acceptance is not established by the supported framing rule."
                : "Gemini 0.46.0 framing rule passed; other native metadata/configuration requirements were not assessed, so whole-agent compatibility remains unknown.", sources);
        }
        return Unknown("No complete package compatibility rule is modeled for this agent/version.", sources);

        PackageAssessment Unknown(string reason, ImmutableArray<PackageRuleSource> sources = default) =>
            new("unknown", scope, fingerprint, [reason], sources.IsDefault ? [] : sources);
    }

    // Only Gemini 0.46.0 has a source-correlated package rule here. Other
    // agents retain documentation evidence and explicit version uncertainty.
    private static ImmutableArray<PackageRuleSource> Sources(string agent) => agent switch
    {
        "gemini-cli" => [new("https://github.com/google-gemini/gemini-cli/blob/v0.46.0/packages/core/src/skills/skillLoader.ts",
            "v0.46.0", "tagged-header-framing")],
        "claude-code" => [new("https://code.claude.com/docs/en/skills", "2026-10-15", "rolling-documentation")],
        "codex" => [new("https://learn.chatgpt.com/docs/build-skills", "2026-10-15", "rolling-documentation")],
        "opencode" => [new("https://opencode.ai/docs/skills/", "2026-10-15", "rolling-documentation")],
        _ => []
    };
}
