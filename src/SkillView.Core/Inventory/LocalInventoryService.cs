using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Preferences;

namespace SkillView.Inventory;

/// Merges `gh skill list` records, when available, with the filesystem scan
/// by resolved path. The preferred inventory source wins, but the filesystem
/// scan is always consulted so reconciliation still surfaces `FsScan`-only
/// orphans and anomalies.
public sealed class LocalInventoryService
{
    private readonly ScanRootResolver _resolver;
    private readonly LocalSkillScanner _scanner;
    private readonly GhSkillListAdapter _listAdapter;
    private readonly SkillLockFileReader _lockReader;
    private readonly Logger _logger;
    private readonly ProcessRunner _contextRunner;

    public LocalInventoryService(
        ScanRootResolver resolver,
        LocalSkillScanner scanner,
        GhSkillListAdapter listAdapter,
        Logger logger, ProcessRunner? contextRunner = null)
    {
        _resolver = resolver;
        _scanner = scanner;
        _listAdapter = listAdapter;
        _lockReader = new SkillLockFileReader(logger);
        _logger = logger;
        _contextRunner = contextRunner ?? new ProcessRunner(logger);
    }

    internal LocalInventoryService InContext(ProcessRunner runner) => new(_resolver, _scanner,
        new GhSkillListAdapter(runner, _logger), _logger, runner);

    public sealed record Options(
        IReadOnlyList<string> ScanRoots,
        bool AllowHiddenDirs,
        string? FilterScope = null,
        string? FilterAgent = null,
        IReadOnlyList<string>? WorkspaceDirectories = null,
        IReadOnlyList<ScanRootResolver.AgentProfileRoot>? Profiles = null)
    {
        internal bool DiscoverNestedSkillFiles { get; init; }
        internal bool IncludeRootSkillFiles { get; init; }
    }

