using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory.Models;

namespace SkillView.Inventory;

/// Resolves the set of directories the scanner will walk per §10.1. Project
/// scope is active only inside a git working tree. Unknown / non-existent
/// paths are filtered — missing `.claude/skills` is normal when the user
/// doesn't run that agent, it is not an error.
public sealed class ScanRootResolver
{
    public static readonly ImmutableArray<(string RelativePath, string AgentHint)> ProjectSeeds = ImmutableArray.Create(
        (".agents/skills", "agents"),
        (".claude/skills", "claude"),
        (".github/skills", "github")
    );

    public static readonly ImmutableArray<(string HomeRelativePath, string AgentHint)> UserSeeds = ImmutableArray.Create(
        (".copilot/skills", "copilot"),
        (".claude/skills", "claude"),
        (".cursor/skills", "cursor"),
        (".codex/skills", "codex"),
        (".gemini/skills", "gemini"),
        (".gemini/antigravity/skills", "antigravity")
    );

    public sealed record Options(
        string CurrentDirectory,
        string HomeDirectory,
        IReadOnlyList<string> CustomRoots);

    /// Emits scan roots that actually exist on disk. `Options.CurrentDirectory`
    /// is used to probe for a git working tree.
    public ImmutableArray<ScanRoot> Resolve(Options opts)
    {
        var builder = ImmutableArray.CreateBuilder<ScanRoot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var gitRoot = FindGitRoot(opts.CurrentDirectory);
        if (gitRoot is not null)
        {
            foreach (var (rel, agent) in ProjectSeeds)
            {
                var path = Path.Combine(gitRoot, rel);
                TryAdd(builder, seen, path, Scope.Project, agent);
            }
        }

        foreach (var (rel, agent) in UserSeeds)
        {
            var path = Path.Combine(opts.HomeDirectory, rel);
            TryAdd(builder, seen, path, Scope.User, agent);
        }

        foreach (var custom in opts.CustomRoots)
        {
            if (string.IsNullOrWhiteSpace(custom)) continue;
            var full = Path.GetFullPath(custom);
            TryAdd(builder, seen, full, Scope.Custom, agentHint: null);
        }

        return builder.ToImmutable();
    }

    /// Exposed for the Doctor screen — returns the same roots `Resolve` would
    /// produce, but marked by existence. UI wants to show "Would scan X
    /// (missing)" vs "Will scan X" without treating missing as error.
    public static string? FindGitRoot(string start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        string? cursor = Path.GetFullPath(start);
        while (!string.IsNullOrEmpty(cursor))
        {
            if (Directory.Exists(Path.Combine(cursor, ".git"))) return cursor;
            // Shallow-clone worktrees store `.git` as a pointer file, not a dir.
            if (File.Exists(Path.Combine(cursor, ".git"))) return cursor;
            var parent = Path.GetDirectoryName(cursor);
            if (parent == cursor) return null;
            cursor = parent;
        }
        return null;
    }

    private static void TryAdd(
        ImmutableArray<ScanRoot>.Builder builder,
        HashSet<string> seen,
        string path,
        Scope scope,
        string? agentHint)
    {
        if (!Directory.Exists(path)) return;
        var normalized = NormalizeKey(path);
        if (!seen.Add(normalized)) return;
        builder.Add(new ScanRoot(path, scope, agentHint));
    }

    internal static string NormalizeKey(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Replace('\\', '/').TrimEnd('/');
    }
}
