using SkillView.PackageChecking;
using Xunit;

namespace SkillView.Tests.PackageChecking;

public sealed class MarkdownPackageReferencesTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ExplicitLinksImagesDefinitionsAndStandaloneCodePathsRetainLineAndTarget()
    {
        var result = MarkdownPackageReferences.Scan("[Guide](references/guide.md)\n![Image](<assets/a b.png> \"Title\")\n[Run][script]\n[script]: scripts/check.py\n`./scripts/tool.py`\n[script]\n", 50, Token, 4);
        Assert.Empty(result.Reasons);
        Assert.Equal(new[] { "references/guide.md", "assets/a b.png", "scripts/check.py", "./scripts/tool.py", "scripts/check.py" }, result.References.Select(reference => reference.Target));
        Assert.Equal(new[] { 5, 6, 7, 9, 10 }, result.References.Select(reference => reference.Line));
        Assert.False(result.References[3].DecodeUrl);
    }

    [Fact]
    public void CodeBlocksEscapesAndCommandsAreNeverInterpretedAsPackageDependencies()
    {
        var text = "```sh\n[Missing](scripts/no.py)\n```\n~~~\n`assets/no.png`\n~~~\n\\[Ignore](missing.md)\n`python scripts/no.py` `scripts/a.py --flag` `assets/$NAME.png`\n[ordinary prose]\n";
        var result = MarkdownPackageReferences.Scan(text, 50, Token);
        Assert.Empty(result.References);
        Assert.Empty(result.Reasons);
    }

    [Theory]
    [InlineData("[a](scripts/missing.py (unsupported title))")]
    [InlineData("[a](scripts/missing.py\nmore)")]
    [InlineData("`scripts/missing.py``")]
    [InlineData("<img src=\"assets/missing.png\">")]
    public void UnrecognizedSyntaxIsPartialWithoutFabricatingAMissingTarget(string text)
    {
        var result = MarkdownPackageReferences.Scan(text, 50, Token);
        Assert.Empty(result.References);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void UnsupportedOrDuplicateDefinitionsDoNotBecomeKnownTargets()
    {
        var result = MarkdownPackageReferences.Scan("[a][x]\n[x]: first.md\n[x]: second.md\n[b][y]\n[y]: third.md (unsupported title)\n", 50, Token);
        Assert.Empty(result.References);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void UnusedDefinitionsAreOutsideTheAdvertisedDependencyScope()
    {
        var result = MarkdownPackageReferences.Scan("[unused]: references/missing.md\nOrdinary prose.\n", 50, Token);
        Assert.Empty(result.References); Assert.Empty(result.Reasons);
        Assert.Contains("Unused definitions", PackageCheckReport.ReferenceScope);
    }

    [Fact]
    public void UnsupportedHtmlAndQuotedCodeCannotInventMissingReferenceTargets()
    {
        var result = MarkdownPackageReferences.Scan("Before <!-- [no](missing-a.md) -->\n<!--\n[no](missing-b.md)\n-->\n<script>\n[no](missing-c.md)\n</script>\n\n> ```\n> [no](missing-d.md)\n> ```\n[yes](real.md)\n", 50, Token);
        Assert.Equal("real.md", Assert.Single(result.References).Target);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void ReferenceBudgetStopsAtItsBoundAndMarksIncompleteCoverage()
    {
        var result = MarkdownPackageReferences.Scan("[a](a.md) [b](b.md) [c](c.md)", 2, Token);
        Assert.Equal(2, result.References.Length);
        Assert.Contains(result.Reasons, reason => reason.Contains("limit", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsingHonorsPreCanceledToken()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => MarkdownPackageReferences.Scan("[a](a.md)", 50, source.Token));
    }
}
