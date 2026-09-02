using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory.Models;

namespace SkillView.Inventory;

/// Resolves the set of directories the scanner will walk. Project
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
        (".gemini/antigravity/skills", "antigravity"),
        (".pi/agent/skills", "pi"),
        // `gh skill install --agent universal --scope user` (gh 2.96.0) writes
        // here — verified: `gh skill install <repo> --agent universal --scope
        // user` reports "Installed ... in ~/.agents/skills", distinct from the
        // default (no --agent) copilot install at ~/.copilot/skills. Mirrors
        // ProjectSeeds' `.agents/skills` entry, which is already shared by
        // several agents at project scope.
        (".agents/skills", "agents")
    );

    public sealed record Options(
        string CurrentDirectory,
        string HomeDirectory,
        IReadOnlyList<string> CustomRoots,
        string? ClaudeUserConfigDir = null,
        string? PiCodingAgentDir = null);

    /// Emits scan roots that actually exist on disk. `Options.CurrentDirectory`
    /// is used to probe for a git working tree.
    public ImmutableArray<ScanRoot> Resolve(Options opts) =>
        ResolveWithCancellation(opts, CancellationToken.None);

    internal ImmutableArray<ScanRoot> ResolveWithCancellation(
        Options opts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builder = ImmutableArray.CreateBuilder<ScanRoot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var gitRoot = FindGitRootWithCancellation(opts.CurrentDirectory, cancellationToken);
        if (gitRoot is not null)
        {
            foreach (var (rel, agent) in ProjectSeeds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(gitRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                TryAdd(builder, seen, path, Scope.Project, agent);
            }
        }

        foreach (var (rel, agent) in UserSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(opts.HomeDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
            TryAdd(builder, seen, path, Scope.User, agent);
        }

        // gh ≥ 2.95 (cli/cli#13523) writes Claude Code user-scope skills to
        // `$CLAUDE_CONFIG_DIR/skills` when that env var is set, instead of the
        // default `~/.claude/skills`. Mirror that here so the filesystem scan
        // corroborates whatever `gh skill list` reports. We add it alongside
        // the default location (rather than replacing it) so any leftover
        // skills in `~/.claude/skills` still surface as orphans. TryAdd dedupes
        // when CLAUDE_CONFIG_DIR happens to point back at `~/.claude`.
        if (!string.IsNullOrWhiteSpace(opts.ClaudeUserConfigDir))
        {
            var claudeConfigSkills = Path.Combine(opts.ClaudeUserConfigDir, "skills");
            TryAdd(builder, seen, claudeConfigSkills, Scope.User, "claude");
        }

        // gh ≥ 2.99 (cli/cli#14260) similarly honors PI_CODING_AGENT_DIR for
        // Pi's user-scope skills. Keep the default ~/.pi/agent/skills root
        // above as well: users can have legacy skills there, and gh's override
        // is additive from SkillView's inventory perspective.
        if (!string.IsNullOrWhiteSpace(opts.PiCodingAgentDir))
        {
            var piConfigSkills = Path.Combine(opts.PiCodingAgentDir, "skills");
            TryAdd(builder, seen, piConfigSkills, Scope.User, "pi");
        }

        foreach (var custom in opts.CustomRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(custom)) continue;
            var full = Path.GetFullPath(custom);
            TryAdd(builder, seen, full, Scope.Custom, agentHint: null);
        }

        return builder.ToImmutable();
    }

    /// Exposed for the Doctor screen — returns the same roots `Resolve` would
    /// produce, but marked by existence. UI wants to show "Would scan X
    /// (missing)" vs "Will scan X" without treating missing as error.
    public static string? FindGitRoot(string start) =>
        FindGitRootWithCancellation(start, CancellationToken.None);

    internal static string? FindGitRootWithCancellation(
        string start,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        string? cursor = Path.GetFullPath(start);
        while (!string.IsNullOrEmpty(cursor))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        return PathIdentity.NormalizeKey(path);
    }
}
