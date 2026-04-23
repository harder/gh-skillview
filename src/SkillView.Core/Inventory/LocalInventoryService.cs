using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;

namespace SkillView.Inventory;

/// Merges `gh skill list` records (when available) with the filesystem scan
/// by resolved path. Preferred inventory source wins (§6.2); filesystem scan
/// is always consulted so the reconciliation surfaces `FsScan`-only orphans
/// and anomalies (§5.2, §10.4).
public sealed class LocalInventoryService
{
    private readonly ScanRootResolver _resolver;
    private readonly LocalSkillScanner _scanner;
    private readonly GhSkillListAdapter _listAdapter;
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
        _logger = logger;
    }

    public sealed record Options(
        IReadOnlyList<string> ScanRoots,
        bool AllowHiddenDirs,
        string? FilterScope = null,
        string? FilterAgent = null);

    public async Task<InventorySnapshot> CaptureAsync(
        string? ghPath,
        CapabilityProfile capabilities,
        Options options,
        CancellationToken cancellationToken = default)
    {
        var roots = _resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: Environment.CurrentDirectory,
            HomeDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            CustomRoots: options.ScanRoots));

        _logger.Info("inventory", $"scan roots resolved: {roots.Length}");

        var fsSw = Stopwatch.StartNew();
        var scanned = _scanner.Scan(roots, new LocalSkillScanner.Options(options.AllowHiddenDirs));
        fsSw.Stop();
        _logger.Info("inventory", $"filesystem scan found {scanned.Length} skill(s) in {fsSw.ElapsedMilliseconds}ms");

        var usedGhList = false;
        ImmutableArray<GhSkillListRecord> ghRecords = ImmutableArray<GhSkillListRecord>.Empty;
        var ghSw = Stopwatch.StartNew();
        if (ghPath is not null && capabilities.HasSkillList)
        {
            ghRecords = await _listAdapter
                .ListAsync(ghPath, capabilities, options.FilterScope, options.FilterAgent, cancellationToken)
                .ConfigureAwait(false);
            usedGhList = true;
            _logger.Info("inventory", $"gh skill list returned {ghRecords.Length} record(s)");
        }
        ghSw.Stop();

        var merged = Merge(scanned, ghRecords);

        if (!string.IsNullOrEmpty(options.FilterScope))
        {
            var wanted = ParseScope(options.FilterScope);
            if (wanted is not null)
            {
                merged = merged.Where(s => s.Scope == wanted).ToImmutableArray();
            }
        }
        if (!string.IsNullOrEmpty(options.FilterAgent))
        {
            merged = merged
                .Where(s => s.Agents.Any(a => string.Equals(a.AgentId, options.FilterAgent, StringComparison.OrdinalIgnoreCase)))
                .ToImmutableArray();
        }

        // Collect diagnostics from the scan pass.
        var brokenCount = merged.Count(s => s.Validity == ValidityState.BrokenSymlink);
        var diagnostics = new ScanDiagnostics
        {
            FsScanDuration = fsSw.Elapsed,
            GhListDuration = usedGhList ? ghSw.Elapsed : TimeSpan.Zero,
            BrokenSymlinksFound = brokenCount,
        };

        return new InventorySnapshot
        {
            Skills = merged,
            ScannedRoots = roots,
            UsedGhSkillList = usedGhList,
            CapturedAt = DateTimeOffset.UtcNow,
            Diagnostics = diagnostics,
        };
    }

    internal static ImmutableArray<InstalledSkill> Merge(
        ImmutableArray<InstalledSkill> scanned,
        ImmutableArray<GhSkillListRecord> ghRecords)
    {
        if (ghRecords.IsEmpty)
        {
            return scanned;
        }

        // Build a key→record index for the scan output.
        var scanIndex = new Dictionary<string, InstalledSkill>(StringComparer.Ordinal);
        foreach (var s in scanned)
        {
            scanIndex[PathResolver.Normalize(s.ResolvedPath)] = s;
        }

        var outputBuilder = ImmutableArray.CreateBuilder<InstalledSkill>(scanned.Length + ghRecords.Length);
        var matchedScanKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rec in ghRecords)
        {
            var key = ResolveKey(rec);
            if (key is not null && scanIndex.TryGetValue(key, out var match))
            {
                outputBuilder.Add(match with { Provenance = Provenance.Both });
                matchedScanKeys.Add(key);
            }
            else
            {
                // CLI claims this install but filesystem scan didn't see it.
                // Emit as CliList-only, with the path the CLI reported.
                var path = rec.ResolvedPath ?? rec.Path ?? string.Empty;
                outputBuilder.Add(new InstalledSkill
                {
                    Name = rec.Name ?? Path.GetFileName(path.TrimEnd('/')) ?? "(unnamed)",
                    ResolvedPath = path,
                    ScanRoot = path,
                    Scope = ParseScope(rec.Scope) ?? Scope.Custom,
                    Agents = rec.Agents.IsDefaultOrEmpty
                        ? (rec.Agent is null
                            ? ImmutableArray<AgentMembership>.Empty
                            : ImmutableArray.Create(new AgentMembership(rec.Agent, path, rec.IsSymlink)))
                        : rec.Agents.Select(a => new AgentMembership(a, path, rec.IsSymlink)).ToImmutableArray(),
                    FrontMatter = new SkillFrontMatter
                    {
                        Name = rec.Name,
                        Version = rec.Version,
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
            if (matchedScanKeys.Contains(kv.Key)) continue;
            outputBuilder.Add(kv.Value);
        }

        return outputBuilder.ToImmutable();
    }

    private static string? ResolveKey(GhSkillListRecord rec)
    {
        var path = rec.ResolvedPath ?? rec.Path;
        return string.IsNullOrEmpty(path) ? null : PathResolver.Normalize(path);
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
