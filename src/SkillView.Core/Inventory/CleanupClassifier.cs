using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory.Models;

namespace SkillView.Inventory;

/// Classifies `InstalledSkill` records into cleanup candidate types.
/// Runs purely over an already-captured `InventorySnapshot` plus the list of
/// scan roots that produced it; does not hit the filesystem for anything the
/// scanner already observed, with the single exception of the "empty-dir"
/// check on scan-root children that the scanner omits (they never become
/// `InstalledSkill` records in the first place).
public static class CleanupClassifier
{
    public enum CandidateKind
    {
        EmptyDirectory,
        Malformed,
        SourceOrphaned,
        Duplicate,
        BrokenSharedMapping,
        HiddenNestedResidue,
        BrokenSymlink,
        OrphanCanonicalCopy,
    }

    public sealed record Candidate(
        CandidateKind Kind,
        string Path,
        string Reason,
        InstalledSkill? Skill);

    public sealed record Options(bool IncludeIgnored = false);

    /// Classify. Candidates with `.skillview-ignore` present are filtered out
    /// unless `Options.IncludeIgnored` is set.
    public static ImmutableArray<Candidate> Classify(
        InventorySnapshot snapshot,
        IReadOnlyList<ScanRoot> scanRoots,
        Options? options = null) =>
        ClassifyWithCancellation(snapshot, scanRoots, options, CancellationToken.None);

