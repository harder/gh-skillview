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

    [Theory]
    [InlineData("`example\n[hidden](missing.md)\nend`\n")]
    [InlineData("``example\n[hidden][inside]\n[inside]: missing.md\nend``\n")]
    [InlineData("`example\n[inside]: missing.md\nend\\`\n")]
    public void MultilineCodeExcludesItsParagraphFromBothDefinitionAndReferencePasses(string excluded)
    {
        var result = MarkdownPackageReferences.Scan(excluded + "\n[inside]\n[good](real.md)\n", 50, Token);
        Assert.Equal("real.md", Assert.Single(result.References).Target);
        Assert.NotEmpty(result.Reasons);
    }

    [Theory]
    [InlineData("[a multiline\nlabel](missing.md)\n")]
    [InlineData("[a multiline\n[inside]: missing.md\nlabel](other.md)\n")]
    public void MultilineLabelsAreUnknownAndCannotPublishDefinitionsFromTheirExcludedParagraph(string excluded)
    {
        var result = MarkdownPackageReferences.Scan(excluded + "\n[inside]\n[good](real.md)\n", 50, Token);
        Assert.Equal("real.md", Assert.Single(result.References).Target);
        Assert.NotEmpty(result.Reasons);
    }

    [Theory]
    [InlineData("script")]
    [InlineData("STYLE")]
    [InlineData("pre")]
    [InlineData("textarea")]
    public void RawHtmlCodeBlocksContinueAcrossBlankLinesUntilATypeOneTerminator(string tag)
    {
        // CommonMark type 1 permits any of its four end tags, not only the opening tag.
        var result = MarkdownPackageReferences.Scan($"<{tag}>\n\n[hidden](missing.md)\n[inside]: missing-definition.md\n</pRe>\n[inside]\n[good](real.md)\n", 50, Token);
        Assert.Equal("real.md", Assert.Single(result.References).Target);
        Assert.NotEmpty(result.Reasons);
    }

    [Theory]
    [InlineData("<script><!-- comment -->\n\n[hidden](missing.md)\n</script>\n")]
    [InlineData("<style>same-line closure</STYLE>\n")]
    public void RawHtmlStartTakesPrecedenceOverCommentsAndCanEndOnItsOpeningLine(string excluded)
    {
        var result = MarkdownPackageReferences.Scan(excluded + "[good](real.md)\n", 50, Token);
        Assert.Equal("real.md", Assert.Single(result.References).Target);
        Assert.NotEmpty(result.Reasons);
    }

    [Theory]
    [InlineData("- ```md\n  [inside]: missing.md\n  [hidden](other.md)\n  ```\n")]
    [InlineData("1. ~~~md\n   [inside]: missing.md\n   [hidden](other.md)\n   ~~~\n")]
    [InlineData("12. ```md\n    [inside]: missing.md\n    [hidden](other.md)\n    ```\n")]
    [InlineData("- - ```md\n    [inside]: missing.md\n    [hidden](other.md)\n    ```\n")]
    [InlineData("- ```md\n\t[inside]: missing.md\n\t[hidden](other.md)\n\t```\n")]
    public void ListContainedFencesExcludeCodeAndDefinitionsWithoutHidingTheNextParagraph(string excluded)
    {
        var result = MarkdownPackageReferences.Scan(excluded + "\n[inside]\n[good](real.md)\n", 50, Token);
        Assert.Equal("real.md", Assert.Single(result.References).Target);
        Assert.NotEmpty(result.Reasons);
    }

    [Theory]
    [InlineData("[a](assets/a&amp;b.png)\n")]
    [InlineData("[a](assets/&#97;.png)\n")]
    [InlineData("[a](assets/&#x61;.png)\n")]
    [InlineData("[a][entity]\n[entity]: assets/a&amp;b.png\n")]
    public void UnescapedDestinationAmpersandsAreUnsupportedWithoutInventingLiteralTargets(string excluded)
    {
        var result = MarkdownPackageReferences.Scan(excluded + "[good](real.md)\n", 50, Token);
        Assert.Equal("real.md", Assert.Single(result.References).Target);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void EscapedAmpersandStaysLiteralAndCodePathsNeverDecodeEntities()
    {
        var result = MarkdownPackageReferences.Scan("[a](assets/a\\&amp;b.png) `assets/a&amp;b.png`\n", 50, Token);
        Assert.Equal("assets/a&amp;b.png", Assert.Single(result.References).Target);
        Assert.Empty(result.Reasons);
        // The existing standalone-code-path grammar excludes shell punctuation, including '&'.
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
