using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Inventory;

namespace SkillView.Ui;

internal sealed class SearchAgentMetadataCache
{
    private readonly Dictionary<string, ImmutableArray<string>> _agentsByResult =
        new(StringComparer.Ordinal);

    internal static string? NormalizeAgent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        foreach (var entry in InstallAgentCatalog.Entries)
        {
            if (string.Equals(trimmed, entry.GhId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, entry.AgentHint, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, entry.Label, StringComparison.OrdinalIgnoreCase))
            {
                return entry.GhId;
            }
        }

        return trimmed.ToLowerInvariant();
    }

    internal static ImmutableArray<string> ExtractAgentsFromMarkdown(string markdown)
    {
        var (_, frontMatter, _) = FrontMatterParser.Parse(markdown);
        if (frontMatter.Agents.IsDefaultOrEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in frontMatter.Agents)
        {
            var normalized = NormalizeAgent(agent);
            if (normalized is null || !seen.Add(normalized))
            {
                continue;
            }

            builder.Add(normalized);
        }

        return builder.ToImmutable();
    }

    internal bool Has(SearchResultSkill result) => _agentsByResult.ContainsKey(BuildKey(result));

    internal void Store(SearchResultSkill result, ImmutableArray<string> agents) =>
        _agentsByResult[BuildKey(result)] = agents;

    internal IReadOnlyList<SearchResultSkill> Filter(
        IReadOnlyList<SearchResultSkill> results,
        string? requestedAgent)
    {
        var normalized = NormalizeAgent(requestedAgent);
        if (normalized is null)
        {
            return results;
        }

        return results
            .Where(result =>
                _agentsByResult.TryGetValue(BuildKey(result), out var agents)
                && agents.Any(agent => string.Equals(agent, normalized, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static string BuildKey(SearchResultSkill result) =>
        $"{result.Repo ?? string.Empty}\n{result.SkillName ?? string.Empty}\n{result.Path ?? string.Empty}";
}
