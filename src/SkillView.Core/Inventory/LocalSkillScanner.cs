using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory.Models;
using SkillView.Logging;

namespace SkillView.Inventory;

/// Walks `ScanRoot`s per §10.2 and produces a deduplicated list of
/// `InstalledSkill` records. Symlinks resolve via `PathResolver`; shared
/// canonical targets collapse into a single record with multiple
/// `AgentMembership` entries.
public sealed class LocalSkillScanner
{
    public const string IgnoreMarkerName = ".skillview-ignore";
    public const string SkillFileName = "SKILL.md";

    private readonly Logger _logger;
    public LocalSkillScanner(Logger logger) { _logger = logger; }

    public sealed record Options(bool AllowHiddenDirs = false);

    public ImmutableArray<InstalledSkill> Scan(
        IReadOnlyList<ScanRoot> roots,
        Options? options = null)
    {
        options ??= new Options();
        var byResolved = new Dictionary<string, Builder>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            ScanRoot(root, options, byResolved);
        }

        var builder = ImmutableArray.CreateBuilder<InstalledSkill>(byResolved.Count);
        foreach (var entry in byResolved.Values)
        {
            builder.Add(entry.Build());
        }
        // Stable ordering by name then resolved path for reproducible output.
        builder.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            return string.Compare(a.ResolvedPath, b.ResolvedPath, StringComparison.Ordinal);
        });
        return builder.ToImmutable();
    }

    private void ScanRoot(ScanRoot root, Options opts, Dictionary<string, Builder> acc)
    {
        // `EnumerateFileSystemEntries` includes broken symlinks, which
        // `EnumerateDirectories` silently skips on POSIX (stat fails). §10.2
        // requires broken symlinks to surface, not disappear.
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(root.Path);
        }
        catch (IOException ex)
        {
            _logger.Warn("inventory.scan", $"enumerate {root.Path} failed: {ex.Message}");
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warn("inventory.scan", $"enumerate {root.Path} denied: {ex.Message}");
            return;
        }

        foreach (var child in children)
        {
            var leaf = Path.GetFileName(child);
            if (!opts.AllowHiddenDirs && leaf.StartsWith('.')) continue;
            // Ignore plain files at the scan root: skills are directories.
            var isSymlink = PathResolver.IsSymlink(child);
            if (!Directory.Exists(child) && !isSymlink) continue;
            ConsiderCandidate(root, child, acc);
        }
    }

    private void ConsiderCandidate(ScanRoot root, string candidatePath, Dictionary<string, Builder> acc)
    {
        var isSymlink = PathResolver.IsSymlink(candidatePath);
        var resolved = PathResolver.Resolve(candidatePath);
        if (resolved is null)
        {
            // Broken symlink or vanished path.
            if (isSymlink)
            {
                RegisterBroken(root, candidatePath, acc);
            }
            return;
        }

        var skillMdPath = Path.Combine(resolved, SkillFileName);
        var hasSkillMd = File.Exists(skillMdPath);

        SkillFrontMatter fm = SkillFrontMatter.Empty;
        var validity = ValidityState.Valid;
        if (!hasSkillMd)
        {
            validity = ValidityState.MissingSkillMd;
        }
        else
        {
            try
            {
                var content = File.ReadAllText(skillMdPath);
                var (_, parsed, parsedFence) = FrontMatterParser.Parse(content);
                if (!parsedFence)
                {
                    validity = ValidityState.UnparsableFrontMatter;
                }
                fm = parsed;
                if (fm.Name is not null && !string.Equals(fm.Name, Path.GetFileName(resolved), StringComparison.Ordinal))
                {
                    // §10.3 "skill name in front-matter matches directory where applicable".
                    validity = ValidityState.NameMismatch;
                }
            }
            catch (IOException ex)
            {
                _logger.Warn("inventory.scan", $"read {skillMdPath} failed: {ex.Message}");
                validity = ValidityState.UnparsableFrontMatter;
            }
        }

        var ignored = File.Exists(Path.Combine(resolved, IgnoreMarkerName));
        var resolvedKey = PathResolver.Normalize(resolved);

        if (!acc.TryGetValue(resolvedKey, out var entry))
        {
            entry = new Builder
            {
                Name = fm.Name ?? Path.GetFileName(resolved) ?? resolved,
                ResolvedPath = resolved,
                ScanRoot = root.Path,
                Scope = root.Scope,
                FrontMatter = fm,
                Validity = validity,
                Ignored = ignored,
                InstalledAt = TryGetInstalledAt(resolved),
                IsSymlinked = isSymlink,
            };
            acc[resolvedKey] = entry;
        }
        else
        {
            // Preserve the strongest validity signal already observed.
            if (entry.Validity == ValidityState.Valid && validity != ValidityState.Valid)
            {
                entry.Validity = validity;
            }
            entry.Ignored = entry.Ignored || ignored;
            entry.IsSymlinked = entry.IsSymlinked || isSymlink;
        }

        var agentId = fm.Name is not null
            ? root.AgentHint ?? "unknown"
            : root.AgentHint ?? "unknown";
        entry.AgentMemberships.Add(new AgentMembership(agentId, candidatePath, isSymlink));
    }

    private static DateTimeOffset? TryGetInstalledAt(string resolved)
    {
        try
        {
            var skillMd = Path.Combine(resolved, SkillFileName);
            if (!File.Exists(skillMd)) return null;
            return new DateTimeOffset(File.GetCreationTimeUtc(skillMd), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static void RegisterBroken(ScanRoot root, string candidatePath, Dictionary<string, Builder> acc)
    {
        var key = PathResolver.Normalize(candidatePath);
        if (acc.ContainsKey(key)) return;
        acc[key] = new Builder
        {
            Name = Path.GetFileName(candidatePath) ?? candidatePath,
            ResolvedPath = candidatePath,
            ScanRoot = root.Path,
            Scope = root.Scope,
            FrontMatter = SkillFrontMatter.Empty,
            Validity = ValidityState.BrokenSymlink,
            Ignored = false,
            InstalledAt = null,
            IsSymlinked = true,
            AgentMemberships = { new AgentMembership(root.AgentHint ?? "unknown", candidatePath, true) },
        };
    }

    private sealed class Builder
    {
        public string Name = string.Empty;
        public string ResolvedPath = string.Empty;
        public string ScanRoot = string.Empty;
        public Scope Scope;
        public SkillFrontMatter FrontMatter = SkillFrontMatter.Empty;
        public ValidityState Validity;
        public bool Ignored;
        public DateTimeOffset? InstalledAt;
        public bool IsSymlinked;
        public List<AgentMembership> AgentMemberships { get; } = new();

        public InstalledSkill Build() => new()
        {
            Name = Name,
            ResolvedPath = ResolvedPath,
            ScanRoot = ScanRoot,
            Scope = Scope,
            Agents = AgentMemberships.ToImmutableArray(),
            FrontMatter = FrontMatter,
            Validity = Validity,
            Provenance = Provenance.FsScan,
            Ignored = Ignored,
            IsSymlinked = IsSymlinked,
            InstalledAt = InstalledAt,
        };
    }
}
