using System.Collections.Immutable;
using SkillView.Inventory;

namespace SkillView.Ui;

internal static class InstallAgentCatalog
{
    internal sealed record Entry(string GhId, string Label, string AgentHint, string HomeRelativePath);

    internal static readonly ImmutableArray<Entry> Entries =
    [
        new("claude-code", "Claude", "claude", ".claude"),
        new("github-copilot", "Copilot", "copilot", ".copilot"),
        new("cursor", "Cursor", "cursor", ".cursor"),
        new("codex", "Codex", "codex", ".codex"),
        new("gemini-cli", "Gemini", "gemini", ".gemini"),
        new("antigravity", "Antigravity", "antigravity", Path.Combine(".gemini", "antigravity")),
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

    internal static HashSet<string> DetectInstalledGhIds(string homeDirectory)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return found;
        }

        foreach (var entry in Entries)
        {
            if (Directory.Exists(Path.Combine(homeDirectory, entry.HomeRelativePath)))
            {
                found.Add(entry.GhId);
            }
        }

        return found;
    }

    internal static List<(string Label, string Path)> DetectInstalledDisplayEntries(string homeDirectory)
    {
        var found = new List<(string Label, string Path)>();
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return found;
        }

        foreach (var entry in Entries)
        {
            var full = Path.Combine(homeDirectory, entry.HomeRelativePath);
            if (Directory.Exists(full))
            {
                found.Add((entry.Label, full));
            }
        }

        return found;
    }
}
