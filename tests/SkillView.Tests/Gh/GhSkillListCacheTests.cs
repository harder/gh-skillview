using System.Collections.Immutable;
using SkillView.Gh;
using SkillView.Gh.Models;
using Xunit;

namespace SkillView.Tests.Gh;

public sealed class GhSkillListCacheTests
{
    [Fact]
    public void TryGet_ReturnsStoredEntry_BeforeExpiry()
    {
        var now = DateTimeOffset.Parse("2026-05-01T00:00:00Z");
        var cache = new GhSkillListCache(() => now, ttl: TimeSpan.FromSeconds(10));
        var records = ImmutableArray.Create(new GhSkillListRecord { Name = "demo" });

        cache.Store("/usr/bin/gh", scope: "user", agent: "claude-code", records);

        var hit = cache.TryGet("/usr/bin/gh", "user", "claude-code", out var cached);

        Assert.True(hit);
        Assert.Equal(records, cached);
    }

    [Fact]
    public void TryGet_MissesAfterExpiry()
    {
        var now = DateTimeOffset.Parse("2026-05-01T00:00:00Z");
        var cache = new GhSkillListCache(() => now, ttl: TimeSpan.FromSeconds(10));
        cache.Store("/usr/bin/gh", scope: null, agent: null, ImmutableArray<GhSkillListRecord>.Empty);

        now = now.AddSeconds(11);

        Assert.False(cache.TryGet("/usr/bin/gh", null, null, out _));
    }

    [Fact]
    public void Invalidate_RemovesStoredEntries()
    {
        var cache = new GhSkillListCache(() => DateTimeOffset.UtcNow, ttl: TimeSpan.FromSeconds(10));
        cache.Store("/usr/bin/gh", scope: null, agent: null, ImmutableArray.Create(new GhSkillListRecord { Name = "demo" }));

        cache.Invalidate();

        Assert.False(cache.TryGet("/usr/bin/gh", null, null, out _));
    }
}
