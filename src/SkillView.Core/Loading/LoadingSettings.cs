using System.Collections.Immutable;
using System.Text.RegularExpressions;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Loading;

// Applicability belongs to a source, not its filesystem existence. Unknown
// trust cannot make project settings authoritative or make them disappear.
internal sealed record LoadingSettingsLayer(string Scope, string Applicability,
    LoadingConfigurationDocument Document)
{
    internal string? ProjectTrust { get; init; }
}

internal sealed record LoadingSettings(ImmutableArray<LoadingSettingsLayer> Layers,
    ImmutableArray<string> UnobservedSources)
{
    internal ImmutableArray<string> LocalPolicyBlockers { get; init; } = [];
    internal ImmutableArray<string> CapturedFacts { get; init; } = [];
    internal string ExpectedProjectTrust { get; init; } = "unknown";
    internal string Fingerprint => SkillDistributorIdentity.Digest([
        .. Layers.SelectMany(layer => new[] { layer.Scope, layer.Applicability,
            layer.ProjectTrust, layer.Document.Evidence.Path, layer.Document.Evidence.State, layer.Document.Evidence.Sha256 }),
        .. UnobservedSources, .. LocalPolicyBlockers, .. CapturedFacts, ExpectedProjectTrust]);

    internal ImmutableArray<LoadingFileEvidence> Evidence => Layers.Select(layer => layer.Document.Evidence with
    { Purpose = layer.Document.Evidence.Purpose + "; applicability=" + layer.Applicability }).ToImmutableArray();

    internal bool HasUncertainLocalPolicy => !LocalPolicyBlockers.IsEmpty || Layers.Any(layer => layer.Applicability != "excluded"
        && (layer.Document.Evidence.State is not ("observed" or "missing")
            || layer.Applicability == "conditional" && HasPolicy(layer.Document.Root)));

    private static bool HasPolicy(LoadingValue? value) => value?.Fields.Keys.Any(key => key is
        "skills" or "skillOverrides" or "permissions" or "permission" or "tools" or "agent" or "mode" or "default_agent"
        or "enabledPlugins" or "security" or "strictPluginOnlyCustomization" or "allowManagedPermissionRulesOnly") == true;

    // Only apply known local sources. Return a separate uncertainty signal;
    // never silently merge a trust-gated document into the effective policy.
    internal LoadingValue Merge(string agentId)
    {
        var agent = AgentIdentity.Normalize(agentId);
        var root = new LoadingValue("table");
        var managed = new LoadingValue("table");
        if (agent == "claude-code")
            foreach (var layer in Layers.Where(layer => IsManaged(layer) && layer.Applicability == "applied" && layer.Document.Root is not null))
                managed = MergeValue(managed, layer.Document.Root!, agent, "");
        var managedPermissionsOnly = managed.Get("allowManagedPermissionRulesOnly")?.Boolean == true;
        foreach (var layer in Layers.Where(layer => layer.Applicability == "applied" && layer.Document.Root is not null))
        {
            var value = layer.Document.Root!;
            if (agent == "claude-code" && !IsManaged(layer))
            {
                value = Without(value, "strictPluginOnlyCustomization", "allowManagedPermissionRulesOnly");
                if (managedPermissionsOnly) value = Without(value, "permissions");
                else if (layer.ProjectTrust is "unknown" or "untrusted" && value.Get("permissions") is { Kind: "table" } permission)
                    value = With(value, "permissions", Without(permission, "allow", "additionalDirectories"));
            }
            if (agent == "opencode") value = NormalizeOpenCodeAgents(value);
            root = MergeValue(root, value, agent, "");
        }
        if (agent == "opencode")
        {
            if (root.Get("mode") is { Kind: "table" } modes)
                root = With(root, "agent", MergeValue(root.Get("agent") ?? new("table"), modes with
                { Fields = modes.Fields.ToImmutableDictionary(item => item.Key, item => OpenCodePolicyShape.SupportedAgent(item.Value) ? AsPrimaryMode(item.Value) : item.Value, StringComparer.Ordinal) }, agent, "agent"));
            root = NormalizeTools(root, deep: true);
        }
        return root;
    }

    private static LoadingValue NormalizeOpenCodeAgents(LoadingValue root)
    {
        root = NormalizePermission(root);
        foreach (var container in new[] { "agent", "mode" })
            if (root.Get(container) is { Kind: "table" } agents)
                root = With(root, container, agents with
                { Fields = agents.Fields.ToImmutableDictionary(item => item.Key, item => NormalizeTools(NormalizePermission(item.Value), deep: false), StringComparer.Ordinal) });
        return root;
    }

    private static LoadingValue NormalizePermission(LoadingValue value)
    {
        if (value.Get("permission") is { Kind: "string", Text: "allow" or "deny" or "ask" } permission)
            return With(value, "permission", new("table")
            { Fields = ImmutableDictionary<string, LoadingValue>.Empty.Add("*", permission), FieldOrder = ["*"] });
        return value;
    }

    private static LoadingValue NormalizeTools(LoadingValue value, bool deep)
    {
        if (value.Kind != "table" || value.Get("tools") is not { Kind: "table" } tools) return value;
        if (tools.Fields.Values.Any(tool => tool.Kind != "boolean")) return value;
        var permission = new LoadingValue("table");
        foreach (var tool in tools.FieldOrder)
        {
            var name = tool is "write" or "edit" or "patch" ? "edit" : tool;
            if (!permission.Fields.ContainsKey(name)) permission = permission with { FieldOrder = permission.FieldOrder.Add(name) };
            permission = permission with { Fields = permission.Fields.SetItem(name, new("string", tools.Fields[tool].Boolean == true ? "allow" : "deny")) };
        }
        if (value.Get("permission") is { } explicitPermission)
        {
            if (explicitPermission.Kind != "table") return value;
            permission = deep ? MergeValue(permission, explicitPermission, "opencode", "permission")
                : explicitPermission.FieldOrder.Aggregate(permission, (current, key) => current with
                {
                    Fields = current.Fields.SetItem(key, explicitPermission.Fields[key]),
                    FieldOrder = current.Fields.ContainsKey(key) ? current.FieldOrder : current.FieldOrder.Add(key)
                });
        }
        return With(value, "permission", permission);
    }

    internal static bool IsManaged(LoadingSettingsLayer layer) => layer.Scope.StartsWith("managed-", StringComparison.Ordinal);
    internal static LoadingValue AsPrimaryMode(LoadingValue value) => With(value, "mode", new("string", "primary"));
    private static LoadingValue Without(LoadingValue value, params string[] keys) => value with
    { Fields = value.Fields.RemoveRange(keys), FieldOrder = value.FieldOrder.Where(key => !keys.Contains(key, StringComparer.Ordinal)).ToImmutableArray() };
    private static LoadingValue With(LoadingValue value, string key, LoadingValue child) => value with
    { Fields = value.Fields.SetItem(key, child), FieldOrder = value.Fields.ContainsKey(key) ? value.FieldOrder : value.FieldOrder.Add(key) };

    private static LoadingValue MergeValue(LoadingValue earlier, LoadingValue later, string agent, string path)
    {
        if (earlier.Kind == "table" && later.Kind == "table")
        {
            var fields = earlier.Fields.ToBuilder();
            var order = earlier.FieldOrder.ToList();
            foreach (var key in later.FieldOrder)
            {
                var childPath = path.Length == 0 ? key : path + "." + key;
                var exists = fields.TryGetValue(key, out var previous);
                fields[key] = exists
                    ? MergeValue(previous!, later.Fields[key], agent, childPath) : later.Fields[key];
                // OpenCode v1.19.1 merges with Remeda 2.26.0 before flattening
                // permissions. Overwriting a key preserves its original slot.
                if (!exists) order.Add(key);
            }
            return new("table") { Fields = fields.ToImmutable(), FieldOrder = order.ToImmutableArray() };
        }
        if (earlier.Kind == "array" && later.Kind == "array"
            && (agent == "claude-code" && path.StartsWith("permissions.", StringComparison.Ordinal)
                || agent == "gemini-cli" && path == "skills.disabled"))
            return new("array") { Items = earlier.Items.Concat(later.Items).Distinct().ToImmutableArray() };
        return later;
    }
}