    public async Task<InventorySnapshot> CaptureAsync(
        string? ghPath,
        Options options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Local filesystem probing is synchronous by nature. Keep it off the
        // Terminal.Gui thread and make every stage cancellation-aware. Run the
        // gh inventory call concurrently so a slow disk does not add its full
        // latency to a slow subprocess.
        var localTask = Task.Run(async () =>
        {
            string? activeProject = null;
            string? contextError = null;
            var currentDirectory = _contextRunner.EffectiveWorkingDirectory;
            try
            {
                activeProject = await GhPlacementCatalog.ProjectRootAsync(_contextRunner,
                    new(WorkingDirectory: currentDirectory), currentDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { contextError = ex.Message; }
            return CaptureLocal(options, cancellationToken, activeProject, contextError, currentDirectory);
        }, cancellationToken);

        // `gh skill list` is the primary inventory source (gh ≥ 2.97 is
        // required, so it's always available); the filesystem scan above
        // supplements it with symlink/anomaly/package data gh doesn't emit.
        var usedGhList = false;
        ImmutableArray<GhSkillListRecord> ghRecords = ImmutableArray<GhSkillListRecord>.Empty;
        var ghTask = ghPath is null
            ? Task.FromResult(new GhCapture(
                ImmutableArray<GhSkillListRecord>.Empty,
                TimeSpan.Zero, false, "gh unavailable"))
            : CaptureGhAsync(ghPath, cancellationToken);

        await Task.WhenAll(localTask, ghTask).ConfigureAwait(false);
        var local = await localTask.ConfigureAwait(false);
        var gh = await ghTask.ConfigureAwait(false);
        if (ghPath is not null)
        {
            ghRecords = gh.Records;
            usedGhList = gh.Succeeded;
            _logger.Info("inventory", $"gh skill list returned {ghRecords.Length} record(s)");
        }

        var providers = ImmutableArray.CreateBuilder<ProviderObservation>();
        // Default gh discovery covers more agents than local seeds, but only
        // explicit --dir observations establish absence for a particular root.
        providers.Add(new ProviderObservation("gh-default", gh.Succeeded, [], DateTimeOffset.UtcNow, gh.Error));
        providers.Add(new ProviderObservation("lockfiles", local.LockDiagnostics.IsEmpty,
            local.Roots.Select(root => root.Path).ToImmutableArray(), DateTimeOffset.UtcNow,
            local.LockDiagnostics.IsEmpty ? null : string.Join("; ", local.LockDiagnostics.Take(6).Select(d => $"{d.Path}: {d.Message}"))));
        if (ghPath is not null)
        {
            foreach (var root in local.Roots.Where(root => !root.Managed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var custom = await _listAdapter.CaptureAsync(ghPath, directory: root.Path,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                providers.Add(new ProviderObservation("gh", custom.Succeeded, [root.Path], DateTimeOffset.UtcNow, custom.Error));
                ghRecords = ghRecords.AddRange(custom.Records);
                usedGhList |= custom.Succeeded;
            }
        }
        var merged = MergeWithCancellation(local.Scanned, ghRecords, cancellationToken);

        // Enrich with package-bundle metadata from any `.skill-lock.json`
        // files reachable from the scan roots (e.g. `~/.agents/.skill-lock.json`
        // written by `npx skills`). Free signal — best-effort.
        merged = EnrichOwnership(merged, local.Packages, cancellationToken);
        var managedFailures = new List<DiscoveryLocation>();
        merged = merged.Select(skill =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claims = local.Managed.Claims(skill, managedFailures).ToImmutableArray();
            return skill with
            {
                OwnershipClaims = skill.AllOwnershipClaims.Concat(claims).ToImmutableArray(),
                Package = claims.FirstOrDefault() ?? skill.Package,
                OwnershipRunner = _contextRunner
            };
        }).ToImmutableArray();
        var managedComplete = local.Managed.Complete && managedFailures.Count == 0;
        providers.Add(new("managed", managedComplete, local.Managed.Roots.Select(root => root.Path).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            DateTimeOffset.UtcNow, managedComplete ? null : string.Join("; ", local.Managed.Incomplete
                .Concat(managedFailures.Select(failure => failure.Path + ": " + failure.Reason)).Take(6))));
        var managedRecords = merged.SelectMany(skill => skill.AllOwnershipClaims
            .Where(claim => claim.InstallationMode == "managed-read-only")).Distinct().ToImmutableArray();
        if (!local.LockDiagnostics.IsEmpty)
            merged = merged.Select(skill =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return skill with
                {
                    OwnershipDiagnostics = local.LockDiagnostics.Where(diagnostic => diagnostic.CoveredRoots.IsEmpty
                    || diagnostic.CoveredRoots.Any(root => PathIdentity.IsInside(skill.ResolvedPath, root)
                        || skill.Agents.Any(agent => PathIdentity.IsInside(agent.Path, root))))
                    .Select(diagnostic => $"{diagnostic.Path}: {diagnostic.Message}").Take(128).ToImmutableArray()
                };
            }).ToImmutableArray();

        var wantedScope = ParseScope(options.FilterScope);
        if (wantedScope is not null || !string.IsNullOrEmpty(options.FilterAgent))
        {
            merged = FilterWithCancellation(merged,
                skill => MatchesFilters(skill, wantedScope, options.FilterAgent), cancellationToken);
        }

        // Collect diagnostics from the scan pass.
        var brokenCount = 0;
        foreach (var skill in merged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skill.Validity == ValidityState.BrokenSymlink) brokenCount++;
        }
        var diagnostics = new ScanDiagnostics
        {
            FsScanDuration = local.Duration,
            GhListDuration = gh.Duration,
            BrokenSymlinksFound = brokenCount,
            InaccessiblePaths = local.InaccessiblePaths.AddRange(managedFailures.Select(failure => failure.Path)).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            Locations = local.Locations.AddRange(managedFailures),
        };

        var snapshot = new InventorySnapshot
        {
            Skills = merged,
            ScannedRoots = local.Roots,
            ConfiguredRoots = local.ConfiguredRoots,
            ConfiguredRootsComplete = local.ConfigurationComplete,
            ManagedRegions = local.Managed.Regions,
            UsedGhSkillList = usedGhList,
            CapturedAt = DateTimeOffset.UtcNow,
            Diagnostics = diagnostics,
            Providers = providers.ToImmutable(),
            OwnershipRecords = local.Packages.Select(package => package.Package)
                .Concat(managedRecords).ToImmutableArray(),
            OwnershipReadDiagnostics = local.LockDiagnostics,
        };
        var observed = await Task.Run(() => snapshot.Skills.Select(skill =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return skill with
            {
                ObservedSubject = SkillIdentityObserver.Capture(skill, snapshot.CapturedAt,
                !snapshot.IsPartial && snapshot.ConfiguredRootsComplete, cancellationToken, snapshot.ManagedRegions)
            };
        }).ToImmutableArray(), cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var read = CleanupSuppressionStore.ReadFor(_contextRunner, snapshot.ProtectedRoots.Select(root => root.Path)
                .Concat(observed.Select(skill => skill.ResolvedPath)).ToArray(), cancellationToken);
            long byteBudget = 32 * 1024 * 1024;
            var enriched = observed.Select(skill =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var suppression = CleanupSuppressionStore.Match(skill, read, cancellationToken, ref byteBudget);
                return skill with { Ignored = suppression.Active, CleanupSuppression = suppression };
            }).ToImmutableArray();
            return snapshot with { Skills = enriched, PreferenceDiagnostics = read.Diagnostic is { } message ? [message] : [] };
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static bool MatchesFilters(InstalledSkill skill, Scope? scope, string? agent)
    {
        // Both predicates describe the same exposure. Legacy memberships with
        // no scope use the installation scope; known contrary scopes never do.
        if (!string.IsNullOrEmpty(agent))
            return skill.Agents.Any(exposure => AgentIdentity.Matches(exposure.AgentId, agent)
                && (scope is null || (exposure.Scope ?? skill.Scope) == scope));
        return scope is null || skill.Scope == scope || skill.Agents.Any(exposure => exposure.Scope == scope);
    }

    private async Task<GhCapture> CaptureGhAsync(
        string ghPath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var records = await _listAdapter
            // Filter physical rows only after reconciliation, preserving every
            // gh observation needed to explain their other consumers/conflicts.
            .CaptureAsync(ghPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();
        return new GhCapture(records.Records, stopwatch.Elapsed, records.Succeeded, records.Error);
    }

    private LocalCapture CaptureLocal(Options options, CancellationToken cancellationToken, string? activeProject, string? contextError, string currentDirectory)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inaccessible = new List<string>();
        if (contextError is not null) inaccessible.Add("gh project context unobserved: " + contextError);
        string? EnvironmentPath(string name) => _contextRunner.GetEffectiveEnvironmentVariable(name) is { Length: > 0 } value
            ? Path.GetFullPath(value, currentDirectory) : null;
        var home = EnvironmentPath(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configuration = new ScanRootConfiguration();
        if (contextError is not null) configuration.MarkIncomplete();
        var roots = _resolver.ResolveWithCancellation(new ScanRootResolver.Options(
            CurrentDirectory: currentDirectory,
            HomeDirectory: home,
            CustomRoots: options.ScanRoots,
            ClaudeUserConfigDir: EnvironmentPath("CLAUDE_CONFIG_DIR"),
            PiCodingAgentDir: EnvironmentPath("PI_CODING_AGENT_DIR"),
            WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles, GhProjectDirectory: activeProject),
            cancellationToken, inaccessible, configuration);

        _logger.Info("inventory", $"scan roots resolved: {roots.Length}");
        var fsSw = Stopwatch.StartNew();
        var locations = new List<DiscoveryLocation>();
        var managed = ManagedSkillDiscovery.Capture(_contextRunner, roots, cancellationToken, allowHidden: options.AllowHiddenDirs);
        locations.AddRange(managed.Locations);
        inaccessible.AddRange(managed.Incomplete);
        var allRoots = roots.AddRange(managed.Roots);
        var scanned = _scanner.ScanWithCancellation(
            allRoots,
            new LocalSkillScanner.Options(options.AllowHiddenDirs)
            { Managed = managed, DiscoverNestedSkillFiles = options.DiscoverNestedSkillFiles, IncludeRootSkillFiles = options.IncludeRootSkillFiles },
            cancellationToken, inaccessible, locations);
        var lockDiagnostics = new List<LockReadDiagnostic>();
        var packages = _lockReader.LoadObservations(
            roots.Select(root => root.Path),
            cancellationToken, new(home, EnvironmentPath("XDG_STATE_HOME")), diagnostics: lockDiagnostics);
        fsSw.Stop();
        _logger.Info(
            "inventory",
            $"filesystem scan found {scanned.Length} skill(s) in {fsSw.ElapsedMilliseconds}ms");
        return new LocalCapture(allRoots, scanned, packages, fsSw.Elapsed, inaccessible.Distinct(StringComparer.Ordinal).ToImmutableArray(),
            lockDiagnostics.ToImmutableArray(), locations.ToImmutableArray(), configuration.Roots, configuration.IsComplete, managed);
    }

    internal static ImmutableArray<InstalledSkill> EnrichOwnership(ImmutableArray<InstalledSkill> skills,
        ImmutableArray<PackageObservation> packages, CancellationToken cancellationToken)
    {

        if (!packages.IsEmpty)
        {
            var enriched = ImmutableArray.CreateBuilder<InstalledSkill>(skills.Length);
            foreach (var skill in skills)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var claims = packages.Where(entry => (entry.Package.Selector is not null || entry.Name == skill.Name) && entry.Package.Describes(skill))
                    .Select(entry => entry.Package).ToImmutableArray();
                var associated = claims.Where(package => package.Provider != "gh-vercel-global" || SharedTrackingMatches(package, skill)).ToArray();
                var trackingConflicts = claims.Where(package => ContradictsTracking(package, skill))
                    .Select(package => $"Owner record contradicts tracking: {package.LockFilePath}").ToImmutableArray();
                enriched.Add(claims.IsEmpty ? skill : skill with
                {
                    Package = associated.OrderByDescending(package => package.IsExclusiveOwner).FirstOrDefault(),
                    OwnershipClaims = claims,
                    MetadataConflicts = associated.Where(package => package.IsExclusiveOwner)
                        .Select(package => (package.Source, package.SourceUrl, package.SkillPath, package.RepositoryRef,
                            package.PinnedRef, package.ContentHash, package.HashKind, package.PackageVersion)).Distinct().Count() > 1
                        ? skill.MetadataConflicts.Add("Multiple owner records: " + string.Join(", ", claims.Select(p => p.LockFilePath)))
                        : skill.MetadataConflicts.AddRange(trackingConflicts)
                });
            }
            return enriched.MoveToImmutable();
        }
        return skills;
    }

    internal static bool SharedTrackingMatches(SkillPackage package, InstalledSkill skill) =>
        skill.FrontMatter.Upstream is { } repository && package.SourceType == "github"
        && SkillSourceIdentity.NormalizeRepository(repository) == SkillSourceIdentity.NormalizeRepository(
            package.SourceUrl ?? "https://github.com/" + package.Source)
        && package.ContentHash is { } tree && tree == skill.TreeSha
        && SkillSourceIdentity.NormalizeSubpath(package.SkillPath) == SkillSourceIdentity.NormalizeSubpath(skill.FrontMatter.GithubPath)
        && package.PinnedRef == skill.FrontMatter.PinnedRef;

    private static bool ContradictsTracking(SkillPackage package, InstalledSkill skill)
    {
        var tracked = skill.SourceIdentity;
        if (tracked.Origin is null) return false;
        var recorded = (skill with
        {
            FrontMatter = SkillFrontMatter.Empty,
            Package = package,
            OwnershipClaims = [],
            MetadataConflicts = []
        }).SourceIdentity;
        if (recorded.Origin is { } origin && (origin != tracked.Origin || recorded.Provider != tracked.Provider)) return true;
        if (recorded.Subpath is { } path && tracked.Subpath is { } trackedPath && path != trackedPath) return true;
        if (package.PinnedRef is not null && package.PinnedRef != skill.FrontMatter.PinnedRef) return true;
        if (package.HashKind == "recorded-git-tree" && package.ContentHash is { } hash && skill.TreeSha is { } tree && hash != tree) return true;
        return package.RepositoryRef is { } reference && skill.FrontMatter.GithubRef is { } trackedRef
            && ShortRef(reference) != ShortRef(trackedRef);
        static string ShortRef(string value) => value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value[11..]
            : value.StartsWith("refs/tags/", StringComparison.Ordinal) ? value[10..] : value;
    }

    private sealed record LocalCapture(
        ImmutableArray<ScanRoot> Roots,
        ImmutableArray<InstalledSkill> Scanned,
        ImmutableArray<PackageObservation> Packages,
        TimeSpan Duration,
        ImmutableArray<string> InaccessiblePaths,
        ImmutableArray<LockReadDiagnostic> LockDiagnostics,
        ImmutableArray<DiscoveryLocation> Locations,
        ImmutableArray<ScanRoot> ConfiguredRoots,
        bool ConfigurationComplete, ManagedSkillDiscovery.Observation Managed);

    private sealed record GhCapture(
        ImmutableArray<GhSkillListRecord> Records,
        TimeSpan Duration,
        bool Succeeded,
        string? Error);

    internal static ImmutableArray<InstalledSkill> Merge(
        ImmutableArray<InstalledSkill> scanned,
        ImmutableArray<GhSkillListRecord> ghRecords) =>
        MergeWithCancellation(scanned, ghRecords, CancellationToken.None);

    internal static ImmutableArray<InstalledSkill> MergeWithCancellation(
        ImmutableArray<InstalledSkill> scanned,
        ImmutableArray<GhSkillListRecord> ghRecords,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = new Dictionary<string, InstalledSkill>(StringComparer.Ordinal);
        var caseSensitivity = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var skill in scanned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = NormalizeMergeKey(skill.ResolvedPath, caseSensitivity);
            index[key] = index.TryGetValue(key, out var old) ? Combine(old, skill) : skill;
        }
        foreach (var record in ghRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ResolveKey(record, caseSensitivity);
            if (key is null) continue;
            var path = record.ResolvedPath ?? record.Path!;
            var scope = ParseScope(record.Scope) ?? Scope.Custom;
            var hosts = record.Hosts.IsDefaultOrEmpty
                ? (record.Agent is null ? [] : ImmutableArray.Create(record.Agent))
                : record.Hosts;
            var observation = new InstalledSkill
            {
                Name = record.Name ?? Path.GetFileName(path.TrimEnd('/')),
                ResolvedPath = path,
                ScanRoot = path,
                Scope = scope,
                Agents = hosts.Select(host => new AgentMembership(AgentIdentity.Normalize(host), path, record.IsSymlink, scope)).ToImmutableArray(),
                FrontMatter = new SkillFrontMatter
                {
                    Name = record.Name,
                    GithubRef = record.Version,
                    Upstream = record.SourceUrl ?? record.Repo,
                    GithubTreeSha = record.GithubTreeSha,
                    Pinned = record.Pinned,
                },
                Validity = ValidityState.Valid,
                Provenance = Provenance.CliList,
                Ignored = false,
                IsSymlinked = record.IsSymlink,
                InstalledAt = null,
            };
            index[key] = index.TryGetValue(key, out var match) ? Combine(match, observation) : observation;
        }
        return index.Values.OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .ThenBy(skill => skill.ResolvedPath, StringComparer.Ordinal).ToImmutableArray();
    }

