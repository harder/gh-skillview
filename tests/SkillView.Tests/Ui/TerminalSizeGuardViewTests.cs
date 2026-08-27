using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class TerminalSizeGuardViewTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    [InlineData(140, 42)]
    public void SupportedLayouts_DoNotShowResizeGuard(int width, int height)
    {
        Assert.False(TerminalSizeGuardView.IsTooSmall(width, height));
    }

    [Theory]
    [InlineData(79, 24)]
    [InlineData(80, 23)]
    [InlineData(60, 18)]
    public void CrampedLayouts_ShowResizeGuard(int width, int height)
    {
        Assert.True(TerminalSizeGuardView.IsTooSmall(width, height));
        Assert.Contains("Ctrl+Q quits", TerminalSizeGuardView.BuildMessage(width, height));
    }
}
