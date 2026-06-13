using System.Globalization;
using System.Text.Json;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill search --json`. SkillView requires gh ≥ 2.94.0, which
/// exposes `--json`, `--owner`, and `--page` (it paginates rather than the
/// pre-2.94 `--limit`). `--owner`/`--page` emit unconditionally; `Limit` is
/// applied client-side as a result cap.
public sealed class GhSkillSearchService
{
    public const int DefaultLimit = 30;
    public const int MaxLimit = 200;

    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public GhSkillSearchService(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public sealed record Options(
        string? Owner = null,
        int Limit = DefaultLimit,
        int Page = 1);

    public async Task<SearchResponse> SearchAsync(
        string ghPath,
        string query,
        Options? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new Options();
        var limit = Math.Clamp(options.Limit, 1, MaxLimit);

        var args = BuildArgs(query, options.Owner, options.Page);
        var result = await _runner.RunAsync(ghPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.Warn("gh.skill.search", $"exit={result.ExitCode} err={result.StdErr.Trim()}");
            return new SearchResponse(Array.Empty<SearchResultSkill>(), result.ExitCode, result.StdErr.Trim());
        }

        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            return new SearchResponse(Array.Empty<SearchResultSkill>(), 0, null);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(
                result.StdOut,
                GhJsonContext.Default.SearchResultSkillArray) ?? Array.Empty<SearchResultSkill>();
            // gh 2.94 search has no result-count flag; cap client-side.
            IReadOnlyList<SearchResultSkill> capped =
                parsed.Length > limit ? parsed.Take(limit).ToArray() : parsed;
            return new SearchResponse(capped, 0, null);
        }
        catch (JsonException ex)
        {
            _logger.Error("gh.skill.search", $"JSON parse failed: {ex.Message}");
            return new SearchResponse(Array.Empty<SearchResultSkill>(), -1, $"JSON parse failed: {ex.Message}");
        }
    }

    // Backwards-compatible thin overload — the Phase 0 shell wires this shape.
    public async Task<IReadOnlyList<SearchResultSkill>> SearchAsync(
        string ghPath,
        string query,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var response = await SearchAsync(
            ghPath,
            query,
            new Options(Limit: limit),
            cancellationToken).ConfigureAwait(false);
        return response.Results;
    }

    internal static IReadOnlyList<string> BuildArgs(string query, string? owner, int page)
    {
        var args = new List<string>
        {
            "skill", "search", query,
            "--json", "description,namespace,path,repo,skillName,stars",
        };

        if (!string.IsNullOrWhiteSpace(owner))
        {
            args.Add("--owner");
            args.Add(owner);
        }

        if (page > 1)
        {
            args.Add("--page");
            args.Add(page.ToString(CultureInfo.InvariantCulture));
        }

        return args;
    }
}

public sealed record SearchResponse(
    IReadOnlyList<SearchResultSkill> Results,
    int ExitCode,
    string? ErrorMessage)
{
    public bool Succeeded => ExitCode == 0;
}
