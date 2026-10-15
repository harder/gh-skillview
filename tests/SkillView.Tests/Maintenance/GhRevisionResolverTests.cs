using System.Text.Json;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class GhRevisionResolverTests
{
    private const string Repository = "https://github.com/harder/fixture.git";
    private static readonly string Commit = new('a', 40), RootTree = new('b', 40), SkillsTree = new('c', 40), SkillTree = new('d', 40), Tag = new('e', 40);
    private readonly List<string> calls = [];
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly Dictionary<string, GhApiPayload> responses = new()
    {
        ["repos/harder/fixture/releases/latest"] = new(200, "{\"tag_name\":\"v2\"}"),
        ["repos/harder/fixture/git/ref/tags/v2"] = new(200, Ref("tags/v2", Commit, "commit")),
        [$"repos/harder/fixture/git/commits/{Commit}"] = new(200, "{\"sha\":\"" + Commit + "\",\"tree\":{\"sha\":\"" + RootTree + "\"}}"),
        [$"repos/harder/fixture/git/trees/{RootTree}"] = new(200, Tree(RootTree, "skills", SkillsTree)),
        [$"repos/harder/fixture/git/trees/{SkillsTree}"] = new(200, Tree(SkillsTree, "demo", SkillTree)),
        ["repos/harder/fixture"] = new(200, "{\"default_branch\":\"main\"}"),
        ["repos/harder/fixture/git/ref/heads/main"] = new(200, Ref("heads/main", Commit, "commit"))
    };
    private GhRevisionResolver Resolver() => new((endpoint, token) =>
    {
        token.ThrowIfCancellationRequested(); calls.Add(endpoint);
        return Task.FromResult(responses.TryGetValue(endpoint, out var response) ? response : throw new IOException("Unexpected request: " + endpoint));
    });
    private static string Ref(string name, string id, string type) => "{\"ref\":\"refs/" + name + "\",\"object\":{\"sha\":\"" + id + "\",\"type\":\"" + type + "\"}}";
    private static string Tree(string id, string name, string child) => "{\"sha\":\"" + id + "\",\"truncated\":false,\"tree\":[{\"path\":\"" + name + "\",\"type\":\"tree\",\"mode\":\"040000\",\"sha\":\"" + child + "\"}]}";

    [Theory]
    [InlineData("skills/demo")]
    [InlineData("skills/demo/")]
    [InlineData("skills/demo/SKILL.md")]
    public async Task LatestReleaseResolvesOnceThenWalksOnlyImmutableTrees(string path)
    {
        var result = await Resolver().ResolveLatestAsync(Repository, path, Token);
        Assert.Equal(new(Repository, "refs/tags/v2", Commit, SkillTree, "skills/demo"), result);
        Assert.Equal(new[] { "repos/harder/fixture/releases/latest", "repos/harder/fixture/git/ref/tags/v2",
            $"repos/harder/fixture/git/commits/{Commit}", $"repos/harder/fixture/git/trees/{RootTree}", $"repos/harder/fixture/git/trees/{SkillsTree}" }, calls);
    }

    [Theory]
    [InlineData(404, "{}")]
    [InlineData(200, "{\"tag_name\":\"\"}")]
    [InlineData(200, "{\"tag_name\":null}")]
    [InlineData(200, "{}")]
    public async Task GenuineNoReleaseFallsBackToObservedDefaultBranch(int status, string body)
    {
        responses[callsLatest] = new(status, body);
        var result = await Resolver().ResolveLatestAsync(Repository, "skills/demo", Token);
        Assert.Equal("refs/heads/main", result.Ref); Assert.Equal(Commit, result.Commit);
        Assert.Contains("repos/harder/fixture", calls); Assert.DoesNotContain("repos/harder/fixture/git/ref/tags/v2", calls);
    }
    private const string callsLatest = "repos/harder/fixture/releases/latest";

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task AuthenticationRateLimitAndServerFailureNeverFallBack(int status)
    {
        responses[callsLatest] = new(status, "{}");
        await Assert.ThrowsAsync<IOException>(() => Resolver().ResolveLatestAsync(Repository, "skills/demo", Token));
        Assert.Equal(new[] { callsLatest }, calls);
    }

    [Fact]
    public async Task MissingTagDoesNotFallBackAfterReleaseWasSelected()
    {
        responses["repos/harder/fixture/git/ref/tags/v2"] = new(404, "{}");
        await Assert.ThrowsAsync<IOException>(() => Resolver().ResolveLatestAsync(Repository, "skills/demo", Token));
        Assert.DoesNotContain("repos/harder/fixture", calls);
    }

    [Fact]
    public async Task AnnotatedTagIsDereferencedToCommitBeforeSubpathLookup()
    {
        responses["repos/harder/fixture/git/ref/tags/v2"] = new(200, Ref("tags/v2", Tag, "tag"));
        responses[$"repos/harder/fixture/git/tags/{Tag}"] = new(200, "{\"sha\":\"" + Tag + "\",\"object\":{\"sha\":\"" + Commit + "\",\"type\":\"commit\"}}");
        var result = await Resolver().ResolveLatestAsync(Repository, "skills/demo", Token);
        Assert.Equal(Commit, result.Commit); Assert.Contains($"repos/harder/fixture/git/tags/{Tag}", calls);
    }

    [Fact]
    public async Task AnnotatedTagCycleIsBounded()
    {
        responses["repos/harder/fixture/git/ref/tags/v2"] = new(200, Ref("tags/v2", Tag, "tag"));
        responses[$"repos/harder/fixture/git/tags/{Tag}"] = new(200, "{\"sha\":\"" + Tag + "\",\"object\":{\"sha\":\"" + Tag + "\",\"type\":\"tag\"}}");
        await Assert.ThrowsAsync<IOException>(() => Resolver().ResolveLatestAsync(Repository, "skills/demo", Token));
        Assert.Equal(10, calls.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/skills/demo")]
    [InlineData("skills/../demo")]
    [InlineData("skills\\demo")]
    [InlineData("skills/./demo")]
    public async Task UnobservedOrUnsafePathRefusesBeforeAnyRequest(string? path)
    {
        await Assert.ThrowsAsync<IOException>(() => Resolver().ResolveLatestAsync(Repository, path, Token));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("SKILL.md")]
    public async Task ObservedRepositoryRootNeedsNoSubpathLookup(string path)
    {
        var result = await Resolver().ResolveLatestAsync(Repository, path, Token);
        Assert.Equal(RootTree, result.Tree); Assert.Equal("", result.Subpath); Assert.Equal(3, calls.Count);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("missing")]
    [InlineData("blob")]
    [InlineData("identity")]
    [InlineData("duplicate")]
    public async Task AmbiguousOrIncompleteSubpathRefuses(string kind)
    {
        var body = responses[$"repos/harder/fixture/git/trees/{RootTree}"].Body;
        body = kind switch
        {
            "truncated" => body.Replace("false", "true"),
            "missing" => body.Replace("skills", "different"),
            "blob" => body.Replace("\"type\":\"tree\"", "\"type\":\"blob\""),
            "identity" => body.Replace(RootTree, Tag),
            _ => body.Replace("]}", "," + JsonDocument.Parse(body).RootElement.GetProperty("tree")[0].GetRawText() + "]}")
        };
        responses[$"repos/harder/fixture/git/trees/{RootTree}"] = new(200, body);
        await Assert.ThrowsAsync<IOException>(() => Resolver().ResolveLatestAsync(Repository, "skills/demo", Token));
    }

    [Theory]
    [InlineData("HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n\r\n{}", 200)]
    [InlineData("HTTP/1.1 404 Not Found\nContent-Type: application/json\n\n{}", 404)]
    public void IncludedHttpStatusIsParsedWithoutInferringFromErrorWording(string output, int expected)
    {
        Assert.Equal(new(expected, "{}"), GhRevisionResolver.ParseResponse(output));
    }

    [Fact]
    public async Task PreCanceledResolutionMakesNoRequest()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => Resolver().ResolveLatestAsync(Repository, "skills/demo", new(true)));
        Assert.Empty(calls);
    }
}
