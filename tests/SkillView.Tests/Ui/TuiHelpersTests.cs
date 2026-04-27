using SkillView.Inventory;
using SkillView.Ui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.Views;
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

    [Fact]
    public void Truncate_RespectsDisplayWidth_ForWideGlyphs()
    {
        var result = TuiHelpers.Truncate("当用户明确要求使用", 8);

        Assert.EndsWith("…", result);
        Assert.True(result.GetColumns() <= 8);
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
    public void HelpText_ClarifiesEnterPreviewRequiresResultsFocus()
    {
        Assert.Contains("when results are focused", TuiHelpers.HelpText);
    }

    [Fact]
    public void WelcomeHint_IsNotEmpty()
    {
        Assert.True(TuiHelpers.WelcomeHint.Length > 0);
        Assert.Contains("help", TuiHelpers.WelcomeHint);
    }

    [Theory]
    [InlineData("copilot", "C")]
    [InlineData("github-copilot", "C")]
    [InlineData("claude-code", "⟁")]
    [InlineData("gemini-cli", "✦")]
    public void AgentIcon_SupportsCurrentAgentIds(string agentId, string expected)
    {
        Assert.Equal(expected, TuiHelpers.AgentIcon(agentId));
    }

    [Theory]
    [InlineData('v')]
    [InlineData('V')]
    [InlineData('p')]
    [InlineData('P')]
    public void IsPreviewKey_AcceptsPreviewRunes(char key)
    {
        Assert.True(TuiHelpers.IsPreviewKey(new Key(key)));
    }

    [Fact]
    public void IsPreviewKey_AcceptsEnter()
    {
        Assert.True(TuiHelpers.IsPreviewKey(new Key(KeyCode.Enter)));
    }

    [Fact]
    public void IsPreviewKey_RejectsOtherKeys()
    {
        Assert.False(TuiHelpers.IsPreviewKey(new Key('x')));
    }

    [Fact]
    public void ApplyScheme_SetsSchemeName_OnProvidedViews()
    {
        var label = new Label();
        var field = new TextField();

        TuiHelpers.ApplyScheme("Base", label, field, null);

        Assert.Equal("Base", label.SchemeName);
        Assert.Equal("Base", field.SchemeName);
    }

    [Fact]
    public void ConfigureReadOnlyPane_SetsReadableDefaults()
    {
        var view = new TextView
        {
            ReadOnly = false,
            WordWrap = false,
        };

        TuiHelpers.ConfigureReadOnlyPane(view, "Dialog");

        Assert.True(view.ReadOnly);
        Assert.True(view.WordWrap);
        Assert.Equal("Dialog", view.SchemeName);
    }

    [Fact]
    public void NoSearchMatcher_RejectsAllKeys()
    {
        var matcher = NoSearchMatcher.Instance;
        Assert.False(matcher.IsCompatibleKey(new Key('a')));
        Assert.False(matcher.IsCompatibleKey(new Key('z')));
        Assert.False(matcher.IsCompatibleKey(new Key('1')));
        Assert.False(matcher.IsCompatibleKey(new Key(KeyCode.Enter)));
    }

    [Fact]
    public void NoSearchMatcher_NeverMatches()
    {
        var matcher = NoSearchMatcher.Instance;
        Assert.False(matcher.IsMatch("test", "test"));
        Assert.False(matcher.IsMatch("a", "abc"));
    }
}
