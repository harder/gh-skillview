using System.Text.RegularExpressions;

namespace SkillView.Loading;

/// Reject positive conclusions when source decoding is unproven. This is the
/// loading-relevant subset of the pinned schema, not portable skill lint.
internal static class OpenCodePolicyShape
{
    internal static bool Supported(LoadingValue root)
    {
        if (root.Kind != "table" || !Permission(root.Get("permission")) || !Tools(root.Get("tools"))) return false;
        if (root.Get("skills") is { } skills)
        {
            if (skills.Kind != "table") return false;
            foreach (var key in new[] { "paths", "urls" })
                if (skills.Get(key) is { } values && (values.Kind != "array" || values.Items.Any(value => value.Kind != "string"))) return false;
        }
        foreach (var key in new[] { "agent", "mode" })
        {
            var agents = root.Get(key);
            if (agents is not null && (agents.Kind != "table" || agents.Fields.Values.Any(agent => !SupportedAgent(agent)))) return false;
        }
        return true;
    }

    internal static bool SupportedAgent(LoadingValue agent)
    {
        if (agent.Kind != "table" || !Permission(agent.Get("permission")) || !Tools(agent.Get("tools"))) return false;
        foreach (var key in new[] { "name", "model", "variant", "prompt", "description" })
            if (agent.Get(key) is { Kind: not "string" }) return false;
        foreach (var key in new[] { "disable", "hidden" }) if (agent.Get(key) is { Kind: not "boolean" }) return false;
        if (agent.Get("mode") is { } mode && (mode.Kind != "string" || mode.Text is not ("subagent" or "primary" or "all"))) return false;
        if (agent.Get("options") is { Kind: not "table" }) return false;
        // The bounded projection deliberately does not retain numeric values;
        // do not infer native finite/range validation from the "other" kind.
        if (new[] { "temperature", "top_p", "steps", "maxSteps" }.Any(key => agent.Get(key) is not null)) return false;
        if (agent.Get("color") is { } color && (color.Kind != "string" || color.Text is null ||
            color.Text is not ("primary" or "secondary" or "accent" or "success" or "warning" or "error" or "info")
                && !Regex.IsMatch(color.Text, "\\A#[0-9a-fA-F]{6}\\z", RegexOptions.NonBacktracking))) return false;
        return true;
    }

    private static bool Tools(LoadingValue? tools) => tools is null || tools.Kind == "table" && tools.Fields.Values.All(value => value.Kind == "boolean");
    private static bool Action(LoadingValue value) => value.Kind == "string" && value.Text is "allow" or "deny" or "ask";
    private static bool Permission(LoadingValue? permission) => permission is null || Action(permission)
        || permission.Kind == "table" && permission.Fields.All(item => Action(item.Value)
            || item.Key is not ("todowrite" or "question" or "webfetch" or "websearch" or "doom_loop")
                && item.Value.Kind == "table" && item.Value.Fields.Values.All(Action));
}