    internal static ImmutableArray<Candidate> ClassifyWithCancellation(
        InventorySnapshot snapshot,
        IReadOnlyList<ScanRoot> scanRoots,
        Options? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new Options();
        var result = ImmutableArray.CreateBuilder<Candidate>();

        // Pre-index: skills keyed by resolved path, name→count for duplicates,
        // symlink incoming counts for orphan detection.
        var byResolved = new Dictionary<string, List<InstalledSkill>>(StringComparer.Ordinal);
        var byName = new Dictionary<string, List<InstalledSkill>>(StringComparer.OrdinalIgnoreCase);
        var incomingSymlinksByResolved = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var s in snapshot.Skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedKey = PathResolver.Normalize(s.ResolvedPath);
            if (!byResolved.TryGetValue(resolvedKey, out var resolvedBucket))
            {
                resolvedBucket = [];
                byResolved.Add(resolvedKey, resolvedBucket);
            }
            resolvedBucket.Add(s);
            if (!byName.TryGetValue(s.Name, out var bucket))
            {
                bucket = new List<InstalledSkill>();
                byName[s.Name] = bucket;
            }
            bucket.Add(s);

            foreach (var a in s.Agents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!a.IsSymlink) continue;
                var resolved = PathResolver.Resolve(a.Path);
                if (resolved is null) continue;
                var key = PathResolver.Normalize(resolved);
                incomingSymlinksByResolved.TryGetValue(key, out var n);
                incomingSymlinksByResolved[key] = n + 1;
            }
        }

        foreach (var skill in snapshot.Skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skill.Ignored && !options.IncludeIgnored) continue;

            // Broken symlink — scanner already stamps `BrokenSymlink` validity.
            if (skill.Validity == ValidityState.BrokenSymlink)
            {
                result.Add(new Candidate(
                    CandidateKind.BrokenSymlink,
                    skill.ResolvedPath,
                    $"symlink target missing",
                    skill));
                continue;
            }

            // Malformed (missing SKILL.md, unparsable front-matter, name mismatch).
            if (skill.Validity == ValidityState.MissingSkillMd ||
                skill.Validity == ValidityState.UnparsableFrontMatter ||
                skill.Validity == ValidityState.NameMismatch)
            {
                result.Add(new Candidate(
                    CandidateKind.Malformed,
                    skill.ResolvedPath,
                    $"validity={skill.Validity}",
                    skill));
                continue;
            }

            // Source-orphaned: CLI knows nothing about it but filesystem has it.
            if (snapshot.UsedGhSkillList && skill.Provenance == Provenance.FsScan)
            {
                result.Add(new Candidate(
                    CandidateKind.SourceOrphaned,
                    skill.ResolvedPath,
                    "present on disk but unknown to `gh skill list`",
                    skill));
                continue;
            }

            // Hidden nested residue — inside a hidden directory below the scan
            // root (e.g. `.git/…` or other dotdir paths that slipped past
            // `--allow-hidden-dirs`).
            if (IsHiddenNested(skill.ResolvedPath, scanRoots))
            {
                result.Add(new Candidate(
                    CandidateKind.HiddenNestedResidue,
                    skill.ResolvedPath,
                    "lives inside a hidden ancestor directory",
                    skill));
                continue;
            }

            // Orphan canonical copy: symlinks pointed to it, but none of those
            // incoming symlinks are themselves tracked as installs (they all
            // vanished). Heuristic: the canonical `InstalledSkill` has zero
            // non-symlink agent memberships AND its resolved key has zero
            // recorded incoming symlinks.
            if (!skill.IsSymlinked &&
                skill.Agents.Length > 0 &&
                skill.Agents.All(a => !a.IsSymlink) &&
                incomingSymlinksByResolved.GetValueOrDefault(
                    PathResolver.Normalize(skill.ResolvedPath)) == 0 &&
                LooksLikeCanonicalCopy(skill))
            {
                // Skip — "looks like canonical copy" guard prevents noise.
            }
        }

        // Duplicate logical install — two+ skills with same name at different
        // resolved paths, neither symlinked to the other.
        foreach (var (name, bucket) in byName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bucket.Count < 2) continue;
            // Pick the "primary" (prefer `Both` > `CliList` > `FsScan`, then
            // earliest InstalledAt, then shortest path) and mark the rest as
            // duplicates.
            var sorted = bucket
                .OrderBy(s => s.Provenance switch
                {
                    Provenance.Both => 0,
                    Provenance.CliList => 1,
                    _ => 2,
                })
                .ThenBy(s => s.InstalledAt ?? DateTimeOffset.MaxValue)
                .ThenBy(s => s.ResolvedPath.Length)
                .ToList();
            for (var i = 1; i < sorted.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dup = sorted[i];
                if (dup.Ignored && !options.IncludeIgnored) continue;
                result.Add(new Candidate(
                    CandidateKind.Duplicate,
                    dup.ResolvedPath,
                    $"duplicate of '{sorted[0].ResolvedPath}' (same name '{name}')",
                    dup));
            }
        }

        // Broken shared mapping: two records with the SAME resolved path but
        // different front-matter names — indicates the shared directory has
        // drifted.
        foreach (var group in byResolved.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Count < 2) continue;
            var names = group.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count < 2) continue;
            result.Add(new Candidate(
                CandidateKind.BrokenSharedMapping,
                group[0].ResolvedPath,
                $"shared install reports conflicting names: {string.Join(", ", names)}",
                group[0]));
        }

        // Empty directories inside scan roots — not in `snapshot.Skills`
        // because the scanner already filtered them (no SKILL.md). Walk each
        // scan root, enumerate children, flag empty dirs that aren't already
        // an `InstalledSkill`.
        var knownResolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in snapshot.Skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            knownResolved.Add(PathResolver.Normalize(skill.ResolvedPath));
        }

        foreach (var root in scanRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerator<string>? children;
            try { children = Directory.EnumerateDirectories(root.Path).GetEnumerator(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            using (children)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool hasNext;
                    try { hasNext = children.MoveNext(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }
                    if (!hasNext) break;

                    var child = children.Current;
                    var resolved = PathResolver.Resolve(child);
                    if (resolved is null) continue;
                    if (knownResolved.Contains(PathResolver.Normalize(resolved))) continue;
                    bool empty;
                    try { empty = !Directory.EnumerateFileSystemEntries(resolved).Any(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    if (empty)
                    {
                        result.Add(new Candidate(
                            CandidateKind.EmptyDirectory,
                            resolved,
                            $"empty directory under scan root '{root.Path}'",
                            Skill: null));
                    }
                }
            }
        }

        return result.ToImmutable();
    }

    private static bool IsHiddenNested(string resolvedPath, IReadOnlyList<ScanRoot> roots)
    {
        foreach (var root in roots)
        {
            if (!PathResolver.IsInside(resolvedPath, root.Path)) continue;
            var rootKey = PathResolver.Normalize(root.Path);
            var pathKey = PathResolver.Normalize(resolvedPath);
            if (!pathKey.StartsWith(rootKey, StringComparison.Ordinal)) continue;
            var rel = pathKey.Length == rootKey.Length
                ? string.Empty
                : pathKey[(rootKey.Length + 1)..];
            foreach (var segment in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment.StartsWith('.')) return true;
            }
            return false;
        }
        return false;
    }

    private static bool LooksLikeCanonicalCopy(InstalledSkill s)
    {
        // A canonical copy typically has a tree-sha (it was installed via
        // `gh skill install`) and no symlink in its own path.
        return s.TreeSha is not null && !s.IsSymlinked;
    }
}
