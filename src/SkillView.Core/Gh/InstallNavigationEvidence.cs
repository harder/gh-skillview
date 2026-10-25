using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Subprocess;

namespace SkillView.Gh;

// A short-lived browsing observation from final install admission, never write authority.
// No child environment, credentials, stdout selectors or native handles are retained.
internal sealed record InstallNavigationEvidence(SkillSourceIdentity Source, IReadOnlyList<string> Roots,
    string Scope, string ContextKey, bool ExactPlacement, IReadOnlyList<string> Agents)
{
    private static readonly string[] ContextVariables = ["HOME", "USERPROFILE", "XDG_CONFIG_HOME", "XDG_STATE_HOME",
        "XDG_DATA_HOME", "XDG_CACHE_HOME", "APPDATA", "LOCALAPPDATA", "CODEX_HOME", "CLAUDE_CONFIG_DIR",
        "CLAUDE_CODE_PLUGIN_CACHE_DIR", "CLAUDE_CODE_PLUGIN_DIRS", "CLAUDE_CODE_PLUGIN_SEED_DIR", "PI_CODING_AGENT_DIR", "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_CONFIG_COUNT",
        "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM", "GIT_CEILING_DIRECTORIES",
        "GIT_DISCOVERY_ACROSS_FILESYSTEM", "GIT_CONFIG_PARAMETERS"];

    internal static string Context(ProcessRunner runner) => SkillDistributorIdentity.Digest(
        [runner.EffectiveWorkingDirectory, .. ContextVariables.Select(key => runner.GetEffectiveEnvironmentVariable(key))]);

    internal static InstallNavigationEvidence? Capture(GhSkillInstallService.Options options,
        IReadOnlyList<string> roots, ProcessRunner runner)
    {
        if (options.NavigationSource is not { Key: not null } source || options.All || options.FromLocal || options.Upstream
            || options.Path is not null || options.Scope is not ("user" or "project")) return null;
        // Catalog legacy/override alternatives are conservative admission boundaries,
        // not proof of the root used by this host version.
        var exact = options.Scope == "project" || !(options.Agents ?? []).Any(agent =>
            AgentIdentity.Normalize(agent) == "codex"
            || AgentIdentity.Normalize(agent) == "claude-code" && !string.IsNullOrEmpty(runner.GetEffectiveEnvironmentVariable("CLAUDE_CONFIG_DIR", options.ProcessEnvironment))
            || AgentIdentity.Normalize(agent) == "pi" && !string.IsNullOrEmpty(runner.GetEffectiveEnvironmentVariable("PI_CODING_AGENT_DIR", options.ProcessEnvironment)));
        return new(source, roots.ToArray(), options.Scope, Context(runner), exact, options.Agents?.ToArray() ?? []);
    }

    internal static SkillSourceIdentity? SourceAnchor(string repo, string? path)
    {
        // Discover's explicit repository, never a selector or authored skill name.
        var parts = repo.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            || parts.Any(part => part is "." or "..") || SkillSourceIdentity.NormalizeSubpath(path) is not { } subpath) return null;
        return new("repository", SkillSourceIdentity.NormalizeRepository("https://github.com/" + repo), subpath, "selected-discover-path", false);
    }
}
