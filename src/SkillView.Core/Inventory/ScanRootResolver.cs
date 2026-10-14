using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory.Models;
using SkillView.Gh;

namespace SkillView.Inventory;

/// Resolves the set of directories the scanner will walk. Project
/// scope observes CWD, bounded worktree ancestors and explicit workspaces. Non-existent
/// paths are filtered — missing `.claude/skills` is normal when the user
/// doesn't run that agent, it is not an error.
public sealed class ScanRootResolver
{
    public static readonly ImmutableArray<(string RelativePath, string AgentHint)> ProjectSeeds = Seeds(project: true)
        .Add((".github/skills", "github")) // Native/legacy Copilot observation, separate from gh placement.
        .Add((".gemini/skills", "gemini")) // Native Gemini workspace skills.
        .Add((".opencode/skills", "opencode")); // Native OpenCode workspace skills.
    public static readonly ImmutableArray<(string HomeRelativePath, string AgentHint)> UserSeeds =
        Seeds(project: false).Add((".codex/skills", "codex"));

    private static ImmutableArray<(string, string)> Seeds(bool project) => GhPlacementCatalog.Entries
        .GroupBy(entry => project ? entry.ProjectDirectory : entry.UserDirectory, StringComparer.Ordinal)
        .Select(group => (group.Key, group.Key is ".agents/skills" or ".config/agents/skills" ? "agents"
            : group.First().Id switch { "claude-code" => "claude", "github-copilot" => "copilot", "gemini-cli" => "gemini", var id => id }))
        .ToImmutableArray();

    public sealed record Options(
        string CurrentDirectory,
        string HomeDirectory,
        IReadOnlyList<string> CustomRoots,
        string? ClaudeUserConfigDir = null,
        string? PiCodingAgentDir = null,
        IReadOnlyList<string>? WorkspaceDirectories = null,
        IReadOnlyList<AgentProfileRoot>? Profiles = null,
        string? GhProjectDirectory = null);

    /// Emits scan roots that actually exist on disk. `Options.CurrentDirectory`
    /// is used to probe for a git working tree.
    public ImmutableArray<ScanRoot> Resolve(Options opts) =>
        ResolveWithCancellation(opts, CancellationToken.None);

    internal ImmutableArray<ScanRoot> ResolveWithCancellation(
        Options opts,
        CancellationToken cancellationToken,
        ICollection<string>? incompleteLocations = null, ScanRootConfiguration? configuration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builder = ImmutableArray.CreateBuilder<ScanRoot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var probes = 0;
        var exhausted = false;
        var contexts = new[] { opts.CurrentDirectory }.Concat(opts.WorkspaceDirectories ?? []).Take(33).ToArray();
        if ((opts.WorkspaceDirectories?.Count ?? 0) > 32 || opts.CustomRoots.Count > 256 || (opts.Profiles?.Count ?? 0) > 32)
        {
            configuration?.MarkIncomplete();
            incompleteLocations?.Add("Discovery input limit exceeded; omitted contexts remain unobserved.");
        }
        foreach (var context in contexts.Concat(opts.GhProjectDirectory is { } ghProject ? [ghProject] : []))
        {
            var selectedContext = Path.GetFullPath(context, opts.CurrentDirectory);
            if (!Directory.Exists(selectedContext))
            {
                foreach (var (rel, agent) in ProjectSeeds)
                    Add(Path.Combine(selectedContext, rel), Scope.Project, agent, contextRoot: selectedContext);
                configuration?.MarkIncomplete();
                incompleteLocations?.Add(selectedContext);
                continue;
            }
            var physicalContext = OwnershipPath.CanonicalCandidate(selectedContext);
            if (physicalContext is null) { configuration?.MarkIncomplete(); physicalContext = selectedContext; }
            if (!PathIdentity.Equals(physicalContext, selectedContext))
                foreach (var (rel, agent) in ProjectSeeds) Add(Path.Combine(selectedContext, rel), Scope.Project, agent, contextRoot: selectedContext);
            var gitRoot = FindGitRootWithCancellation(physicalContext, cancellationToken, incompleteLocations, configuration);
            string? cursor = physicalContext;
            for (var depth = 0; cursor is not null && depth < 32; depth++)
            {
                foreach (var (rel, agent) in ProjectSeeds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Path.Combine(cursor, rel.Replace('/', Path.DirectorySeparatorChar));
                    Add(path, Scope.Project, agent, contextRoot: cursor);
                }
                if (gitRoot is null || PathIdentity.Equals(cursor, gitRoot)) break;
                cursor = Path.GetDirectoryName(cursor);
            }
            if (cursor is not null && gitRoot is not null && !PathIdentity.Equals(cursor, gitRoot))
            {
                configuration?.MarkIncomplete();
                incompleteLocations?.Add("Project ancestor depth limit reached: " + selectedContext);
            }
        }

        foreach (var (rel, agent) in UserSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(opts.HomeDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
            Add(path, Scope.User, agent);
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
            Add(claudeConfigSkills, Scope.User, "claude");
        }

        // gh ≥ 2.99 (cli/cli#14260) similarly honors PI_CODING_AGENT_DIR for
        // Pi's user-scope skills. Keep the default ~/.pi/agent/skills root
        // above as well: users can have legacy skills there, and gh's override
        // is additive from SkillView's inventory perspective.
        if (!string.IsNullOrWhiteSpace(opts.PiCodingAgentDir))
        {
            var piConfigSkills = Path.Combine(opts.PiCodingAgentDir, "skills");
            Add(piConfigSkills, Scope.User, "pi");
        }

        foreach (var custom in opts.CustomRoots.Take(256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(custom)) continue;
            var full = Path.GetFullPath(custom, opts.CurrentDirectory);
            Add(full, Scope.Custom, agentHint: null, required: true);
        }

        foreach (var profile in (opts.Profiles ?? []).Take(32))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GhPlacementCatalog.Entries.Any(entry => entry.Id == AgentIdentity.Normalize(profile.AgentId)))
                throw new IOException("Unknown agent profile: " + profile.AgentId);
            var path = Path.GetFullPath(profile.SkillsDirectory, opts.CurrentDirectory);
            Add(path, Scope.User, AgentIdentity.Normalize(profile.AgentId), profile.Name, Path.GetDirectoryName(path), required: true);
        }

