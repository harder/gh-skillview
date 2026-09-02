using System.Collections.Immutable;
using SkillView.Inventory;

namespace SkillView.Ui;

internal static class InstallAgentCatalog
{
    // HomeRelativePath is best-effort: null means we don't have a verified
    // on-disk convention for that agent, so it's simply never pre-checked /
    // never shown in the Doctor "detected agents" table. It never gates the
    // actual `gh skill install` call (that only ever sends GhId), so a null
    // or wrong path is cosmetic, not a correctness or safety issue.
    internal sealed record Entry(string GhId, string Label, string AgentHint, string? HomeRelativePath);

    // Mirrors `gh skill install --help`'s full `--agent` list (gh 2.99.0).
    // Update this array when new agents are added to the gh skill ecosystem —
    // re-diff against `gh skill install --help` when bumping the gh minimum.
    internal static readonly ImmutableArray<Entry> Entries =
    [
        new("claude-code", "Claude", "claude", ".claude"),
        new("github-copilot", "Copilot", "copilot", ".copilot"),
        new("cursor", "Cursor", "cursor", ".cursor"),
        new("codex", "Codex", "codex", ".codex"),
        new("gemini-cli", "Gemini", "gemini", ".gemini"),
        new("antigravity", "Antigravity", "antigravity", Path.Combine(".gemini", "antigravity")),
        new("antigravity-cli", "Antigravity CLI", "antigravity-cli", null),
        new("antigravity2.0", "Antigravity 2.0", "antigravity2.0", null),
        new("adal", "AdaL", "adal", null),
        new("amp", "Amp", "amp", Path.Combine(".config", "amp")),
        new("augment", "Augment", "augment", null),
        new("bob", "IBM Bob", "bob", null),
        new("cline", "Cline", "cline", null),
        new("codebuddy", "CodeBuddy", "codebuddy", null),
        new("command-code", "Command Code", "command", null),
        new("continue", "Continue", "continue", ".continue"),
        new("cortex", "Cortex Code", "cortex", null),
        new("crush", "Crush", "crush", null),
        new("deepagents", "Deep Agents", "deepagents", null),
        new("devin", "Devin", "devin", null),
        new("droid", "Droid", "droid", ".factory"),
        new("firebender", "Firebender", "firebender", null),
        new("goose", "Goose", "goose", Path.Combine(".config", "goose")),
        new("grok", "Grok", "grok", null),
        new("iflow-cli", "iFlow CLI", "iflow", ".iflow"),
        new("junie", "Junie", "junie", null),
        new("kilo", "Kilo Code", "kilo", null),
        new("kimi-cli", "Kimi Code CLI", "kimi", null),
        new("kiro-cli", "Kiro CLI", "kiro", ".kiro"),
        new("kode", "Kode", "kode", null),
        new("mcpjam", "MCPJam", "mcpjam", null),
        new("mistral-vibe", "Mistral Vibe", "mistral-vibe", null),
        new("mux", "Mux", "mux", null),
        new("neovate", "Neovate", "neovate", null),
        new("openclaw", "OpenClaw", "openclaw", null),
        new("opencode", "OpenCode", "opencode", Path.Combine(".config", "opencode")),
        new("openhands", "OpenHands", "openhands", ".openhands"),
        new("pi", "Pi", "pi", Path.Combine(".pi", "agent")),
        new("pochi", "Pochi", "pochi", null),
        new("qoder", "Qoder", "qoder", null),
        new("qwen-code", "Qwen Code", "qwen", ".qwen"),
        new("replit", "Replit", "replit", null),
        new("roo", "Roo Code", "roo", null),
        new("trae", "Trae", "trae", null),
        new("trae-cn", "Trae CN", "trae-cn", null),
        new("universal", "Universal", "universal", null),
        new("warp", "Warp", "warp", null),
        new("zencoder", "Zencoder", "zencoder", null),
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
        string? piCodingAgentDir = null)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return found;
        }

        foreach (var entry in Entries)
        {
            var full = GetHomePath(entry, homeDirectory, piCodingAgentDir);
            if (full is not null && Directory.Exists(full))
            {
                found.Add(entry.GhId);
            }
        }

        return found;
    }

    internal static List<(string Label, string Path)> DetectInstalledDisplayEntries(
        string homeDirectory,
        string? piCodingAgentDir = null)
    {
        var found = new List<(string Label, string Path)>();
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return found;
        }

        foreach (var entry in Entries)
        {
            var full = GetHomePath(entry, homeDirectory, piCodingAgentDir);
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

    private static string? GetHomePath(Entry entry, string homeDirectory, string? piCodingAgentDir)
    {
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
