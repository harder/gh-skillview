using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using SkillView.Inventory.Models;
using SkillView.Logging;

namespace SkillView.Inventory;

/// Walks `ScanRoot`s and produces a deduplicated list of `InstalledSkill`
/// records. Symlinks resolve via `PathResolver`; shared canonical targets
/// collapse into a single record with multiple `AgentMembership` entries.
public sealed class LocalSkillScanner
{
    public const string IgnoreMarkerName = ".skillview-ignore";
    public const string SkillFileName = "SKILL.md";
    public const int MaxFrontMatterPrefixBytes = 64 * 1024;

    private readonly Logger _logger;
    private readonly Action<string>? _candidateObservedForTests;
    private readonly Func<string, IEnumerator<string>> _enumerateEntries;

    public LocalSkillScanner(Logger logger) : this(
        logger,
        candidateObservedForTests: null,
        enumerateEntriesForTests: null)
    {
    }

    internal LocalSkillScanner(
        Logger logger,
        Action<string>? candidateObservedForTests,
        Func<string, IEnumerator<string>>? enumerateEntriesForTests = null)
    {
        _logger = logger;
        _candidateObservedForTests = candidateObservedForTests;
        _enumerateEntries = enumerateEntriesForTests
            ?? (path => Directory.EnumerateFileSystemEntries(path).GetEnumerator());
    }

    public sealed record Options(bool AllowHiddenDirs = false);

    public ImmutableArray<InstalledSkill> Scan(
        IReadOnlyList<ScanRoot> roots,
        Options? options = null) =>
        ScanWithCancellation(roots, options, CancellationToken.None);

    internal ImmutableArray<InstalledSkill> ScanWithCancellation(
        IReadOnlyList<ScanRoot> roots,
        Options? options,
        CancellationToken cancellationToken)
    {
        options ??= new Options();
        var byResolved = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var caseSensitivityByParent = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanRoot(root, options, byResolved, caseSensitivityByParent, cancellationToken);
        }

        var builder = ImmutableArray.CreateBuilder<InstalledSkill>(byResolved.Count);
        foreach (var entry in byResolved.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(entry.Build());
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Stable ordering by name then resolved path for reproducible output.
        builder.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            return string.Compare(a.ResolvedPath, b.ResolvedPath, StringComparison.Ordinal);
        });
        return builder.ToImmutable();
    }

    private void ScanRoot(
        ScanRoot root,
        Options opts,
        Dictionary<string, Builder> acc,
        Dictionary<string, bool> caseSensitivityByParent,
        CancellationToken cancellationToken)
    {
        // `EnumerateFileSystemEntries` includes broken symlinks, which
        // `EnumerateDirectories` silently skips on POSIX when stat fails.
        // Broken symlinks should surface, not disappear.
        IEnumerator<string>? children = null;
        try
        {
            children = _enumerateEntries(root.Path);
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

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasNext;
                try
                {
                    hasNext = children.MoveNext();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn("inventory.scan", $"enumerate {root.Path} failed: {ex.Message}");
                    return;
                }

                if (!hasNext) return;
                var child = children.Current;
                _candidateObservedForTests?.Invoke(child);
                cancellationToken.ThrowIfCancellationRequested();
                var leaf = Path.GetFileName(child);
                if (!opts.AllowHiddenDirs && leaf.StartsWith('.')) continue;
                // Ignore plain files at the scan root: skills are directories.
                var isSymlink = PathResolver.IsSymlink(child);
                if (!Directory.Exists(child) && !isSymlink) continue;
                ConsiderCandidate(root, child, acc, caseSensitivityByParent, cancellationToken);
            }
        }
        finally
        {
            children.Dispose();
        }
    }

    private void ConsiderCandidate(
        ScanRoot root,
        string candidatePath,
        Dictionary<string, Builder> acc,
        Dictionary<string, bool> caseSensitivityByParent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isSymlink = PathResolver.IsSymlink(candidatePath);
        var resolved = PathResolver.Resolve(candidatePath);
        if (resolved is null)
        {
            // Broken symlink or vanished path.
            if (isSymlink)
            {
                RegisterBroken(root, candidatePath, acc, caseSensitivityByParent);
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
                var content = ReadBoundedPrefix(skillMdPath, cancellationToken);
                var (_, parsed, parsedFence) = FrontMatterParser.Parse(content);
                if (!parsedFence)
                {
                    validity = ValidityState.UnparsableFrontMatter;
                }
                fm = parsed;
                if (fm.Name is not null && !string.Equals(fm.Name, Path.GetFileName(resolved), StringComparison.Ordinal))
                {
                    // Treat a front-matter name mismatch as invalid so it stays visible.
                    validity = ValidityState.NameMismatch;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn("inventory.scan", $"read {skillMdPath} failed: {ex.Message}");
                validity = ValidityState.UnparsableFrontMatter;
            }
        }

        var ignored = File.Exists(Path.Combine(resolved, IgnoreMarkerName));
        var resolvedKey = NormalizeKey(resolved, caseSensitivityByParent);

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

    private static string ReadBoundedPrefix(string path, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxFrontMatterPrefixBytes);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8 * 1024,
                FileOptions.SequentialScan);
            var total = 0;
            while (total < MaxFrontMatterPrefixBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, total, MaxFrontMatterPrefixBytes - total);
                if (read == 0) break;
                total += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

    private static void RegisterBroken(
        ScanRoot root,
        string candidatePath,
        Dictionary<string, Builder> acc,
        Dictionary<string, bool> caseSensitivityByParent)
    {
        var key = NormalizeKey(candidatePath, caseSensitivityByParent);
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

    private static string NormalizeKey(
        string path,
        Dictionary<string, bool> caseSensitivityByParent)
    {
        var normalized = PathIdentity.Normalize(path);
        var parent = Path.GetDirectoryName(normalized) ?? normalized;
        var parentKey = PathIdentity.Normalize(parent);
        if (!caseSensitivityByParent.TryGetValue(parentKey, out var caseSensitive))
        {
            caseSensitive = PathIdentity.IsCaseSensitive(normalized);
            caseSensitivityByParent[parentKey] = caseSensitive;
        }

        return PathIdentity.NormalizeKey(normalized, caseSensitive);
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
