using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Inventory;

namespace SkillView.Ui;

internal sealed class SearchAgentMetadataCache
{
    internal const int DefaultCapacity = 512;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _agentsByResult =
        new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();

    internal SearchAgentMetadataCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

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

    internal bool Has(SearchResultSkill result)
    {
        lock (_gate)
        {
            if (!_agentsByResult.TryGetValue(BuildKey(result), out var node)) return false;
            Touch(node);
            return true;
        }
    }

    internal void Store(SearchResultSkill result, ImmutableArray<string> agents)
    {
        var key = BuildKey(result);
        lock (_gate)
        {
            if (_agentsByResult.TryGetValue(key, out var existing))
            {
                existing.Value = existing.Value with { Agents = agents };
                Touch(existing);
                return;
            }

            var node = _lru.AddFirst(new CacheEntry(key, agents));
            _agentsByResult.Add(key, node);
            if (_agentsByResult.Count <= _capacity) return;

            var expired = _lru.Last!;
            _lru.RemoveLast();
            _agentsByResult.Remove(expired.Value.Key);
        }
    }

    internal IReadOnlyList<SearchResultSkill> Filter(
        IReadOnlyList<SearchResultSkill> results,
        string? requestedAgent)
    {
        var normalized = NormalizeAgent(requestedAgent);
        if (normalized is null)
        {
            return results;
        }

        lock (_gate)
        {
            return results
                .Where(result =>
                    _agentsByResult.TryGetValue(BuildKey(result), out var node)
                    && node.Value.Agents.Any(agent => string.Equals(agent, normalized, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
    }

    internal int CountForTests
    {
        get { lock (_gate) return _agentsByResult.Count; }
    }

    private void Touch(LinkedListNode<CacheEntry> node)
    {
        _lru.Remove(node);
        _lru.AddFirst(node);
    }

    private static string BuildKey(SearchResultSkill result) =>
        $"{result.Repo ?? string.Empty}\n{result.SkillName ?? string.Empty}\n{result.Path ?? string.Empty}";

    private sealed record CacheEntry(string Key, ImmutableArray<string> Agents);
}