    private static InstalledSkill Combine(InstalledSkill first, InstalledSkill next)
    {
        var conflicts = first.MetadataConflicts.AddRange(next.MetadataConflicts).ToBuilder();
        string? Prefer(string? local, string? remote, string field)
        {
            if (!string.IsNullOrWhiteSpace(local) && !string.IsNullOrWhiteSpace(remote)
                && !(field == "repository ref" ? EquivalentRef(local, remote) : string.Equals(local.TrimEnd('/'), remote.TrimEnd('/'), StringComparison.Ordinal)))
                conflicts.Add($"Conflicting {field}: {local} / {remote}");
            return string.IsNullOrWhiteSpace(local) ? string.IsNullOrWhiteSpace(remote) ? null : remote : local;
        }
        var a = first.FrontMatter;
        var b = next.FrontMatter;
        // gh's version is a repository ref, not the author's semantic version.
        var isCli = next.Provenance == Provenance.CliList;
        if (!string.Equals(first.Name, next.Name, StringComparison.Ordinal)
            && !string.Equals(first.Name, Path.GetFileName(next.Name), StringComparison.Ordinal))
            conflicts.Add($"Conflicting skill names: {first.Name} / {next.Name}");
        var fm = a with
        {
            Upstream = Prefer(a.Upstream, b.Upstream, "source"),
            GithubTreeSha = Prefer(a.GithubTreeSha, b.GithubTreeSha, "tree SHA"),
            GithubRef = Prefer(a.GithubRef, b.GithubRef, "repository ref"),
            Version = isCli ? a.Version : Prefer(a.Version, b.Version, "authored version"),
            Pinned = a.Pinned || b.Pinned,
        };
        var memberships = first.Agents.Concat(next.Agents)
            .GroupBy(agent => (AgentIdentity.Normalize(agent.AgentId), PathIdentity.NormalizeKey(agent.Path), agent.Scope, agent.Profile))
            .SelectMany(group =>
            {
                // Missing gh context is an incomplete observation of the same
                // placement, not a new consumer. Preserve every known context,
                // without adding synthetic null consumers to old saved plans.
                var contextual = group.Where(agent => agent.Context is not null).ToArray();
                var contextualRoots = contextual.Where(agent => agent.DiscoveryRoot is not null)
                    .Select(agent => PathIdentity.NormalizeKey(agent.DiscoveryRoot!)).ToHashSet(StringComparer.Ordinal);
                return group.Where(agent => contextual.Length == 0 || agent.Context is not null
                        || agent.DiscoveryRoot is { } root && !contextualRoots.Contains(PathIdentity.NormalizeKey(root)))
                    .GroupBy(agent => agent.Context is null ? null : PathIdentity.NormalizeKey(agent.Context))
                    .SelectMany(context =>
                    {
                        var rooted = context.Where(agent => agent.DiscoveryRoot is not null).ToArray();
                        return (rooted.Length == 0 ? context : rooted.AsEnumerable())
                            .GroupBy(agent => agent.DiscoveryRoot is null ? null : PathIdentity.NormalizeKey(agent.DiscoveryRoot))
                            .Select(root => root.OrderByDescending(agent => agent.IsSymlink).First() with { AgentId = group.Key.Item1 });
                    });
            })
            .ToImmutableArray();
        return first with
        {
            FrontMatter = fm,
            Agents = memberships,
            MetadataConflicts = conflicts.Distinct(StringComparer.Ordinal).ToImmutableArray(),
            Provenance = first.Provenance == next.Provenance ? first.Provenance : Provenance.Both,
            IsSymlinked = first.IsSymlinked || next.IsSymlinked,
            Ignored = first.Ignored || next.Ignored,
        };
    }

