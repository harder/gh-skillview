using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Gh;

namespace SkillView.Ui;

internal static class InstallAgentCatalog
{
    // HomeRelativePath is best-effort: null means we don't have a verified
    // on-disk convention for that agent, so it's simply never pre-checked /
    // never shown in the Doctor "detected agents" table. It never gates the
    // actual `gh skill install` call (that only ever sends GhId), so a null
    // or wrong path is cosmetic, not a correctness or safety issue.
    internal sealed record Entry(string GhId, string Label, string AgentHint)
    {
        internal string? HomeRelativePath
        {
            get
            {
                if (GhId == "codex") return ".codex"; // Native legacy/config detection, not gh's shared placement.
                if (GhId == "amp") return Path.Combine(".config", "amp"); // Native config, never inferred from shared skills.
                var path = GhPlacementCatalog.Entries.Single(entry => entry.Id == GhId).UserDirectory;
                return path is ".agents/skills" or ".config/agents/skills" ? null : Path.GetDirectoryName(path);
            }
        }
    }

    // Mirrors `gh skill install --help`'s full `--agent` list (gh 2.101.0).
    // Update this array when new agents are added to the gh skill ecosystem —
    // re-diff against `gh skill install --help` when bumping the gh minimum.
    internal static readonly ImmutableArray<Entry> Entries =
    [
        new("claude-code", "Claude", "claude"),
        new("github-copilot", "Copilot", "copilot"),
        new("cursor", "Cursor", "cursor"),
        new("codex", "Codex", "codex"),
        new("gemini-cli", "Gemini", "gemini"),
        new("antigravity", "Antigravity", "antigravity"),
        new("antigravity-cli", "Antigravity CLI", "antigravity-cli"),
        new("antigravity2.0", "Antigravity 2.0", "antigravity2.0"),
        new("adal", "AdaL", "adal"),
        new("amp", "Amp", "amp"),
        new("augment", "Augment", "augment"),
        new("bob", "IBM Bob", "bob"),
        new("cline", "Cline", "cline"),
        new("codebuddy", "CodeBuddy", "codebuddy"),
        new("command-code", "Command Code", "command"),
        new("continue", "Continue", "continue"),
        new("cortex", "Cortex Code", "cortex"),
        new("crush", "Crush", "crush"),
        new("deepagents", "Deep Agents", "deepagents"),
        new("devin", "Devin", "devin"),
        new("droid", "Droid", "droid"),
        new("firebender", "Firebender", "firebender"),
        new("goose", "Goose", "goose"),
        new("grok", "Grok", "grok"),
        new("iflow-cli", "iFlow CLI", "iflow"),
        new("junie", "Junie", "junie"),
        new("kilo", "Kilo Code", "kilo"),
        new("kimi-cli", "Kimi Code CLI", "kimi"),
        new("kiro-cli", "Kiro CLI", "kiro"),
        new("kode", "Kode", "kode"),
        new("mcpjam", "MCPJam", "mcpjam"),
        new("mistral-vibe", "Mistral Vibe", "mistral-vibe"),
        new("mux", "Mux", "mux"),
        new("neovate", "Neovate", "neovate"),
        new("openclaw", "OpenClaw", "openclaw"),
        new("opencode", "OpenCode", "opencode"),
        new("openhands", "OpenHands", "openhands"),
        new("pi", "Pi", "pi"),
        new("pochi", "Pochi", "pochi"),
        new("qoder", "Qoder", "qoder"),
        new("qwen-code", "Qwen Code", "qwen"),
        new("replit", "Replit", "replit"),
        new("roo", "Roo Code", "roo"),
        new("trae", "Trae", "trae"),
        new("trae-cn", "Trae CN", "trae-cn"),
        new("universal", "Universal", "universal"),
        new("warp", "Warp", "warp"),
        new("zencoder", "Zencoder", "zencoder"),
    ];

    internal static ImmutableArray<string> GhIds => Entries.Select(entry => entry.GhId).ToImmutableArray();

    internal static bool HasProjectScopeCandidate(string currentDirectory)
    {
        var gitRoot = ScanRootResolver.FindGitRoot(currentDirectory);
        if (gitRoot is null)
        {
            return false;
        }

        foreach (var (relativePath, _) in ScanRootResolver.ProjectSeeds)
        {
            if (Directory.Exists(Path.Combine(gitRoot, relativePath)))
            {
                return true;
            }
        }

        return false;
    }

    internal static HashSet<string> DetectInstalledGhIds(
        string homeDirectory,
        string? piCodingAgentDir = null, string? claudeConfigDir = null)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return found;
        }

        foreach (var entry in Entries)
        {
            var full = GetHomePath(entry, homeDirectory, piCodingAgentDir, claudeConfigDir);
            if (full is not null && Directory.Exists(full))
            {
                found.Add(entry.GhId);
            }
        }

        return found;
    }

    internal static List<(string Label, string Path)> DetectInstalledDisplayEntries(
        string homeDirectory,
        string? piCodingAgentDir = null, string? claudeConfigDir = null)
    {
        var found = new List<(string Label, string Path)>();
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return found;
        }

        foreach (var entry in Entries)
        {
            var full = GetHomePath(entry, homeDirectory, piCodingAgentDir, claudeConfigDir);
            if (full is null)
            {
                continue;
            }

            if (Directory.Exists(full))
            {
                found.Add((entry.Label, full));
            }
        }

        return found;
    }

    private static string? GetHomePath(Entry entry, string homeDirectory, string? piCodingAgentDir, string? claudeConfigDir)
    {
        if (entry.GhId == "claude-code" && !string.IsNullOrWhiteSpace(claudeConfigDir)) return claudeConfigDir;
        // gh 2.99 honors PI_CODING_AGENT_DIR for all Pi user-scope skill
        // operations. The value names Pi's configuration directory; skills
        // live beneath its `skills` child, while this heuristic intentionally
        // detects the configuration directory itself.
        if (entry.GhId == "pi" && !string.IsNullOrWhiteSpace(piCodingAgentDir))
        {
            return piCodingAgentDir;
        }

        return entry.HomeRelativePath is null
            ? null
            : Path.Combine(homeDirectory, entry.HomeRelativePath);
    }
}
