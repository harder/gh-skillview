using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class HelpOverlayTests
{
    [Fact]
    public void MarkdownBody_SplitsPrimaryAndAdvancedKeys()
    {
        var markdown = HelpOverlay.MarkdownBodyForTests;

        Assert.Contains("## Primary", markdown);
        Assert.Contains("## Advanced", markdown);
        Assert.DoesNotContain("## Search & filter", markdown, StringComparison.Ordinal);
        Assert.Contains("**I**", markdown);
        Assert.Contains("**A**", markdown);
        Assert.Contains("**Previous**", markdown);
        Assert.Contains("**Next**", markdown);
        Assert.Contains("submitted query and filters", markdown);
        Assert.Contains("Retry or Cancel", markdown);
        Assert.Contains("**F4**", markdown);
        Assert.DoesNotContain("2.95", markdown);
        Assert.Contains("**F5**", markdown);
    }
    [Fact]
    public void ContextualHelp_IsNotEmpty()
    {
        Assert.True(HelpOverlay.MarkdownBodyForTests.Length > 0);
        Assert.Contains("quit", HelpOverlay.MarkdownBodyForTests);
    }

    [Fact]
    public void ContextualHelp_ClarifiesEnterPreviewRequiresResultsFocus()
    {
        Assert.Contains("while the results list has focus", HelpOverlay.MarkdownBodyForTests);
    }

    [Fact]
    public void ContextualHelp_DocumentsDiscoverFilters()
    {
        Assert.Contains("Discover filters", HelpOverlay.MarkdownBodyForTests, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hidden directories", HelpOverlay.MarkdownBodyForTests, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContextualHelp_DocumentsRenderedMarkdownCopySupport()
    {
        Assert.Contains("Ctrl+C", HelpOverlay.MarkdownBodyForTests);
        Assert.Contains("Ctrl+C copies selected text", HelpOverlay.MarkdownBodyForTests, StringComparison.OrdinalIgnoreCase);
    }

}
