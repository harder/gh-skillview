using System.Text.Json;
using SkillView.Gh;
using SkillView.Gh.Models;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhJsonContextTests
{
    private const string Fixture = """
        [
          {
            "description": "Render markdown",
            "namespace": "vercel-labs",
            "path": "skills/render-md",
            "repo": "vercel-labs/skills",
            "skillName": "render-md",
            "stars": 120
          },
          {
            "description": null,
            "skillName": "bare",
            "repo": "someone/other",
            "stars": 0
          }
        ]
        """;

    [Fact]
    public void ParsesSearchJsonViaSourceGenContext()
    {
        var parsed = JsonSerializer.Deserialize(Fixture, GhJsonContext.Default.SearchResultSkillArray);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Length);
        Assert.Equal("render-md", parsed[0].SkillName);
        Assert.Equal(120, parsed[0].Stars);
        Assert.Null(parsed[1].Description);
        Assert.Equal(0, parsed[1].Stars);
    }

    [Fact]
    public void HandlesEmptyArray()
    {
        var parsed = JsonSerializer.Deserialize("[]", GhJsonContext.Default.SearchResultSkillArray);
        Assert.NotNull(parsed);
        Assert.Empty(parsed!);
    }
}
