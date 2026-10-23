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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parses_gh_2_103_expiring_token_output(bool stderr)
    {
        // gh 2.103 adds refresh and expiry details after the existing login
        // and active-account fields. Keep accepting stdout and stderr.
        const string output = """
            github.com
              ✓ Logged in to github.com account monalisa (keyring)
              - Active account: true
              - Git operations protocol: https
              - Token: gho_******
              - Expiring token that gh refreshes automatically
              - Token expires: 2026-10-23T08:00:00Z (in about 8 hours)
              - Refresh token expires: 2027-04-21T00:00:00Z (in about 6 months)
              - Token scopes: 'repo', 'read:org'
            """;

        var status = GhAuthStatusParser.Parse(stderr ? null : output, stderr ? output : null, 0);

        Assert.True(status.LoggedIn);
        Assert.Equal("github.com", status.ActiveHost);
        Assert.Equal("monalisa", status.Account);
        Assert.Equal(["github.com"], status.Hosts);
        Assert.Equal(output, status.RawOutput);
    }

    [Fact]
    public void Rejected_refresh_token_does_not_report_an_authenticated_account()
    {
        const string output = """
            github.com
              X Refresh token for github.com account monalisa was rejected
              - Active account: true
              - The stored expiring credential was removed because its refresh token is no longer valid.
              - To re-authenticate, run: gh auth login -h github.com
            """;

        var status = GhAuthStatusParser.Parse(null, output, 1);

        Assert.False(status.LoggedIn);
        Assert.Null(status.Account);
        Assert.Equal("github.com", status.ActiveHost);
        Assert.Equal(["github.com"], status.Hosts);
        Assert.Equal(output, status.RawOutput);
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
