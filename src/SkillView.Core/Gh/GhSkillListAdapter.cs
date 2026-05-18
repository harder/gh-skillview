using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill list --json` (cli/cli#13245, implemented in PR #13418).
/// Only called when the capability probe reports `HasSkillList`. The
/// upstream-canonical JSON fields are read first; legacy / alternate field
/// names are kept as defensive fallbacks in case the schema shifts before
/// the PR merges. All parsing goes through `JsonDocument` (AOT-safe).
///
/// Canonical upstream shape per the PR:
///   { skillName, hosts:[], scope, sourceURL, version, pinned, path }
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
        CapabilityProfile capabilities,
        string? scope = null,
        string? agent = null,
        CancellationToken cancellationToken = default)
    {
        if (!capabilities.HasSkillList)
        {
            return ImmutableArray<GhSkillListRecord>.Empty;
        }

        if (_cache.TryGet(ghPath, scope, agent, out var cached))
        {
            _logger.Debug("gh.skill.list", $"cache hit scope={scope ?? "(any)"} agent={agent ?? "(any)"} count={cached.Length}");
            return cached;
        }

        var args = new List<string> { "skill", "list", "--json" };
        if (!string.IsNullOrEmpty(scope) && capabilities.SupportsListScope)
        {
            args.Add("--scope");
            args.Add(scope);
        }
        if (!string.IsNullOrEmpty(agent) && capabilities.SupportsListAgent)
        {
            args.Add("--agent");
            args.Add(agent);
        }

        var result = await _runner.RunAsync(ghPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.Warn("gh.skill.list", $"exit={result.ExitCode} err={result.StdErr.Trim()}");
            return ImmutableArray<GhSkillListRecord>.Empty;
        }

        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            _cache.Store(ghPath, scope, agent, ImmutableArray<GhSkillListRecord>.Empty);
            return ImmutableArray<GhSkillListRecord>.Empty;
        }

        var parsed = Parse(result.StdOut, _logger);
        _cache.Store(ghPath, scope, agent, parsed);
        return parsed;
    }

    public void Invalidate() => _cache.Invalidate();

    /// Parses a JSON payload into `GhSkillListRecord`s. Accepts either a top-
    /// level array or a top-level object with a records array under one of
    /// several common field names.
    public static ImmutableArray<GhSkillListRecord> Parse(string json, Logger? logger = null)
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
                return ImmutableArray<GhSkillListRecord>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<GhSkillListRecord>();
            foreach (var el in array.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                builder.Add(ReadRecord(el));
            }
            return builder.ToImmutable();
        }
        catch (JsonException ex)
        {
            logger?.Error("gh.skill.list", $"JSON parse failed: {ex.Message}");
            return ImmutableArray<GhSkillListRecord>.Empty;
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
        // Upstream emits the agent list under `hosts` (always an array, even
        // for single-agent installs and empty for --dir scans). Older /
        // alternate payloads used `agents`; read both, prefer `hosts`.
        var hosts = ReadStringArray(obj, "hosts", "agents");

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
