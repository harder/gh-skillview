using SkillView.Inventory;

namespace SkillView.Loading;

internal static class AgentLoadingRules
{
    internal const string DocumentationRevision = "2026-10-15";

    internal static AgentLoadingRuleSet? Find(string agentId) => AgentIdentity.Normalize(agentId) switch
    {
        "claude-code" => new("claude-code", "claude-skills-" + DocumentationRevision, [], "not-run",
            [new("https://code.claude.com/docs/en/skills", DocumentationRevision, "rolling-documentation"),
            new("https://code.claude.com/docs/en/settings", DocumentationRevision, "rolling-documentation"),
            new("https://code.claude.com/docs/en/settings-reference", DocumentationRevision, "rolling-documentation"),
            new("https://code.claude.com/docs/en/managed-settings", DocumentationRevision, "rolling-documentation")]),
        "codex" => new("codex", "codex-skills-" + DocumentationRevision, [], "not-run",
            [new("https://learn.chatgpt.com/docs/build-skills", DocumentationRevision, "rolling-documentation"),
            new("https://learn.chatgpt.com/docs/config-file/config-basic", DocumentationRevision, "rolling-documentation"),
            new("https://learn.chatgpt.com/docs/enterprise/roll-out-a-gateway", DocumentationRevision, "rolling-system-paths"),
            new("https://learn.chatgpt.com/docs/enterprise/managed-configuration", DocumentationRevision, "rolling-managed-paths"),
            new("https://learn.chatgpt.com/docs/config-file/config-reference", DocumentationRevision, "rolling-documentation"),
            new("https://learn.chatgpt.com/docs/app-server", DocumentationRevision, "rolling-documentation")]),
        "gemini-cli" => new("gemini-cli", "gemini-skills-v0.46.0", ["0.46.0"], "not-run",
            [new("https://geminicli.com/docs/cli/skills/", DocumentationRevision, "rolling-documentation"),
            new("https://github.com/google-gemini/gemini-cli/blob/v0.46.0/packages/core/src/skills/skillManager.ts", "v0.46.0", "tagged-source"),
            new("https://github.com/google-gemini/gemini-cli/blob/v0.46.0/packages/cli/src/config/settings.ts", "v0.46.0", "tagged-source"),
            new("https://github.com/google-gemini/gemini-cli/blob/v0.46.0/packages/core/src/utils/trust.ts", "v0.46.0", "tagged-trust"),
            new("https://github.com/google-gemini/gemini-cli/blob/v0.46.0/packages/cli/src/config/extensions/extensionEnablement.ts", "v0.46.0", "tagged-extension-policy"),
            new("https://github.com/google-gemini/gemini-cli/blob/v0.46.0/packages/cli/src/config/extension-manager.ts", "v0.46.0", "tagged-extension-discovery")]),
        "opencode" => new("opencode", "opencode-skills-" + DocumentationRevision, [], "not-run",
            [new("https://opencode.ai/docs/skills/", DocumentationRevision, "rolling-documentation"),
            new("https://opencode.ai/docs/config/", DocumentationRevision, "rolling-documentation"),
            new("https://opencode.ai/docs/permissions/", DocumentationRevision, "rolling-documentation"),
            new("https://github.com/anomalyco/opencode/blob/v1.19.1/packages/opencode/src/config/paths.ts", "4a929747483f9e19b05c7cf944fc0aca6e5111b0", "tagged-config-paths"),
            new("https://github.com/anomalyco/opencode/blob/v1.19.1/packages/opencode/src/permission/index.ts", "4a929747483f9e19b05c7cf944fc0aca6e5111b0", "tagged-permission-order"),
            new("https://github.com/remeda/remeda/blob/v2.26.0/packages/remeda/src/mergeDeep.ts", "v2.26.0", "tagged-permission-merge"),
            new("https://github.com/anomalyco/opencode/blob/v1.19.1/packages/opencode/src/config/agent.ts", "4a929747483f9e19b05c7cf944fc0aca6e5111b0", "tagged-markdown-agents"),
            new("https://github.com/anomalyco/opencode/blob/v1.19.1/packages/core/src/v1/config/agent.ts", "4a929747483f9e19b05c7cf944fc0aca6e5111b0", "tagged-agent-normalization")]),
        _ => null
    };

    internal static NativeLoadingObservation NativeAvailability(SkillLoadingContext context)
    {
        // A command advertised as "list" can initialize plugins, storage,
        // cleanup, authentication or logging before reaching its list handler.
        // No client is launched until its startup is independently admissible.
        var reason = context.AgentId switch
        {
            "gemini-cli" when context.ClientVersion == "0.46.0" =>
                "Gemini 0.46.0 schedules checkpoint/output/log cleanup before argument parsing and initializes storage during skills list. Automatic zero-write observation is unavailable. " +
                "https://github.com/google-gemini/gemini-cli/blob/v0.46.0/packages/cli/src/gemini.tsx#L365-L380",
            "codex" => "Codex documents skills/list on app-server, but read-only RPC semantics do not establish zero-write process startup or an existing session's state. No app-server session was launched.",
            "claude-code" => "Claude's session skill listing is not an audited standalone zero-write inventory command. No model session was launched; remote account and running-session skills remain unobserved.",
            "opencode" => "OpenCode discovery can depend on plugin and configuration initialization. Zero-write inventory startup has not been established for this client version; no client was launched.",
            _ => "No audited zero-write native skill inventory provider is available for this client/version."
        };
        return NativeLoadingObservation.Unavailable(context, reason);
    }
}