/// Conditional local-policy expectation, independent of native/session state.
internal sealed record LoadingInvocationPolicy(bool? Enabled, bool? Manual, bool? Automatic,
    string Approval, string Visibility, ImmutableArray<string> Reasons)
{
    internal bool? Slash { get; init; }
    internal static LoadingInvocationPolicy Unknown(string reason) => new(null, null, null, "unknown", "unknown", [reason]);
}

internal static class LoadingPolicyEvaluator
{
    internal static LoadingInvocationPolicy Evaluate(SkillLoadingContext context, LoadingSettings settings,
        string name, string path, LoadingValue? frontMatter, LoadingValue? openAiMetadata, bool plugin, bool managed = false)
    {
        if (settings.HasUncertainLocalPolicy)
            return LoadingInvocationPolicy.Unknown("An applicable local configuration is unreadable/unsupported or a policy-bearing source has unknown trust.");
        if (context.AgentId == "opencode" && settings.Layers.Any(layer => layer.Applicability == "applied"
            && layer.Document.Root is { } root && !OpenCodePolicyShape.Supported(root)))
            return Invalid("An OpenCode source has unsupported/invalid agent or permission fields; native decoding cannot be assumed successful.");
        if (context.AgentId == "codex" && settings.Layers.Count(layer => layer.Applicability == "applied"
            && layer.Document.Root?.Get("skills", "config") is not null) > 1)
            return LoadingInvocationPolicy.Unknown("Multiple Codex skills.config arrays were observed; their native array-merge behavior is not established by the cited documentation.");
        var root = settings.Merge(context.AgentId);
        var containers = context.AgentId switch
        {
            "claude-code" => new[] { "permissions", "skillOverrides" },
            "gemini-cli" or "codex" => ["skills"],
            "opencode" => ["agent", "mode", "tools"],
            _ => Array.Empty<string>()
        };
        if (containers.Any(key => root.Get(key) is { Kind: not "table" }))
            return Invalid("A loading-policy container must be a table/object.");
        try
        {
            return context.AgentId switch
            {
                "claude-code" => Claude(root, name, frontMatter, plugin, managed),
                "gemini-cli" => Gemini(root, name),
                "codex" => Codex(root, path, context, openAiMetadata),
                "opencode" => OpenCode(root, name, context.AgentName, frontMatter),
                _ => LoadingInvocationPolicy.Unknown("No loading policy is implemented for this agent.")
            };
        }
        catch (NotSupportedException)
        { return Invalid("A local permission pattern exceeded the bounded evaluator's supported shape."); }
    }

