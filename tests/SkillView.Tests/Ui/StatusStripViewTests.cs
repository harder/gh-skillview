using SkillView.Bootstrapping;
using SkillView.Ui;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Xunit;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace SkillView.Tests.Ui;

public sealed class StatusStripViewTests
{
    [Fact]
    public void SetBusy_ControlsInlineSpinnerState()
    {
        var view = new StatusStripView();

        view.SetBusy(true);
        Assert.True(view.IsBusyForTests);

        view.SetBusy(false);
        Assert.False(view.IsBusyForTests);
    }

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

    // ── Bug 2 regression: exact-fit should be included, not dropped ──────────

    [Fact]
    public void TruncateHintsForTests_ExactFit_IncludesHint()
    {
        // "q:Quit" = 6 chars; available = 6 — should be included, not dropped.
        var hints = new[] { new StatusHint("q", "Quit") };

        var result = StatusStripView.TruncateHintsForTests(hints, availableWidth: 6);

        Assert.Single(result);
        Assert.Equal("q", result[0].Key);
    }

    [Fact]
    public void TruncateHintsForTests_OneLessThanExact_DropsHint()
    {
        // "q:Quit" = 6 chars; available = 5 — hint must not fit.
        var hints = new[] { new StatusHint("q", "Quit") };

        var result = StatusStripView.TruncateHintsForTests(hints, availableWidth: 5);

        Assert.Empty(result);
    }

    // ── Bug 1 regression: ComputeHintLeftEdge must account for center text ──

    [Fact]
    public void ComputeHintLeftEdge_NoStatusText_ReturnsLeftBadgeEnd()
    {
        var left = StatusStripView.ComputeHintLeftEdge(width: 80, leftBadgeEnd: 5, statusText: "");

        Assert.Equal(5, left);
    }

    [Fact]
    public void ComputeHintLeftEdge_WithStatusText_ReturnsEndOfCenterRegion()
    {
        // width=40, leftBadgeEnd=0, statusText="Hello World" (11 chars)
        // centerX = max(0, 40/4=10) = 10; centerEnd = 10 + min(11, 20) = 21
        var left = StatusStripView.ComputeHintLeftEdge(width: 40, leftBadgeEnd: 0, statusText: "Hello World");

        Assert.Equal(21, left);
    }

    [Fact]
    public void ComputeHintLeftEdge_LeftBadgesExceedCenterEnd_ReturnsLeftBadgeEnd()
    {
        // width=40, leftBadgeEnd=25, statusText="Hi" (2 chars)
        // centerX = max(25, 10) = 25; centerEnd = 25 + 2 = 27
        // leftEdge = max(25, 27) = 27
        var left = StatusStripView.ComputeHintLeftEdge(width: 40, leftBadgeEnd: 25, statusText: "Hi");

        Assert.Equal(27, left);
    }

    [Fact]
    public void ComputeHintLeftEdge_LongStatusText_ClampedToHalfWidth()
    {
        // width=20, leftBadgeEnd=0, statusText=30 chars (exceeds half-width of 10)
        // centerX = max(0, 5) = 5; centerEnd = 5 + min(30, 10) = 15
        var longStatus = new string('x', 30);

        var left = StatusStripView.ComputeHintLeftEdge(width: 20, leftBadgeEnd: 0, statusText: longStatus);

        Assert.Equal(15, left);
    }

    // ── Bug 3 regression: theme helper returns sensible attributes ───────────

    [Fact]
    public void GetStatusStripAttributes_DefaultTheme_UsesWingetTuiSurfaceBackground()
    {
        var prev = TuiHelpers.CurrentTheme;
        try
        {
            TuiHelpers.SetTheme(AppTheme.Default);
            var (strip, _, _) = TuiHelpers.GetStatusStripAttributes();
            Assert.Equal(WingetTuiTheme.Surface, strip.Background);
        }
        finally
        {
            TuiHelpers.SetTheme(prev);
        }
    }

    [Fact]
    public void GetStatusStripAttributes_HighContrastTheme_UsesBlackBackground()
    {
        var prev = TuiHelpers.CurrentTheme;
        try
        {
            TuiHelpers.SetTheme(AppTheme.HighContrast);
            var (strip, _, _) = TuiHelpers.GetStatusStripAttributes();
            Assert.Equal(new Attribute(StandardColor.Gray, StandardColor.Black).Background, strip.Background);
        }
        finally
        {
            TuiHelpers.SetTheme(prev);
        }
    }
}
