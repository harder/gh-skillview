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
    private readonly Func<string, Stream> _openMetadata;

    public LocalSkillScanner(Logger logger) : this(
        logger,
        candidateObservedForTests: null,
        enumerateEntriesForTests: null)
    {
    }

    internal LocalSkillScanner(
        Logger logger,
        Action<string>? candidateObservedForTests,
        Func<string, IEnumerator<string>>? enumerateEntriesForTests = null,
        Func<string, Stream>? openMetadataForTests = null)
    {
        _logger = logger;
        _candidateObservedForTests = candidateObservedForTests;
        _enumerateEntries = enumerateEntriesForTests
            ?? (path => Directory.EnumerateFileSystemEntries(path).GetEnumerator());
        _openMetadata = openMetadataForTests ?? RegularFileInspection.Open;
    }

    public sealed record Options(bool AllowHiddenDirs = false)
    {
        internal ManagedSkillDiscovery.Observation? Managed { get; init; }
        // OpenCode's **/SKILL.md observation includes root files and skills
        // nested inside other skills. Ordinary maintenance keeps leaf semantics.
        internal bool DiscoverNestedSkillFiles { get; init; }
        internal bool IncludeRootSkillFiles { get; init; }
    }

    public ImmutableArray<InstalledSkill> Scan(
        IReadOnlyList<ScanRoot> roots,
        Options? options = null) =>
        ScanWithCancellation(roots, options, CancellationToken.None);

    internal ImmutableArray<InstalledSkill> ScanWithCancellation(
        IReadOnlyList<ScanRoot> roots,
        Options? options,
        CancellationToken cancellationToken,
        ICollection<string>? inaccessiblePaths = null,
        ICollection<DiscoveryLocation>? locations = null)
    {
        options ??= new Options();
        var byResolved = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var caseSensitivityByParent = new Dictionary<string, bool>(StringComparer.Ordinal);
        var budget = new ScanBudget();

        var completedManaged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.OrderByDescending(root => root.Managed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root.Managed && root.DirectSkill && !options.DiscoverNestedSkillFiles && !options.IncludeRootSkillFiles)
            {
                var errors = inaccessiblePaths?.Count ?? 0;
                ConsiderCandidate(root, root.Path, byResolved, caseSensitivityByParent, cancellationToken, budget, inaccessiblePaths);
                if (!budget.Exhausted && (inaccessiblePaths?.Count ?? 0) == errors) completedManaged.Add(PathIdentity.NormalizeKey(root.Path));
                continue;
            }
            if (!root.Managed && ManagedBoundary(root.Path, root, options, completedManaged, inaccessiblePaths, locations)) continue;
            var errorsBeforeWalk = inaccessiblePaths?.Count ?? 0;
            if ((options.DiscoverNestedSkillFiles || options.IncludeRootSkillFiles)
                && (root.Managed && root.DirectSkill || File.Exists(Path.Combine(root.Path, SkillFileName))))
                ConsiderCandidate(root, root.Path, byResolved, caseSensitivityByParent, cancellationToken, budget, inaccessiblePaths);
            var result = Walk(root.Path, 0, root, options, byResolved, caseSensitivityByParent,
                cancellationToken, inaccessiblePaths, budget, locations, completedManaged);
            if (root.Managed && result.Complete && (inaccessiblePaths?.Count ?? 0) == errorsBeforeWalk)
                completedManaged.Add(PathIdentity.NormalizeKey(root.Path));
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

    public const int MaxScanEntries = 8192;
    public const int MaxScanDepth = 8;
    public const int MaxScanDirectories = 2048;
    public const int MaxMetadataBytes = 16 * 1024 * 1024;

    private sealed class ScanBudget
    {
        internal int Entries;
        internal int Directories;
        internal int MetadataBytes;
        internal bool Exhausted;
    }

    private (bool Found, bool Complete) Walk(string directory, int depth, ScanRoot root, Options opts,
        Dictionary<string, Builder> acc, Dictionary<string, bool> caseSensitivityByParent,
        CancellationToken token, ICollection<string>? inaccessible, ScanBudget budget,
        ICollection<DiscoveryLocation>? locations, HashSet<string> completedManaged)
    {
        token.ThrowIfCancellationRequested();
        if (budget.Exhausted || depth >= MaxScanDepth || ++budget.Directories > MaxScanDirectories)
        {
            budget.Exhausted |= budget.Directories > MaxScanDirectories;
            inaccessible?.Add(directory);
            Record("budget-exceeded", "Namespace traversal limit reached.");
            return (false, false);
        }
        var found = false;
        var complete = true;
        try
        {
            using var entries = _enumerateEntries(directory);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (budget.Exhausted) return (found, false);
                if (!entries.MoveNext()) break;
                if (++budget.Entries > MaxScanEntries)
                {
                    budget.Exhausted = true;
                    inaccessible?.Add(root.Path);
                    Record("budget-exceeded", "Aggregate entry limit reached.");
                    return (found, false);
                }
                var child = entries.Current;
                _candidateObservedForTests?.Invoke(child);
                token.ThrowIfCancellationRequested();
                var leaf = Path.GetFileName(child);
                if (!root.Managed && ManagedBoundary(child, root, opts, completedManaged, inaccessible, locations))
                {
                    found = true;
                    continue;
                }
                if (!opts.AllowHiddenDirs && leaf.StartsWith('.'))
                {
                    if (locations is { Count: < 128 }) locations.Add(new(child, "excluded", "Hidden entry; use --allow-hidden-dirs to observe it."));
                    try
                    {
                        if ((File.GetAttributes(child) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        { inaccessible?.Add(child); complete = false; }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { inaccessible?.Add(child); complete = false; }
                    continue;
                }
                var linked = PathResolver.IsSymlink(child);
                if (!Directory.Exists(child) && !linked) continue;
                // Windows Directory.Exists can report a dangling directory link
                // as present. Resolve the target before treating it as a live
                // container; dangling links must still reach RegisterBroken.
                if (linked && PathResolver.Resolve(child) is not null && !File.Exists(Path.Combine(child, SkillFileName)))
                {
                    inaccessible?.Add(child);
                    if (locations is { Count: < 128 }) locations.Add(new(child, "unsupported", "Linked container is not traversed; add its target as an explicit scan root."));
                    complete = false;
                    continue;
                }
                var candidateObserved = File.Exists(Path.Combine(child, SkillFileName)) || linked;
                if (candidateObserved)
                {
                    ConsiderCandidate(root, child, acc, caseSensitivityByParent, token, budget, inaccessible);
                    found = true;
                    if (budget.Exhausted)
                    {
                        Record("budget-exceeded", "Aggregate metadata byte limit reached.");
                        return (found, false);
                    }
                    if (!opts.DiscoverNestedSkillFiles) continue;
                    if (linked)
                    {
                        inaccessible?.Add(child);
                        if (locations is { Count: < 128 }) locations.Add(new(child, "unsupported", "Linked skill subtree is not traversed; native recursive discovery may find additional skills."));
                        complete = false;
                        continue;
                    }
                }
                var nested = Walk(child, depth + 1, root, opts, acc, caseSensitivityByParent, token, inaccessible, budget, locations, completedManaged);
                complete &= nested.Complete;
                if (nested.Found)
                {
                    found = true;
                    if (locations is { Count: < 128 }) locations.Add(new(child, "container", "Namespace/package container; payloads are its skill descendants."));
                }
                else if (nested.Complete && !root.Managed && !candidateObserved)
                {
                    ConsiderCandidate(root, child, acc, caseSensitivityByParent, token, budget, inaccessible);
                    found = true;
                }
            }
            return (found, complete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            inaccessible?.Add(directory);
            _logger.Warn("inventory.scan", $"enumerate {directory} failed: {ex.Message}");
            Record("inaccessible", ex.Message);
            return (found, false);
        }
        void Record(string state, string reason)
        {
            if (locations is { Count: < 128 }) locations.Add(new(directory, state, reason));
        }
    }

    private static bool ManagedBoundary(string path, ScanRoot root, Options options, HashSet<string> completed,
        ICollection<string>? incomplete, ICollection<DiscoveryLocation>? locations)
    {
        if (options.Managed is not { } managed) return false;
        bool unknown = false;
        bool Observed(ManagedSkillRegion region, string candidate)
        {
            var value = managed.ObserveCoverage(region, candidate);
            if (value is null)
            {
                unknown = true;
                if (incomplete is { Count: < 128 }) incomplete.Add(region.Path);
                if (locations is { Count: < 128 }) locations.Add(new(region.Path, "unsupported", "Managed ownership comparison could not be inspected."));
            }
            return value == true;
        }
        var boundary = managed.Regions.FirstOrDefault(region => Observed(region, path));
        if (boundary is null) return false;
        var required = managed.Roots.Where(candidate => Observed(boundary, candidate.Path)).ToArray();
        var covered = !managed.TopologyIncomplete
            && !managed.Locations.Any(location => location.State == "unsupported" && Observed(boundary, location.Path))
            && required.All(candidate => completed.Contains(PathIdentity.NormalizeKey(candidate.Path))) && !unknown;
        if (!covered) incomplete?.Add(path);
        if (locations is { Count: < 128 }) locations.Add(new(path, covered ? "managed" : "unsupported",
            covered ? "Managed resources observed separately; only declared skill payloads are scanned."
                : "Managed resources remain protected; discovery coverage is incomplete."));
        return true;
    }

    private void ConsiderCandidate(
        ScanRoot root,
        string candidatePath,
        Dictionary<string, Builder> acc,
        Dictionary<string, bool> caseSensitivityByParent,
        CancellationToken cancellationToken, ScanBudget budget, ICollection<string>? inaccessiblePaths = null)
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
                var content = ReadBoundedPrefix(skillMdPath, cancellationToken, budget);
                var (_, parsed, parsedFence) = FrontMatterParser.Parse(content);
                if (!parsedFence)
                {
                    validity = ValidityState.UnparsableFrontMatter;
                }
                fm = parsed;
                if (parsedFence && !fm.Diagnostics.IsEmpty) validity = ValidityState.InvalidMetadata;
                if (fm.Name is not null && !string.Equals(fm.Name, Path.GetFileName(resolved), StringComparison.Ordinal))
                {
                    // Treat a front-matter name mismatch as invalid so it stays visible.
                    validity = ValidityState.NameMismatch;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                inaccessiblePaths?.Add(skillMdPath);
                _logger.Warn("inventory.scan", $"read {skillMdPath} failed: {ex.Message}");
                validity = ValidityState.UnparsableFrontMatter;
            }
        }

        var ignored = File.Exists(Path.Combine(resolved, IgnoreMarkerName));
        var resolvedKey = NormalizeKey(OwnershipPath.CanonicalCandidate(resolved) ?? resolved, caseSensitivityByParent);

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
        entry.AgentMemberships.Add(new AgentMembership(AgentIdentity.Normalize(agentId), candidatePath, isSymlink, root.Scope, Context: root.Context ?? Path.GetDirectoryName(root.Path), DiscoveryRoot: root.Path, Profile: root.Profile, LoadingRule: "filesystem-placement; effective loading unknown"));
    }

    private string ReadBoundedPrefix(string path, CancellationToken cancellationToken, ScanBudget budget)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxFrontMatterPrefixBytes);
        try
        {
            using var stream = _openMetadata(path);
            var total = 0;
            var limit = Math.Min(MaxFrontMatterPrefixBytes, MaxMetadataBytes - budget.MetadataBytes + 1);
            while (total < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, total, limit - total);
                if (read == 0) break;
                total += read;
                budget.MetadataBytes += read;
                if (budget.MetadataBytes > MaxMetadataBytes)
                {
                    budget.Exhausted = true;
                    throw new IOException("Aggregate skill metadata byte limit reached; remaining skills are unobserved.");
                }
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
        var address = OwnershipPath.EntryAddress(candidatePath) ?? candidatePath;
        var key = NormalizeKey(address, caseSensitivityByParent);
        if (!acc.TryGetValue(key, out var entry))
        {
            entry = new Builder
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
            };
            acc.Add(key, entry);
        }
        entry.AgentMemberships.Add(new AgentMembership(AgentIdentity.Normalize(root.AgentHint ?? "unknown"), candidatePath,
            true, root.Scope, Context: root.Context ?? Path.GetDirectoryName(root.Path), DiscoveryRoot: root.Path,
            Profile: root.Profile, LoadingRule: "filesystem-placement; broken exposure"));
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
