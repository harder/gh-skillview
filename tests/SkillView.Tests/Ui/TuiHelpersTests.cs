using SkillView.Inventory;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class TuiHelpersTests
{
    [Theory]
    [InlineData(null, 10, "")]
    [InlineData("", 10, "")]
    [InlineData("short", 10, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("this is a long string", 10, "this is a…")]
    [InlineData("ab", 2, "ab")]
    [InlineData("abc", 2, "a…")]
    public void Truncate_ClipsWithEllipsis(string? input, int max, string expected)
    {
        Assert.Equal(expected, TuiHelpers.Truncate(input, max));
    }

    [Theory]
    [InlineData(null, 3, "")]
    [InlineData("", 3, "")]
    [InlineData("/a/b/c", 3, "/a/b/c")]
    [InlineData("/home/user/.config/skills/copilot/my-skill", 3, "…/skills/copilot/my-skill")]
    [InlineData("/a/b", 3, "/a/b")]
    [InlineData("/a/b/c/d/e", 2, "…/d/e")]
    public void ShortenPath_KeepsLastSegments(string? input, int segments, string expected)
    {
        Assert.Equal(expected, TuiHelpers.ShortenPath(input, segments));
    }

    [Theory]
    [InlineData(CleanupClassifier.CandidateKind.Malformed, "malformed")]
    [InlineData(CleanupClassifier.CandidateKind.SourceOrphaned, "orphan")]
    [InlineData(CleanupClassifier.CandidateKind.Duplicate, "duplicate")]
    [InlineData(CleanupClassifier.CandidateKind.EmptyDirectory, "empty-dir")]
    [InlineData(CleanupClassifier.CandidateKind.BrokenSharedMapping, "broken-map")]
    [InlineData(CleanupClassifier.CandidateKind.HiddenNestedResidue, "hidden-nest")]
    [InlineData(CleanupClassifier.CandidateKind.BrokenSymlink, "broken-link")]
    [InlineData(CleanupClassifier.CandidateKind.OrphanCanonicalCopy, "orphan-copy")]
    public void ShortKind_ReturnsCompactLabel(CleanupClassifier.CandidateKind kind, string expected)
    {
        Assert.Equal(expected, TuiHelpers.ShortKind(kind));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  \n  \n  ", "")]
    [InlineData("error: not found", "error: not found")]
    [InlineData("  \nactual error line\nmore stuff", "actual error line")]
    [InlineData("a very long error message that exceeds the default maximum length for inline status display in the TUI", "a very long error message that exceeds the default maximum …")]
    public void ErrorSnippet_ExtractsFirstLine(string? input, string expected)
    {
        Assert.Equal(expected, TuiHelpers.ErrorSnippet(input));
    }

    [Fact]
    public void ErrorSnippet_RespectsCustomMaxLen()
    {
        Assert.Equal("abcdefghi…", TuiHelpers.ErrorSnippet("abcdefghijklmnop", maxLen: 10));
    }

    [Fact]
    public void HelpText_IsNotEmpty()
    {
        Assert.True(TuiHelpers.HelpText.Length > 0);
        Assert.Contains("quit", TuiHelpers.HelpText);
    }

    [Fact]
    public void WelcomeHint_IsNotEmpty()
    {
        Assert.True(TuiHelpers.WelcomeHint.Length > 0);
        Assert.Contains("help", TuiHelpers.WelcomeHint);
    }
}
