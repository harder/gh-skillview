using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed record AgentReloadInstruction(string Agent, string Requirement, string Reason, string? Source,
    string SourceVersion = "documentation checked 2026-10-16; installed client behavior unobserved");

internal static class AgentReloadGuidance
{
    internal static IEnumerable<AgentReloadInstruction> For(OperationSubject subject)
    {
        var agents = subject.Exposures.Select(exposure => exposure.Membership.AgentId).Distinct(StringComparer.Ordinal);
        var claudePlugin = subject.Distributors.Any(distributor => distributor.Provider is "claude-plugin" or "local-claude-plugin");
        foreach (var agent in agents)
        {
            if (agent == "claude" && claudePlugin)
                yield return new(agent, "reload-or-next-session", "For Claude plugins updated on disk in the background, an already running session keeps its loaded version. Run /reload-plugins in that session or start a new session. Disk verification does not establish session activation.",
                    "https://code.claude.com/docs/en/discover-plugins#keep-plugins-updated");
            else if (agent == "codex")
                yield return new(agent, "conditional-restart", "For newly installed skills, the official Codex skills catalog instructs restarting Codex to discover them. Whether this session has loaded a changed existing skill is unknown.",
                    "https://github.com/openai/skills#installing-a-skill");
            else
                yield return new(agent, "unknown", "No verified reload rule for this agent and delivery type. Consult the client's documentation; disk and owner observations do not establish a loaded version.", null,
                    "unavailable: provider/delivery-specific reload contract not verified");
        }
    }
}
