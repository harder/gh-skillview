using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhAuthStatusParserTests
{
    [Fact]
    public void Parses_modern_logged_in_output()
    {
        const string stderr = """
            github.com
              ✓ Logged in to github.com account kevinharder (keyring)
              - Active account: true
              - Git operations protocol: https
              - Token: gho_************
            """;
        var status = GhAuthStatusParser.Parse(string.Empty, stderr, 0);
        Assert.True(status.LoggedIn);
        Assert.Equal("github.com", status.ActiveHost);
        Assert.Equal("kevinharder", status.Account);
        Assert.Contains("github.com", status.Hosts);
    }

    [Fact]
    public void Parses_legacy_as_form()
    {
        const string stderr = """
            github.com
              ✓ Logged in to github.com as someuser (keyring)
              ✓ Git operations for github.com configured to use https protocol.
            """;
        var status = GhAuthStatusParser.Parse(string.Empty, stderr, 0);
        Assert.True(status.LoggedIn);
        Assert.Equal("someuser", status.Account);
        Assert.Equal("github.com", status.ActiveHost);
    }

    [Fact]
    public void Handles_not_logged_in()
    {
        const string stderr = "You are not logged into any GitHub hosts. Run gh auth login to authenticate.";
        var status = GhAuthStatusParser.Parse(string.Empty, stderr, 1);
        Assert.False(status.LoggedIn);
        Assert.Null(status.Account);
    }

    [Fact]
    public void Multi_host_picks_active_account_true()
    {
        const string stderr = """
            github.com
              ✓ Logged in to github.com account alice (keyring)
              - Active account: true
            enterprise.example.com
              ✓ Logged in to enterprise.example.com account bob (keyring)
            """;
        var status = GhAuthStatusParser.Parse(string.Empty, stderr, 0);
        Assert.True(status.LoggedIn);
        Assert.Equal("alice", status.Account);
        Assert.Equal("github.com", status.ActiveHost);
        Assert.Equal(2, status.Hosts.Length);
    }

    [Fact]
    public void Empty_output_yields_unknown_shape()
    {
        var status = GhAuthStatusParser.Parse(null, null, 1);
        Assert.False(status.LoggedIn);
        Assert.Null(status.ActiveHost);
        Assert.NotNull(status.RawOutput);
    }
}
