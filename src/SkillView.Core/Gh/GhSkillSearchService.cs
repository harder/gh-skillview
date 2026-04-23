using System.Text.Json;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill search --json` (§11.2).
public sealed class GhSkillSearchService
{
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public GhSkillSearchService(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SearchResultSkill>> SearchAsync(
        string ghPath,
        string query,
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "skill", "search", query,
            "--json", "description,namespace,path,repo,skillName,stars",
            "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var result = await _runner.RunAsync(ghPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.Warn("gh.skill.search", $"exit={result.ExitCode} err={result.StdErr.Trim()}");
            return Array.Empty<SearchResultSkill>();
        }

        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            return Array.Empty<SearchResultSkill>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(
                result.StdOut,
                GhJsonContext.Default.SearchResultSkillArray);
            return parsed ?? Array.Empty<SearchResultSkill>();
        }
        catch (JsonException ex)
        {
            _logger.Error("gh.skill.search", $"JSON parse failed: {ex.Message}");
            return Array.Empty<SearchResultSkill>();
        }
    }
}