        return builder.ToImmutable();

        void Add(string path, Scope scope, string? agentHint, string? profile = null, string? contextRoot = null, bool required = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path, opts.CurrentDirectory);
            configuration?.Add(new(path, scope, agentHint)
            { Profile = profile, Context = contextRoot ?? (scope == Scope.User ? opts.HomeDirectory : Path.GetDirectoryName(path)) });
            if (exhausted) return;
            if (++probes > 4096 || builder.Count >= 256)
            {
                exhausted = true;
                configuration?.MarkIncomplete();
                incompleteLocations?.Add("Root discovery limit exceeded; omitted locations remain unobserved.");
                return;
            }
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
                { if (required) incompleteLocations?.Add(path); return; }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { if (required) incompleteLocations?.Add(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { incompleteLocations?.Add(path); return; }
            var key = NormalizeKey(path) + "\0" + scope + "\0" + agentHint + "\0" + (profile ?? "");
            if (seen.Add(key)) builder.Add(new(path, scope, agentHint) { Profile = profile, Context = contextRoot ?? (scope == Scope.User ? opts.HomeDirectory : Path.GetDirectoryName(path)) });
        }
    }

    public sealed record AgentProfileRoot(string AgentId, string SkillsDirectory, string? Name = null);

    /// Exposed for the Doctor screen — returns the same roots `Resolve` would
    /// produce, but marked by existence. UI wants to show "Would scan X
    /// (missing)" vs "Will scan X" without treating missing as error.
    public static string? FindGitRoot(string start) =>
        FindGitRootWithCancellation(start, CancellationToken.None);

    internal static string? FindGitRootWithCancellation(
        string start,
        CancellationToken cancellationToken, ICollection<string>? incompleteLocations = null, ScanRootConfiguration? configuration = null)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        string? cursor = Path.GetFullPath(start);
        for (var depth = 0; !string.IsNullOrEmpty(cursor) && depth < 128; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(Path.Combine(cursor, ".git"))) return cursor;
            // Shallow-clone worktrees store `.git` as a pointer file, not a dir.
            if (File.Exists(Path.Combine(cursor, ".git"))) return cursor;
            var parent = Path.GetDirectoryName(cursor);
            if (parent == cursor) return null;
            cursor = parent;
        }
        if (!string.IsNullOrEmpty(cursor))
        {
            configuration?.MarkIncomplete();
            incompleteLocations?.Add("Git ancestor lookup limit reached: " + start);
        }
        return null;
    }

    internal static string NormalizeKey(string path)
    {
        return PathIdentity.NormalizeKey(path);
    }
}

/// Expected placements remain policy evidence when they are absent on disk.
/// This completeness is separate from scan/provider availability.
internal sealed class ScanRootConfiguration
{
    internal const int MaxRoots = 4096;
    private readonly List<ScanRoot> _roots = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    internal bool IsComplete { get; private set; } = true;
    internal ImmutableArray<ScanRoot> Roots => _roots.ToImmutableArray();
    internal void MarkIncomplete() => IsComplete = false;
    internal void Add(ScanRoot root)
    {
        var key = root.Path + "\0" + root.Scope + "\0" + root.AgentHint + "\0" + root.Profile;
        if (_seen.Contains(key)) return;
        if (_roots.Count >= MaxRoots) { MarkIncomplete(); return; }
        _seen.Add(key); _roots.Add(root);
    }
}
