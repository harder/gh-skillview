using System.Collections.Immutable;
using SkillView.Gh.Models;

namespace SkillView.Gh;

internal sealed class GhSkillListCache
{
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);

    internal GhSkillListCache(Func<DateTimeOffset>? now = null, TimeSpan? ttl = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _ttl = ttl ?? TimeSpan.FromSeconds(15);
    }

    internal bool TryGet(
        string ghPath,
        string? scope,
        string? agent,
        out ImmutableArray<GhSkillListRecord> records)
    {
        var key = BuildKey(ghPath, scope, agent);
        if (_entries.TryGetValue(key, out var entry) && _now() - entry.CapturedAt <= _ttl)
        {
            records = entry.Records;
            return true;
        }

        _entries.Remove(key);
        records = ImmutableArray<GhSkillListRecord>.Empty;
        return false;
    }

    internal void Store(
        string ghPath,
        string? scope,
        string? agent,
        ImmutableArray<GhSkillListRecord> records)
    {
        _entries[BuildKey(ghPath, scope, agent)] = new CacheEntry(_now(), records);
    }

    internal void Invalidate() => _entries.Clear();

    private static string BuildKey(string ghPath, string? scope, string? agent) =>
        $"{ghPath}\n{scope ?? string.Empty}\n{agent ?? string.Empty}";

    private sealed record CacheEntry(
        DateTimeOffset CapturedAt,
        ImmutableArray<GhSkillListRecord> Records);
}
