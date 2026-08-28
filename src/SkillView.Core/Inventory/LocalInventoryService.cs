using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;

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

    public LocalInventoryService(
        ScanRootResolver resolver,
        LocalSkillScanner scanner,
        GhSkillListAdapter listAdapter,
        Logger logger)
    {
        _resolver = resolver;
        _scanner = scanner;
        _listAdapter = listAdapter;
        _lockReader = new SkillLockFileReader(logger);
        _logger = logger;
    }

    public sealed record Options(
        IReadOnlyList<string> ScanRoots,
        bool AllowHiddenDirs,
        string? FilterScope = null,
        string? FilterAgent = null);

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
        var localTask = Task.Run(() => CaptureLocal(options, cancellationToken), cancellationToken);

        // `gh skill list` is the primary inventory source (gh ≥ 2.95 is
        // required, so it's always available); the filesystem scan above
        // supplements it with symlink/anomaly/package data gh doesn't emit.
        var usedGhList = false;
        ImmutableArray<GhSkillListRecord> ghRecords = ImmutableArray<GhSkillListRecord>.Empty;
        var ghTask = ghPath is null
            ? Task.FromResult(new GhCapture(
                ImmutableArray<GhSkillListRecord>.Empty,
                TimeSpan.Zero))
            : CaptureGhAsync(
                ghPath,
                options.FilterScope,
                options.FilterAgent,
                cancellationToken);

        await Task.WhenAll(localTask, ghTask).ConfigureAwait(false);
        var local = await localTask.ConfigureAwait(false);
        var gh = await ghTask.ConfigureAwait(false);
        if (ghPath is not null)
        {
            ghRecords = gh.Records;
            usedGhList = true;
            _logger.Info("inventory", $"gh skill list returned {ghRecords.Length} record(s)");
        }

        var merged = MergeWithCancellation(local.Scanned, ghRecords, cancellationToken);

        // Enrich with package-bundle metadata from any `.skill-lock.json`
        // files reachable from the scan roots (e.g. `~/.agents/.skill-lock.json`
        // written by `npx skills`). Free signal — best-effort.
        var packages = local.Packages;
        if (!packages.IsEmpty)
        {
            var enriched = ImmutableArray.CreateBuilder<InstalledSkill>(merged.Length);
            foreach (var skill in merged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                enriched.Add(packages.TryGetValue(skill.Name, out var package)
                    ? skill with { Package = package }
                    : skill);
            }
            merged = enriched.MoveToImmutable();
        }

        if (!string.IsNullOrEmpty(options.FilterScope))
        {
            var wanted = ParseScope(options.FilterScope);
            if (wanted is not null)
            {
                merged = FilterWithCancellation(
                    merged,
                    skill => skill.Scope == wanted,
                    cancellationToken);
            }
        }
        if (!string.IsNullOrEmpty(options.FilterAgent))
        {
            merged = FilterWithCancellation(
                merged,
                skill => skill.Agents.Any(agent => string.Equals(
                    agent.AgentId,
                    options.FilterAgent,
                    StringComparison.OrdinalIgnoreCase)),
                cancellationToken);
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
        };

        return new InventorySnapshot
        {
            Skills = merged,
            ScannedRoots = local.Roots,
            UsedGhSkillList = usedGhList,
            CapturedAt = DateTimeOffset.UtcNow,
            Diagnostics = diagnostics,
        };
    }

    private async Task<GhCapture> CaptureGhAsync(
        string ghPath,
        string? filterScope,
        string? filterAgent,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var records = await _listAdapter
            .ListAsync(ghPath, filterScope, filterAgent, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();
        return new GhCapture(records, stopwatch.Elapsed);
    }

    private LocalCapture CaptureLocal(Options options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roots = _resolver.ResolveWithCancellation(new ScanRootResolver.Options(
            CurrentDirectory: Environment.CurrentDirectory,
            HomeDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            CustomRoots: options.ScanRoots,
            ClaudeUserConfigDir: Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")),
            cancellationToken);

        _logger.Info("inventory", $"scan roots resolved: {roots.Length}");
        var fsSw = Stopwatch.StartNew();
        var scanned = _scanner.ScanWithCancellation(
            roots,
            new LocalSkillScanner.Options(options.AllowHiddenDirs),
            cancellationToken);
        var packages = _lockReader.LoadFromRootsWithCancellation(
            roots.Select(root => root.Path),
            cancellationToken);
        fsSw.Stop();
        _logger.Info(
            "inventory",
            $"filesystem scan found {scanned.Length} skill(s) in {fsSw.ElapsedMilliseconds}ms");
        return new LocalCapture(roots, scanned, packages, fsSw.Elapsed);
    }

    private sealed record LocalCapture(
        ImmutableArray<ScanRoot> Roots,
        ImmutableArray<InstalledSkill> Scanned,
        ImmutableDictionary<string, SkillPackage> Packages,
        TimeSpan Duration);

    private sealed record GhCapture(
        ImmutableArray<GhSkillListRecord> Records,
        TimeSpan Duration);

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
        if (ghRecords.IsEmpty)
        {
            return scanned;
        }

        // Build a key→record index for the scan output.
        var scanIndex = new Dictionary<string, InstalledSkill>(StringComparer.Ordinal);
        foreach (var s in scanned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanIndex[PathIdentity.NormalizeKey(s.ResolvedPath)] = s;
        }

        var outputBuilder = ImmutableArray.CreateBuilder<InstalledSkill>(scanned.Length + ghRecords.Length);
        var matchedScanKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rec in ghRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ResolveKey(rec);
            if (key is not null && scanIndex.TryGetValue(key, out var match))
            {
                outputBuilder.Add(match with { Provenance = Provenance.Both });
                matchedScanKeys.Add(key);
            }
            else
            {
                // CLI claims this install but filesystem scan didn't see it.
                // Emit as CliList-only, with the path the CLI reported. The
                // upstream `gh skill list` shape (cli/cli#13418) makes
                // `hosts: []` always an array; we adapt that to AgentMembership
                // entries. SourceUrl lands on FrontMatter.Upstream so the UI
                // can render the source link from CliList-only records.
                var path = rec.ResolvedPath ?? rec.Path ?? string.Empty;
                outputBuilder.Add(new InstalledSkill
                {
                    Name = rec.Name ?? Path.GetFileName(path.TrimEnd('/')) ?? "(unnamed)",
                    ResolvedPath = path,
                    ScanRoot = path,
                    Scope = ParseScope(rec.Scope) ?? Scope.Custom,
                    Agents = rec.Hosts.IsDefaultOrEmpty
                        ? (rec.Agent is null
                            ? ImmutableArray<AgentMembership>.Empty
                            : ImmutableArray.Create(new AgentMembership(rec.Agent, path, rec.IsSymlink)))
                        : rec.Hosts.Select(a => new AgentMembership(a, path, rec.IsSymlink)).ToImmutableArray(),
                    FrontMatter = new SkillFrontMatter
                    {
                        Name = rec.Name,
                        Version = rec.Version,
                        Upstream = rec.SourceUrl ?? rec.Repo,
                        GithubTreeSha = rec.GithubTreeSha,
                        Pinned = rec.Pinned,
                    },
                    Validity = ValidityState.Valid,
                    Provenance = Provenance.CliList,
                    Ignored = false,
                    IsSymlinked = rec.IsSymlink,
                    InstalledAt = null,
                });
            }
        }

        foreach (var kv in scanIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matchedScanKeys.Contains(kv.Key)) continue;
            outputBuilder.Add(kv.Value);
        }

        return outputBuilder.ToImmutable();
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

    private static string? ResolveKey(GhSkillListRecord rec)
    {
        var path = rec.ResolvedPath ?? rec.Path;
        return string.IsNullOrEmpty(path) ? null : PathIdentity.NormalizeKey(path);
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
