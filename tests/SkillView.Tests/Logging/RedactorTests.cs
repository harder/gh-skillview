using SkillView.Logging;
using Xunit;

namespace SkillView.Tests.Logging;

public class RedactorTests
{
    [Theory]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123")]
    [InlineData("gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123")]
    [InlineData("ghu_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123")]
    [InlineData("ghs_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123")]
    public void RedactsGhTokens(string token)
    {
        var input = $"using token {token} now";
        var output = Redactor.Redact(input);
        Assert.DoesNotContain(token, output);
        Assert.Contains("[REDACTED]", output);
    }

    [Fact]
    public void RedactsFineGrainedPat()
    {
        var pat = "github_pat_11ABCDEFG0_abcdefghijklmno12345678";
        var output = Redactor.Redact($"token={pat}");
        Assert.DoesNotContain(pat, output);
    }

    [Fact]
    public void RedactsAuthorizationHeader()
    {
        var input = "Authorization: Bearer secret-xyz-123";
        var output = Redactor.Redact(input);
        Assert.DoesNotContain("secret-xyz-123", output);
        Assert.StartsWith("Authorization:", output);
    }

    [Fact]
    public void RedactsUrlUserInfo()
    {
        var input = "clone https://alice:hunter2@github.com/example.git";
        var output = Redactor.Redact(input);
        Assert.DoesNotContain("hunter2", output);
        Assert.Contains("github.com/example.git", output);
    }

    [Fact]
    public void LeavesCleanInputUnchanged()
    {
        const string input = "nothing to redact here";
        Assert.Equal(input, Redactor.Redact(input));
    }
}
