using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill list --json` (shipped in gh 2.94.0, cli/cli#13418, the
/// required minimum). This is SkillView's primary inventory source; the
/// filesystem scan supplements it (see <see cref="Inventory.LocalInventoryService"/>).
/// `gh` requires an explicit comma-separated field list after `--json`; the
/// shipped fields are read first and legacy / alternate names are kept as
/// defensive fallbacks against schema drift. All parsing goes through
/// `JsonDocument` (AOT-safe).
///
/// Shipped shape (gh 2.94.0):
///   { skillName, agentHosts:[], scope, sourceURL, version, pinned, path }
public sealed class GhSkillListAdapter
{
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;
    private readonly GhSkillListCache _cache;

    public GhSkillListAdapter(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
        _cache = new GhSkillListCache();
    }

    public async Task<ImmutableArray<GhSkillListRecord>> ListAsync(
        string ghPath,
        string? scope = null,
        string? agent = null,
        CancellationToken cancellationToken = default)
    {
        var lookup = await _cache.GetOrLoadAsync(
                ghPath,
                scope,
                agent,
                token => LoadAsync(ghPath, scope, agent, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (lookup.FromCache)
        {
            _logger.Debug("gh.skill.list", $"cache hit scope={scope ?? "(any)"} agent={agent ?? "(any)"} count={lookup.Records.Length}");
        }
        return lookup.Records;
    }

    private async Task<GhSkillListCache.LoadResult> LoadAsync(
        string ghPath,
        string? scope,
        string? agent,
        CancellationToken cancellationToken)
    {
        // gh requires an explicit comma-separated field list after `--json`;
        // bare `--json` errors out listing the available fields. These are the
        // fields shipped by gh 2.94.0 (cli/cli#13418). The adapter's parser is
        // tolerant of extra/missing keys, so widening this list later is safe.
        var args = new List<string>
        {
            "skill", "list",
            "--json", "skillName,agentHosts,path,pinned,scope,sourceURL,version",
        };
        if (!string.IsNullOrEmpty(scope))
        {
            args.Add("--scope");
            args.Add(scope);
        }
        if (!string.IsNullOrEmpty(agent))
        {
            args.Add("--agent");
            args.Add(agent);
        }

        var result = await _runner.RunAsync(ghPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.Warn("gh.skill.list", $"exit={result.ExitCode} err={Logger.ErrorSnippet(result.StdErr)}");
            return new GhSkillListCache.LoadResult(
                ImmutableArray<GhSkillListRecord>.Empty,
                ShouldCache: false);
        }

        return ParseLoadResult(result.StdOut, _logger);
    }

    public void Invalidate() => _cache.Invalidate();

    /// Parses a JSON payload into `GhSkillListRecord`s. Accepts either a top-
    /// level array or a top-level object with a records array under one of
    /// several common field names.
    public static ImmutableArray<GhSkillListRecord> Parse(string json, Logger? logger = null)
    {
        _ = TryParse(json, out var records, logger);
        return records;
    }

    internal static GhSkillListCache.LoadResult ParseLoadResult(string json, Logger? logger = null)
    {
        var succeeded = TryParse(json, out var records, logger);
        return new GhSkillListCache.LoadResult(records, ShouldCache: succeeded);
    }

    internal static bool TryParse(
        string json,
        out ImmutableArray<GhSkillListRecord> records,
        Logger? logger = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     TryGetArrayProperty(root, out array,
                         "skills", "installed", "records", "items", "results"))
            {
                // ok
            }
            else
            {
                logger?.Warn("gh.skill.list", $"unexpected JSON root kind {root.ValueKind}");
                records = ImmutableArray<GhSkillListRecord>.Empty;
                return false;
            }

            var builder = ImmutableArray.CreateBuilder<GhSkillListRecord>();
            foreach (var el in array.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    logger?.Warn("gh.skill.list", "unexpected non-object inventory record");
                    records = ImmutableArray<GhSkillListRecord>.Empty;
                    return false;
                }

                var record = ReadRecord(el);
                if (string.IsNullOrWhiteSpace(record.Name))
                {
                    logger?.Warn("gh.skill.list", "inventory record is missing skillName");
                    records = ImmutableArray<GhSkillListRecord>.Empty;
                    return false;
                }
                builder.Add(record);
            }
            records = builder.ToImmutable();
            return true;
        }
        catch (JsonException ex)
        {
            logger?.Error("gh.skill.list", $"JSON parse failed: {ex.Message}");
            records = ImmutableArray<GhSkillListRecord>.Empty;
            return false;
        }
    }

    private static bool TryGetArrayProperty(JsonElement obj, out JsonElement array, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array)
            {
                array = el;
                return true;
            }
        }
        array = default;
        return false;
    }

    private static GhSkillListRecord ReadRecord(JsonElement obj)
    {
        // gh 2.94.0 emits the agent list under `agentHosts` (always an array,
        // even for single-agent installs and empty for --dir scans). Pre-release
        // / alternate payloads used `hosts` or `agents`; read all three, prefer
        // the shipped `agentHosts`.
        var hosts = ReadStringArray(obj, "agentHosts", "hosts", "agents");

        var sourceUrl = GetString(obj, "sourceURL", "source_url");
        var repo = GetString(obj, "repo", "repository");

        return new GhSkillListRecord
        {
            // skillName is the upstream-canonical field; legacy `name` /
            // `skill_name` payloads still parse.
            Name = GetString(obj, "skillName", "name", "skill_name"),
            // path is upstream-canonical; the older keys stay as fallbacks
            // (some early SkillView log fixtures used installPath).
            Path = GetString(obj, "path", "installPath", "install_path"),
            ResolvedPath = GetString(obj, "resolvedPath", "resolved_path", "canonicalPath"),
            SourceUrl = sourceUrl,
            Repo = repo,
            Agent = GetString(obj, "agent"),
            Scope = GetString(obj, "scope"),
            Version = GetString(obj, "version", "ref"),
            // Upstream `gh skill list` doesn't emit tree-sha — keep the
            // fallback keys for parity with payloads that include it.
            GithubTreeSha = GetString(obj, "githubTreeSha", "github_tree_sha", "github-tree-sha", "treeSha", "tree_sha", "sha"),
            Pinned = GetBool(obj, "pinned", "isPinned"),
            // Upstream doesn't emit isSymlink — filesystem scan resolves it.
            // Legacy payloads can still feed the field for testing.
            IsSymlink = GetBool(obj, "isSymlink", "symlink", "is_symlink"),
            Hosts = hosts,
        };
    }

    private static ImmutableArray<string> ReadStringArray(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var builder = ImmutableArray.CreateBuilder<string>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrEmpty(s)) builder.Add(s);
                }
            }
            return builder.ToImmutable();
        }
        return ImmutableArray<string>.Empty;
    }

    private static string? GetString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var el))
            {
                return el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Number => el.GetRawText(),
                    _ => null,
                };
            }
        }
        return null;
    }

    private static bool GetBool(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el)) continue;
            switch (el.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String:
                    var s = el.GetString();
                    return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
                case JsonValueKind.Number:
                    return el.TryGetInt32(out var n) && n != 0;
            }
        }
        return false;
    }
}