    private static LoadingInvocationPolicy Gemini(LoadingValue root, string name)
    {
        var enabled = root.Get("skills", "enabled");
        if (enabled is not null && enabled.Kind != "boolean") return Invalid("skills.enabled must be a boolean.");
        var disabled = root.Get("skills", "disabled");
        if (disabled is not null && (disabled.Kind != "array" || disabled.Items.Any(value => value.Kind != "string" || Unresolved(value.Text))))
            return Invalid("skills.disabled must be a resolved array of skill names.");
        var off = enabled?.Boolean == false || disabled?.Items.Any(value => string.Equals(value.Text, name, StringComparison.OrdinalIgnoreCase)) == true;
        return new(!off, !off, !off, "activation-consent", "listed", off
            ? ["Gemini local configuration disables this name or the skills feature. Disabled names combine across layers and compare without case."]
            : ["Gemini local configuration permits discovery; remote admin policy and activation consent remain unobserved."]);
    }

    private static LoadingInvocationPolicy Claude(LoadingValue root, string name, LoadingValue? frontMatter, bool plugin, bool managed)
    {
        var strict = root.Get("strictPluginOnlyCustomization");
        var managedOnly = root.Get("allowManagedPermissionRulesOnly");
        if (managedOnly is not null && managedOnly.Kind != "boolean") return Invalid("Managed permission-source restriction has an unsupported value.");
        if (strict is not null && (strict.Kind is not ("boolean" or "array")
            || strict.Kind == "array" && strict.Items.Any(item => item.Kind != "string")))
            return Invalid("Managed customization restriction has an unsupported value.");
        if (!plugin && !managed && (strict?.Boolean == true || strict?.Items.Any(item => item.Text == "skills") == true))
            return new(false, false, false, "not-applicable", "hidden", ["The managed skills-only customization restriction excludes ordinary local skills."]) { Slash = false };
        var manual = frontMatter?.Get("user-invocable");
        var disableModel = frontMatter?.Get("disable-model-invocation");
        if (manual is not null && manual.Kind != "boolean" || disableModel is not null && disableModel.Kind != "boolean")
            return Invalid("Claude invocation frontmatter must use typed booleans.");
        var user = manual?.Boolean != false;
        var automatic = disableModel?.Boolean != true;
        var visibility = automatic ? "name-and-description" : "hidden-from-model";
        var declaredName = frontMatter?.Get("name") is { Kind: "string", Text: { Length: > 0 } declared } ? declared : name;
        if (!plugin && declaredName != name && (root.Get("skillOverrides", declaredName) is not null || root.Get("skillOverrides", name) is not null))
            return Invalid("Claude visibility addresses a frontmatter/directory alias; per-command override resolution is not established.");
        var policy = !plugin ? root.Get("skillOverrides", name) : null;
        if (policy is not null)
        {
            if (policy.Kind != "string" || policy.Text is not ("on" or "off" or "name-only" or "user-invocable-only"))
                return Invalid("Claude skillOverrides contains an unsupported visibility state.");
            if (policy.Text == "off") return new(false, false, false, "not-applicable", "hidden", ["Claude skillOverrides turns this skill off."]);
            if (manual?.Boolean == false || disableModel?.Boolean == true && policy.Text != "user-invocable-only")
                return Invalid("Explicit Claude visibility conflicts with invocation frontmatter; version-specific native precedence is unverified.");
            user = true; automatic = policy.Text != "user-invocable-only";
            visibility = policy.Text switch { "user-invocable-only" => "hidden-from-model", "name-only" => "name-only", _ => "name-and-description" };
        }
        var approval = "session-policy-unknown";
        foreach (var action in new[] { "allow", "ask", "deny" })
        {
            var rules = root.Get("permissions", action);
            if (rules is null) continue;
            if (rules.Kind != "array" || rules.Items.Any(item => item.Kind != "string" || Unresolved(item.Text)))
                return Invalid("Claude permission lists contain unsupported values.");
            var matches = rules.Items.SelectMany(rule => new[] { name, declaredName }.Distinct(StringComparer.Ordinal)
                .Select(command => ClaudeSkillRule(rule.Text!, command, action == "deny", plugin))).ToArray();
            if (matches.Any(match => match is null)) return Invalid("Claude Skill permission contains a selector/alias requiring native resolution.");
            if (matches.Any(match => match == true)) approval = action;
        }
        if (approval == "deny") automatic = false; // Skill tool permission; manual slash entry is a separate surface.
        return new(user || automatic, user, automatic, approval, visibility,
            [plugin ? "Plugin names are namespaced; skillOverrides does not apply. Plugin enablement is separately required."
                : "Claude frontmatter and local visibility/Skill permissions were evaluated; runtime flags and managed account policy remain unobserved."])
        { Slash = user };
    }

