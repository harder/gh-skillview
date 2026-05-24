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
    }
}
