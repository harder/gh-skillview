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
        NameCollision,
        Untracked,
        MetadataConflict,
    }

    public sealed record Candidate(
        CandidateKind Kind,
        string Path,
        string Reason,
        InstalledSkill? Skill,
        bool ReviewOnly = false)
    {
        public OperationImpact? Impact { get; init; }
    }

    internal sealed record CandidateSelection(
        ImmutableArray<Candidate> Unique,
        ImmutableArray<Candidate> Duplicates);

    public sealed record Options(bool IncludeIgnored = false);

    /// Classify. Candidates with `.skillview-ignore` present are filtered out
    /// unless `Options.IncludeIgnored` is set.
    public static ImmutableArray<Candidate> Classify(
        InventorySnapshot snapshot,
        IReadOnlyList<ScanRoot> scanRoots,
        Options? options = null) =>
        ClassifyWithCancellation(snapshot, scanRoots, options, CancellationToken.None);

    /// <summary>
    /// Resolves every candidate path key before returning the destructive
    /// sequence. This must happen before the first removal: otherwise deleting
    /// one occurrence can make a later duplicate impossible to normalize or
    /// validate, turning an intentional skip into a false failure.
    /// </summary>
    internal static CandidateSelection DeduplicateByPath(
        IReadOnlyList<Candidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = ImmutableArray.CreateBuilder<Candidate>(candidates.Count);
        var duplicates = ImmutableArray.CreateBuilder<Candidate>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seen.Add(PathIdentity.NormalizeKey(candidate.Path)))
            {
                unique.Add(candidate);
            }
            else
            {
                duplicates.Add(candidate);
            }
        }

        return new CandidateSelection(unique.ToImmutable(), duplicates.ToImmutable());
    }

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
            var resolvedKey = PathIdentity.NormalizeKey(s.ResolvedPath);
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
                var key = PathIdentity.NormalizeKey(resolved);
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
                    skill.AllOwnershipClaims.Any() || !skill.OwnershipDiagnostics.IsDefaultOrEmpty
                        ? "symlink target missing; contextual ownership requires a coordinated maintenance plan" : "symlink target missing",
                    skill, ReviewOnly: skill.AllOwnershipClaims.Any() || !skill.OwnershipDiagnostics.IsDefaultOrEmpty));
                continue;
            }

            // Malformed (missing SKILL.md, unparsable front-matter, name mismatch).
            if (skill.Validity == ValidityState.MissingSkillMd ||
                skill.Validity == ValidityState.UnparsableFrontMatter ||
                skill.Validity == ValidityState.NameMismatch ||
                skill.Validity == ValidityState.InvalidMetadata)
            {
                result.Add(new Candidate(
                    CandidateKind.Malformed,
                    skill.ResolvedPath,
                    $"validity={skill.Validity}; inspect or repair metadata before removal",
                    skill, ReviewOnly: true));
                continue;
            }

            // Cache versions and built-ins are intentional managed resources.
            // Their absence from gh's loose-skill registry is not cleanup evidence.
            if (skill.AllOwnershipClaims.Any(owner => owner.InstallationMode == "managed-read-only")) continue;

            // Tracking absence is only an untracked review observation, never
            // evidence that the payload is unused or its upstream was deleted.
            if (WasCovered(snapshot, skill) && skill.Provenance == Provenance.FsScan)
            {
                result.Add(new Candidate(
                    CandidateKind.Untracked,
                    skill.ResolvedPath,
                    "not reported by gh; may be unmanaged or outside provider coverage — review ownership",
                    skill, ReviewOnly: true));
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
                    skill, ReviewOnly: true));
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
                    PathIdentity.NormalizeKey(skill.ResolvedPath)) == 0 &&
                LooksLikeCanonicalCopy(skill))
            {
                // Skip — "looks like canonical copy" guard prevents noise.
            }
        }

        // A name collision is a review task, never evidence of redundant content.
        // Every physical copy remains visible; no arbitrary survivor is chosen.
        foreach (var (name, bucket) in byName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copies = bucket.Where(skill => !skill.AllOwnershipClaims.Any(owner => owner.InstallationMode == "managed-read-only"))
                .DistinctBy(skill => PathIdentity.NormalizeKey(skill.ResolvedPath)).ToArray();
            if (copies.Length < 2) continue;
            foreach (var copy in copies)
            {
                if (copy.Ignored && !options.IncludeIgnored) continue;
                result.Add(new Candidate(CandidateKind.NameCollision, copy.ResolvedPath,
                    $"{copies.Length} physical installations share name '{name}'; compare sources, revisions, payloads and contexts before consolidation",
                    copy, ReviewOnly: true));
            }
        }
        foreach (var skill in snapshot.Skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skill.MetadataConflicts.IsEmpty || (skill.Ignored && !options.IncludeIgnored)) continue;
            result.Add(new Candidate(CandidateKind.MetadataConflict, skill.ResolvedPath,
                string.Join("; ", skill.MetadataConflicts), skill, ReviewOnly: true));
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
                group[0], ReviewOnly: true));
        }

        // Empty directories inside scan roots — not in `snapshot.Skills`
        // because the scanner already filtered them (no SKILL.md). Walk each
        // scan root, enumerate children, flag empty dirs that aren't already
        // an `InstalledSkill`.
        var knownResolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in snapshot.Skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            knownResolved.Add(PathIdentity.NormalizeKey(skill.ResolvedPath));
        }

        foreach (var root in scanRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root.Managed || snapshot.ManagedRegions.Any(region => region.ObserveCoverage(root.Path) != false)) continue;
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
                    if (snapshot.ManagedRegions.Any(region => region.ObserveCoverage(child) != false)) continue;
                    var resolved = PathResolver.Resolve(child);
                    if (resolved is null) continue;
                    if (knownResolved.Contains(PathIdentity.NormalizeKey(resolved))) continue;
                    bool empty;
                    try { empty = !Directory.EnumerateFileSystemEntries(resolved).Any(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    if (empty)
                    {
                        var leaf = Path.GetFileName(child);
                        var observation = new InstalledSkill
                        {
                            Name = leaf,
                            ResolvedPath = resolved,
                            ScanRoot = root.Path,
                            Scope = root.Scope,
                            Agents = [new("unknown", child, PathResolver.IsSymlink(child), DiscoveryRoot: root.Path)],
                            FrontMatter = SkillFrontMatter.Empty,
                            Validity = ValidityState.MissingSkillMd,
                            Provenance = Provenance.FsScan,
                            Ignored = false,
                            IsSymlinked = false,
                            InstalledAt = null
                        };
                        var protectedOwner = snapshot.IsPartial || snapshot.OwnershipRecords.Any(record => record.Describes(observation));
                        result.Add(new Candidate(
                            CandidateKind.EmptyDirectory,
                            resolved,
                            $"empty directory under scan root '{root.Path}'",
                            Skill: null, ReviewOnly: protectedOwner));
                    }
                }
            }
        }

        return result.Select(candidate =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var impact = candidate.Skill is { } skill
                ? OperationImpact.Payload(OperationSubject.From(skill), "cleanup")
                : SkillIdentityObserver.CaptureEntry(candidate.Path, "cleanup", snapshot.CapturedAt, cancellationToken);
            if (candidate.Kind is CandidateKind.BrokenSharedMapping or CandidateKind.BrokenSymlink
                && candidate.Skill is { } linked && linked.Agents.FirstOrDefault(agent => agent.IsSymlink && PathIdentity.Equals(agent.Path, candidate.Path)) is { } membership)
                impact = OperationImpact.Link(OperationSubject.From(linked), membership);
            return candidate with { Impact = impact };
        }).ToImmutableArray();
    }

    private static bool WasCovered(InventorySnapshot snapshot, InstalledSkill skill)
    {
        if (snapshot.IsPartial) return false;
        // Older in-memory callers have no provider records. Production captures
        // carry explicit coverage; failed or excluded roots establish no absence.
        if (snapshot.Providers.IsEmpty) return snapshot.UsedGhSkillList;
        return snapshot.Providers.Any(provider => provider.Succeeded && provider.CoveredRoots.Any(root =>
            PathResolver.IsInside(skill.ResolvedPath, root)
            || skill.Agents.Any(agent => PathResolver.IsInside(agent.Path, root))));
    }

    private static bool IsHiddenNested(string resolvedPath, IReadOnlyList<ScanRoot> roots)
    {
        foreach (var root in roots)
        {
            if (!PathResolver.IsInside(resolvedPath, root.Path)) continue;
            var relative = Path.GetRelativePath(root.Path, resolvedPath);
            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
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
