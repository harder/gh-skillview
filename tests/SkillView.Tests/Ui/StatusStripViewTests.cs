using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class StatusStripViewTests
{
    [Fact]
    public void TruncateHintsForTests_PreservesRightmostPairs_WhenSpaceIsTight()
    {
        var hints = new[]
        {
            new StatusHint("q", "Quit"),
            new StatusHint("i", "Install"),
            new StatusHint("u", "Update"),
            new StatusHint("r", "Remove"),
        };

        // Very narrow — only rightmost pair(s) should survive.
        var result = StatusStripView.TruncateHintsForTests(hints, availableWidth: 12);

        Assert.NotEmpty(result);
        // The rightmost hint must always be present.
        Assert.Equal("r", result[^1].Key);
    }

    [Fact]
    public void TruncateHintsForTests_ReturnsAllPairs_WhenSpaceIsAdequate()
    {
        var hints = new[]
        {
            new StatusHint("q", "Quit"),
            new StatusHint("i", "Install"),
        };

        var result = StatusStripView.TruncateHintsForTests(hints, availableWidth: 80);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void TruncateHintsForTests_ReturnsEmpty_WhenNoHints()
    {
        var result = StatusStripView.TruncateHintsForTests([], availableWidth: 80);

        Assert.Empty(result);
    }

    [Fact]
    public void TruncateHintsForTests_ZeroWidth_ReturnsEmpty()
    {
        var hints = new[] { new StatusHint("q", "Quit") };

        var result = StatusStripView.TruncateHintsForTests(hints, availableWidth: 0);

        Assert.Empty(result);
    }
}