    private static bool EquivalentRef(string left, string right)
    {
        static string Short(string value) => value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value[11..]
            : value.StartsWith("refs/tags/", StringComparison.Ordinal) ? value[10..] : value;
        left = Short(left);
        right = Short(right);
        if (left == right) return true;
        return false;
    }

    private static ImmutableArray<InstalledSkill> FilterWithCancellation(
        ImmutableArray<InstalledSkill> source,
        Func<InstalledSkill, bool> predicate,
        CancellationToken cancellationToken)
    {
        var builder = ImmutableArray.CreateBuilder<InstalledSkill>();
        foreach (var skill in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate(skill)) builder.Add(skill);
        }
        return builder.ToImmutable();
    }

    private static string? ResolveKey(
        GhSkillListRecord rec,
        Dictionary<string, bool> caseSensitivityByParent)
    {
        var path = rec.ResolvedPath ?? rec.Path;
        return string.IsNullOrEmpty(path)
            ? null
            : NormalizeMergeKey(path, caseSensitivityByParent);
    }

    private static string NormalizeMergeKey(
        string path,
        Dictionary<string, bool> caseSensitivityByParent)
    {
        // gh and the filesystem scanner can spell the same existing install
        // through different ancestor symlinks (notably /var and /private/var
        // on macOS). Resolve those aliases for inventory matching only; keep
        // the original paths in the records for display and removal policy.
        var normalized = OwnershipPath.CanonicalCandidate(path) ?? OwnershipPath.EntryAddress(path) ?? PathIdentity.Normalize(path);

        var parent = Path.GetDirectoryName(normalized) ?? normalized;
        if (!caseSensitivityByParent.TryGetValue(parent, out var caseSensitive))
        {
            caseSensitive = PathIdentity.IsCaseSensitive(normalized);
            caseSensitivityByParent[parent] = caseSensitive;
        }

        return PathIdentity.NormalizeKey(normalized, caseSensitive);
    }



    internal static Scope? ParseScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return null;
        return scope.ToLowerInvariant() switch
        {
            "project" => Scope.Project,
            "user" => Scope.User,
            "custom" => Scope.Custom,
            _ => null,
        };
    }
}
