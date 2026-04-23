using SkillView.Inventory;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Inventory;

public class FrontMatterParserTests
{
    [Fact]
    public void Parses_basic_scalars_and_body()
    {
        const string input = """
            ---
            name: foo
            description: a test skill
            version: 0.1.0
            ---
            # Body
            contents here
            """;
        var (body, fm, parsed) = FrontMatterParser.Parse(input);
        Assert.True(parsed);
        Assert.Equal("foo", fm.Name);
        Assert.Equal("a test skill", fm.Description);
        Assert.Equal("0.1.0", fm.Version);
        Assert.Contains("contents here", body);
    }

    [Fact]
    public void Parses_github_tree_sha_and_pinned()
    {
        const string input = """
            ---
            name: x
            github-tree-sha: abc123def
            pinned: true
            ---
            """;
        var (_, fm, _) = FrontMatterParser.Parse(input);
        Assert.Equal("abc123def", fm.GithubTreeSha);
        Assert.True(fm.Pinned);
    }

    [Fact]
    public void Parses_quoted_strings()
    {
        const string input = """
            ---
            name: "quoted name"
            description: 'single quoted'
            ---
            """;
        var (_, fm, _) = FrontMatterParser.Parse(input);
        Assert.Equal("quoted name", fm.Name);
        Assert.Equal("single quoted", fm.Description);
    }

    [Fact]
    public void Parses_flow_array_allowed_tools()
    {
        const string input = """
            ---
            allowed-tools: [Read, Write, "Bash"]
            ---
            """;
        var (_, fm, _) = FrontMatterParser.Parse(input);
        Assert.Equal(new[] { "Read", "Write", "Bash" }, fm.AllowedTools);
    }

    [Fact]
    public void Parses_block_array_agents()
    {
        const string input = """
            ---
            agents:
              - claude
              - copilot
              - cursor
            name: x
            ---
            """;
        var (_, fm, _) = FrontMatterParser.Parse(input);
        Assert.Equal(new[] { "claude", "copilot", "cursor" }, fm.Agents);
        Assert.Equal("x", fm.Name);
    }

    [Fact]
    public void No_front_matter_returns_whole_body()
    {
        const string input = "# Just markdown\nno front matter\n";
        var (body, fm, parsed) = FrontMatterParser.Parse(input);
        Assert.False(parsed);
        Assert.Equal(input, body);
        Assert.Same(SkillFrontMatter.Empty, fm);
    }

    [Fact]
    public void Unclosed_fence_is_not_parsed()
    {
        const string input = "---\nname: x\nno closing fence\n";
        var (_, _, parsed) = FrontMatterParser.Parse(input);
        Assert.False(parsed);
    }

    [Fact]
    public void Unknown_keys_land_in_extra()
    {
        const string input = """
            ---
            name: x
            author: someone
            ---
            """;
        var (_, fm, _) = FrontMatterParser.Parse(input);
        Assert.Equal("someone", fm.Extra["author"]);
    }
}