    private static bool? ClaudeSkillRule(string rule, string name, bool deny, bool plugin)
    {
        if (!rule.Contains('(')) return Glob(rule, "Skill");
        if (!rule.StartsWith("Skill(", StringComparison.Ordinal) || !rule.EndsWith(')')) return false;
        var selector = rule[6..^1];
        if (selector.StartsWith("skill:", StringComparison.Ordinal)) return null;
        var arguments = selector.EndsWith(" *", StringComparison.Ordinal);
        if (arguments) selector = selector[..^2];
        if (selector.Contains('*') || selector.Contains('?') || selector.Contains(' ')) return null;
        if (name == selector) return true;
        if (arguments && name.StartsWith(selector + ":", StringComparison.Ordinal)) return true;
        // Current documented deny selectors also cover nested commands by
        // their unqualified name. Plugin names remain distinct namespaces.
        return deny && !plugin && !selector.Contains(':') && name.EndsWith(":" + selector, StringComparison.Ordinal);
    }

    private static LoadingInvocationPolicy Codex(LoadingValue root, string path, SkillLoadingContext context, LoadingValue? openAi)
    {
        var overrides = root.Get("skills", "config");
        bool? enabled = true;
        if (overrides is not null)
        {
            if (overrides.Kind != "array") return Invalid("Codex skills.config must be an array of path settings.");
            foreach (var item in overrides.Items)
            {
                var configured = item.Get("path");
                var flag = item.Get("enabled");
                if (item.Kind != "table" || configured?.Kind != "string" || flag?.Kind != "boolean" || Unresolved(configured.Text))
                    return Invalid("Codex path enablement has an unsupported entry.");
                // Official docs disagree between a folder and SKILL.md. Keep
                // an explicit ambiguity instead of treating either as proven.
                try
                {
                    if (!Path.IsPathFullyQualified(configured.Text!))
                        return Invalid("Relative Codex skill paths require native resolution evidence.");
                    if (PathIdentity.Equals(configured.Text!, path) || PathIdentity.Equals(configured.Text!, Path.Combine(path, "SKILL.md")))
                        enabled = null;
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
                { return Invalid("A Codex skills.config path cannot be resolved; path policy remains unknown."); }
            }
        }
        var implicitFlag = openAi?.Get("policy", "allow_implicit_invocation");
        if (implicitFlag is not null && implicitFlag.Kind != "boolean") return Invalid("Codex implicit-invocation policy must be a boolean.");
        return new(enabled, enabled, enabled == true ? implicitFlag?.Boolean != false : null,
            "session-policy-unknown", implicitFlag?.Boolean == false ? "explicit-only" : "listed",
            enabled is null ? ["A Codex path override matches this entry, but official folder/SKILL.md path contracts disagree; native confirmation is required."]
                : ["Codex equal-name skills coexist. Implicit invocation policy is distinct from explicit selection; configuration changes require restart."]);
    }

    private static LoadingInvocationPolicy OpenCode(LoadingValue root, string name, string? agentName, LoadingValue? frontMatter)
    {
        var defaultAgent = root.Get("default_agent");
        if (defaultAgent is not null && defaultAgent.Kind != "string") return Invalid("OpenCode default_agent must be a resolved name.");
        var automaticRole = agentName is null;
        agentName ??= defaultAgent?.Text ?? "build";
        var agent = agentName is null ? null : root.Get("agent", agentName);
        if (agent is { Kind: not "table" }) return Invalid("The selected OpenCode agent must be an object.");
        var disabled = agent?.Get("disable");
        if (disabled is not null && disabled.Kind != "boolean") return Invalid("OpenCode agent disable must be a boolean.");
        if (automaticRole && (disabled?.Boolean == true || agent?.Get("hidden")?.Boolean == true || agent?.Get("mode")?.Text == "subagent"))
            return Invalid("Automatic OpenCode default-role selection is unavailable or falls back to another role; use an explicit agent context to analyze its policy.");
        if (disabled?.Boolean == true) return new(false, false, false, "not-applicable", "hidden", ["The selected OpenCode agent is disabled."]) { Slash = false };
        if (agentName is not ("build" or "plan") && (agent is null || agentName is "explore" or "general" or "compaction" or "title" or "summary"))
            return Invalid("The selected OpenCode agent's native defaults/custom definition have not been resolved.");
        if (new[] { root.Get("tools"), agent?.Get("tools") }.Any(tools => tools is not null
            && (tools.Kind != "table" || tools.Fields.Values.Any(tool => tool.Kind != "boolean"))))
            return Invalid("OpenCode tools must contain typed booleans.");
        var permission = Permission(root.Get("permission"), name);
        var specific = Permission(agent?.Get("permission"), name);
        if (specific is not null) permission = specific;
        if (permission == "unknown") return Invalid("OpenCode permission policy contains unresolved/unsupported values.");
        permission ??= "allow";
        var auto = frontMatter?.Get("metadata", "opencode/autoinvoke");
        var slash = frontMatter?.Get("metadata", "opencode/slash");
        bool? Flag(LoadingValue? value) => value is null ? null : value.Kind == "boolean" ? value.Boolean
            : value.Kind == "string" && bool.TryParse(value.Text, out var flag) ? flag : null;
        if (auto is not null && Flag(auto) is null || slash is not null && Flag(slash) is null)
            return Invalid("OpenCode metadata invocation flags must be booleans or boolean strings.");
        var off = permission == "deny";
        return new(!off, !off, !off && Flag(auto) != false, permission,
            off || Flag(auto) == false ? "hidden-from-model" : "listed",
            ["OpenCode pattern permissions use last match and selected-agent overrides. Manual slash exposure requires opencode/slash; exact-name tool requests remain subject to permission."])
        { Slash = !off && Flag(slash) == true };
    }

    private static string? Permission(LoadingValue? permission, string name)
    {
        if (permission is null) return null;
        if (permission.Kind == "string") return Action(permission);
        if (permission.Kind != "table") return "unknown";
        string? result = null;
        foreach (var toolPattern in permission.FieldOrder)
        {
            if (Unresolved(toolPattern)) return "unknown";
            if (!OpenCodeGlob(toolPattern, "skill")) continue;
            var skills = permission.Fields[toolPattern];
            if (skills.Kind == "string") { result = Action(skills); continue; }
            if (skills.Kind != "table") return "unknown";
            foreach (var pattern in skills.FieldOrder)
            {
                if (Unresolved(pattern)) return "unknown";
                var action = Action(skills.Fields[pattern]);
                if (action == "unknown") return action;
                if (OpenCodeGlob(pattern, name)) result = action;
            }
        }
        return result;
    }

    private static string Action(LoadingValue value) => value.Kind == "string" && value.Text is "allow" or "deny" or "ask" ? value.Text : "unknown";
    private static LoadingInvocationPolicy Invalid(string reason) => LoadingInvocationPolicy.Unknown(reason);
    private static bool Unresolved(string? value) => value is null || value.Contains("{env:", StringComparison.Ordinal)
        || value.Contains("{file:", StringComparison.Ordinal) || value.Contains("${", StringComparison.Ordinal);
    internal static bool OpenCodeGlob(string pattern, string name, bool? windowsForTests = null)
    {
        if (pattern.Length > 2048) throw new NotSupportedException("Permission pattern budget exceeded.");
        var expression = Regex.Escape(pattern.Replace('\\', '/')).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
        // OpenCode makes the final space-plus-wildcard optional for bare names.
        if (expression.EndsWith("\\ .*", StringComparison.Ordinal)) expression = expression[..^4] + "( .*)?";
        var flags = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Singleline;
        if (windowsForTests ?? OperatingSystem.IsWindows()) flags |= RegexOptions.IgnoreCase;
        return Regex.IsMatch(name.Replace('\\', '/'), "\\A" + expression + "$", flags);
    }
    private static bool Glob(string pattern, string name)
    {
        if (pattern.Length > 2048) throw new NotSupportedException("Permission pattern budget exceeded.");
        return Regex.IsMatch(name,
        "\\A" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
