using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhSkillListAdapterTests
{
    [Fact]
    public void Parses_top_level_array()
    {
        const string json = """
            [
              { "name": "foo", "path": "/a/foo", "repo": "o/r", "pinned": true },
              { "name": "bar", "path": "/a/bar", "repo": "o/r2" }
            ]
            """;
        var records = GhSkillListAdapter.Parse(json);
        Assert.Equal(2, records.Length);
        Assert.Equal("foo", records[0].Name);
        Assert.True(records[0].Pinned);
        Assert.Equal("/a/foo", records[0].Path);
    }

    [Fact]
    public void Parses_object_wrapped_array()
    {
        const string json = """
            { "skills": [ { "name": "foo", "path": "/a/foo" } ] }
            """;
        var records = GhSkillListAdapter.Parse(json);
        Assert.Single(records);
        Assert.Equal("foo", records[0].Name);
    }

    [Fact]
    public void Accepts_multiple_alternative_field_names_for_sha()
    {
        const string json = """
            [
              { "name": "a", "github-tree-sha": "sha1" },
              { "name": "b", "treeSha": "sha2" },
              { "name": "c", "sha": "sha3" }
            ]
            """;
        var records = GhSkillListAdapter.Parse(json);
        Assert.Equal("sha1", records[0].GithubTreeSha);
        Assert.Equal("sha2", records[1].GithubTreeSha);
        Assert.Equal("sha3", records[2].GithubTreeSha);
    }

    [Fact]
    public void Parses_agents_array()
    {
        const string json = """
            [ { "name": "x", "agents": ["claude", "copilot"] } ]
            """;
        var records = GhSkillListAdapter.Parse(json);
        Assert.Equal(new[] { "claude", "copilot" }, records[0].Agents);
    }

    [Fact]
    public void Empty_or_garbage_yields_empty_array()
    {
        Assert.Empty(GhSkillListAdapter.Parse(""));
        Assert.Empty(GhSkillListAdapter.Parse("not json"));
        Assert.Empty(GhSkillListAdapter.Parse("42"));
    }
}
