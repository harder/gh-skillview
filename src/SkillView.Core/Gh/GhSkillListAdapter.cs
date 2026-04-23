using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill list --json` (cli/cli#13215). Only called when the
/// capability probe reports `HasSkillList`. The JSON schema for the upstream
/// command is not yet frozen, so this adapter parses tolerantly via
/// `JsonDocument` (AOT-safe) and pulls values from a set of likely field
/// names. When the schema stabilizes, tighten this.
public sealed class GhSkillListAdapter
{
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public GhSkillListAdapter(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
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
            return ImmutableArray<GhSkillListRecord>.Empty;
        }

        return Parse(result.StdOut, _logger);
    }

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
        var agents = ImmutableArray.CreateBuilder<string>();
        if (obj.TryGetProperty("agents", out var agentsEl) && agentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in agentsEl.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String)
                {
                    var s = a.GetString();
                    if (!string.IsNullOrEmpty(s)) agents.Add(s);
                }
            }
        }

        return new GhSkillListRecord
        {
            Name = GetString(obj, "name", "skillName", "skill_name"),
            Path = GetString(obj, "path", "installPath", "install_path"),
            ResolvedPath = GetString(obj, "resolvedPath", "resolved_path", "canonicalPath"),
            Repo = GetString(obj, "repo", "repository"),
            Agent = GetString(obj, "agent"),
            Scope = GetString(obj, "scope"),
            Version = GetString(obj, "version", "ref"),
            GithubTreeSha = GetString(obj, "githubTreeSha", "github_tree_sha", "github-tree-sha", "treeSha", "tree_sha", "sha"),
            Pinned = GetBool(obj, "pinned", "isPinned"),
            IsSymlink = GetBool(obj, "isSymlink", "symlink", "is_symlink"),
            Agents = agents.ToImmutable(),
        };
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
