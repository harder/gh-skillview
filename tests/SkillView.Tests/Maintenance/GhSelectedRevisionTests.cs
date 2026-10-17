using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class GhSelectedRevisionTests
{
    private const string Repository = "https://github.com/harder/fixture.git";
    private static readonly string Commit = new('a', 40), Tree = new('b', 40), Other = new('c', 40);
    private readonly List<string> calls = [];
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string tagCommit = Commit;
    private GhRevisionResolver Resolver() => new((endpoint, token) =>
    {
        token.ThrowIfCancellationRequested(); calls.Add(endpoint);
        return Task.FromResult(endpoint switch
        {
            "repos/harder/fixture/git/ref/tags/release%2Fv1" => new GhApiPayload(200,
                "{\"ref\":\"refs/tags/release/v1\",\"object\":{\"sha\":\"" + tagCommit + "\",\"type\":\"commit\"}}"),
            _ when endpoint == $"repos/harder/fixture/git/commits/{Commit}" || endpoint == $"repos/harder/fixture/git/commits/{Other}"
                => new GhApiPayload(200, "{\"sha\":\"" + endpoint.Split('/')[^1] + "\",\"tree\":{\"sha\":\"" + Tree + "\"}}"),
            _ => throw new IOException("Unexpected request (no fallback permitted): " + endpoint)
        });
    });

    [Fact]
    public async Task SelectedTagRequestsOnlyExactTagAndImmutableCommit()
    {
        var request = new SelectedRevisionRequest("tag", "release/v1", true);
        var result = await Resolver().ResolveSelectedAsync(Repository, "SKILL.md", request, Token);
        Assert.Equal(new(request, Repository, "refs/tags/release/v1", Commit, Tree, ""), result);
        Assert.Equal(new[] { "repos/harder/fixture/git/ref/tags/release%2Fv1", $"repos/harder/fixture/git/commits/{Commit}" }, calls);
    }

    [Fact]
    public async Task SelectedCommitHasNoSymbolicRefOrMutableLookup()
    {
        var request = new SelectedRevisionRequest("commit", Commit.ToUpperInvariant(), false);
        var result = await Resolver().ResolveSelectedAsync(Repository, "", request, Token);
        Assert.Null(result.Ref); Assert.Equal(Commit, result.Commit); Assert.Equal(Tree, result.Tree);
        Assert.Equal(new[] { $"repos/harder/fixture/git/commits/{Commit}" }, calls);
    }

    [Fact]
    public async Task MovingTagCannotFreezeEarlierCaptureEvenWhenTreeMatches()
    {
        var resolver = Resolver();
        var frozen = await resolver.ResolveSelectedAsync(Repository, "", new("tag", "release/v1", true), Token);
        tagCommit = Other;
        var error = await Assert.ThrowsAsync<IOException>(() => resolver.VerifySelectedAsync(frozen, Token));
        Assert.Contains("moved", error.Message);
        Assert.Equal(4, calls.Count);
    }

    [Theory]
    [InlineData("branch", "main")]
    [InlineData("commit", "abc123")]
    [InlineData("commit", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("tag", "")]
    [InlineData("tag", "../main")]
    [InlineData("tag", "a//b")]
    [InlineData("tag", "a.lock")]
    [InlineData("tag", "a/.hidden")]
    [InlineData("tag", "a@{b")]
    [InlineData("tag", "a~1")]
    [InlineData("tag", "a b")]
    [InlineData("tag", "a\\b")]
    public async Task UnsupportedSelectionRefusesBeforeAnyRequest(string kind, string value)
    {
        await Assert.ThrowsAsync<IOException>(() => Resolver().ResolveSelectedAsync(Repository, "", new(kind, value, true), Token));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task MissingOrUnavailableSelectedTagNeverFallsBack(int status)
    {
        var resolver = new GhRevisionResolver((endpoint, token) =>
        { calls.Add(endpoint); return Task.FromResult(new GhApiPayload(status, "{}")); });
        await Assert.ThrowsAsync<GhSourceRequestException>(() => resolver.ResolveSelectedAsync(Repository, "", new("tag", "v1", false), Token));
        Assert.Equal(new[] { "repos/harder/fixture/git/ref/tags/v1" }, calls);
    }

    [Fact]
    public async Task CanceledSelectionDoesNotLaunchRequest()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => Resolver().ResolveSelectedAsync(Repository, "", new("commit", Commit, true), new(true)));
        Assert.Empty(calls);
    }
}
